using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 追蹤器的還原點。store 的寫入方法直接動消費端的 DbContext(常是長壽、共用的),
/// 驗證或存檔失敗時要把自己動過的項目放回呼叫前的樣子:否則被擋下的變更會留在追蹤器裡,
/// 在消費端下一次 SaveChanges 落地,或讓之後每一次存檔都撞同一個錯。
/// 只還原記下來的項目;消費端自己在呼叫前就有的未存變更,記下時是什麼樣子就還原成什麼樣子。
/// </summary>
internal sealed class TrackerSnapshot
{
    private readonly DbContext _db;
    private readonly List<Item> _items = new();
    private readonly HashSet<object> _captured = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _added = new();
    private readonly HashSet<object> _owned = new(ReferenceEqualityComparer.Instance);
    private List<(EntityEntry Entry, EntityState State)>? _pending;

    private sealed record Item(EntityEntry Entry, EntityState State, PropertyValues? Values, HashSet<string>? Modified);

    public TrackerSnapshot(DbContext db) => _db = db;

    /// <summary>記下一個項目目前的狀態;<paramref name="withValues"/> 時連目前值與哪些欄位標成已修改一起記。</summary>
    public void Capture(EntityEntry entry, bool withValues = true)
    {
        if (!_captured.Add(entry.Entity)) return;
        _items.Add(new Item(
            entry,
            entry.State,
            withValues ? entry.CurrentValues.Clone() : null,
            withValues ? new HashSet<string>(entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)) : null));
    }

    /// <summary>記下 store 自己新加進追蹤器的實體;還原時 detach。</summary>
    public void Added(object entity) => _added.Add(entity);

    /// <summary>標明這個實體是這次呼叫要寫的對象(例如 UpdateAsync 的那個標籤);並發衝突時一律重讀。</summary>
    public void Own(object entity) => _owned.Add(entity);

    /// <summary>
    /// 並發衝突之後能不能把這個實體重讀成資料庫的值:這次呼叫的對象、store 新加的,
    /// 或記下時沒有未存變更(Unchanged)的才行。消費端自己改過、還沒存的項目不重讀,免得替他丟掉修改。
    /// </summary>
    public bool MayReload(object entity)
        => _owned.Contains(entity)
           || _added.Any(a => ReferenceEquals(a, entity))
           || _items.Any(i => ReferenceEquals(i.Entry.Entity, entity) && i.State == EntityState.Unchanged);

    /// <summary>
    /// 存檔前呼叫:記下 store 這次要寫出去的項目(新加的、這次呼叫的對象、狀態被這次呼叫改掉的)目前的待存狀態。
    /// </summary>
    public void ArmForSave()
    {
        var pending = new List<(EntityEntry Entry, EntityState State)>();
        void Add(EntityEntry entry)
        {
            if ((entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                && !pending.Any(p => ReferenceEquals(p.Entry.Entity, entry.Entity)))
                pending.Add((entry, entry.State));
        }

        foreach (var entity in _added)
            Add(_db.Entry(entity));
        foreach (var entity in _owned)
            Add(_db.Entry(entity));
        foreach (var item in _items.Where(i => i.Entry.State != i.State))
            Add(item.Entry);
        _pending = pending;
    }

    /// <summary>
    /// 這次存檔是否已被資料庫接受。EF 提交之後會先把寫出去的項目標成 Unchanged(刪除的則 detach),
    /// 才跑 SavedChanges 攔截器之類的後續步驟——在那裡拋的例外不代表沒寫進去,這時還原只會把追蹤器弄得和資料庫不一致
    /// (把已刪掉的放回去、把已提交的值改回舊值)。
    /// </summary>
    public bool SaveAccepted
        => _pending is { Count: > 0 } && _pending.All(p => p.Entry.State != p.State);

    /// <summary>
    /// 把記下的項目裡被改掉的主鍵直接寫回 CLR 屬性。lambda 在偵測變更之前改了主鍵的話,EF 還不知道,
    /// 這時偵測變更或經 EF 把主鍵設回去都會拋「主鍵不可修改」;繞過 EF 寫回去,追蹤器裡的主鍵本來就沒變。
    /// </summary>
    public void RestoreKeys()
    {
        foreach (var item in _items)
        {
            if (item.Values is null) continue;
            var key = item.Entry.Metadata.FindPrimaryKey();
            if (key is null) continue;
            foreach (var property in key.Properties)
            {
                var captured = item.Values[property];
                if (Equals(captured, item.Entry.Property(property.Name).CurrentValue)) continue;
                if (property.PropertyInfo is { CanWrite: true } info)
                    info.SetValue(item.Entry.Entity, captured);
                else
                    property.FieldInfo?.SetValue(item.Entry.Entity, captured);
            }
        }
    }

    /// <summary>
    /// 還原:store 新加的實體 detach;<paramref name="detachUncapturedOf"/> 型別裡沒被記下的項目
    /// (例如 UpdateAsync 的 lambda 經導覽屬性帶進追蹤器的新標籤)也 detach;
    /// 記下的項目依記錄順序放回當時的值、已修改欄位與狀態。
    /// </summary>
    public void Restore(Type? detachUncapturedOf = null)
    {
        var tracker = _db.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;   // 還原途中不讓 EF 自己掃描:掃到還原一半的狀態只會更亂
        try
        {
            foreach (var entity in _added)
            {
                var entry = _db.Entry(entity);
                if (entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;
            }

            if (detachUncapturedOf is not null)
            {
                var strays = tracker.Entries()
                    .Where(e => detachUncapturedOf.IsInstanceOfType(e.Entity) && !_captured.Contains(e.Entity))
                    .ToList();
                foreach (var entry in strays)
                    entry.State = EntityState.Detached;
            }

            foreach (var item in _items)
            {
                var entry = item.Entry;
                if (entry.State == EntityState.Detached && item.State != EntityState.Detached)
                    entry.State = item.State;   // 例如被 Remove 掉的 Added 項目:EF 對它 Remove 是直接 detach

                // 外鍵改回去時,EF 會順著把 Parent / Children 這類導覽屬性一起修正回來
                if (item.Values is not null)
                    entry.CurrentValues.SetValues(item.Values);

                if (item.State == EntityState.Modified && item.Modified is not null)
                {
                    foreach (var property in entry.Properties)
                        property.IsModified = item.Modified.Contains(property.Metadata.Name);
                }
                else if (entry.State != item.State)
                {
                    entry.State = item.State;
                }
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }
}
