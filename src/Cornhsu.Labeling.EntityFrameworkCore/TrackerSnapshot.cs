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
    private readonly HashSet<object> _capturedUnchanged = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _added = new();
    private readonly HashSet<object> _addedSet = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _owned = new(ReferenceEqualityComparer.Instance);
    private List<(EntityEntry Entry, EntityState State)>? _pending;

    private sealed record Item(EntityEntry Entry, EntityState State, PropertyValues? Values, HashSet<string>? Modified);

    public TrackerSnapshot(DbContext db) => _db = db;

    /// <summary>記下一個項目目前的狀態;<paramref name="withValues"/> 時連目前值與哪些欄位標成已修改一起記。</summary>
    public void Capture(EntityEntry entry, bool withValues = true)
    {
        if (!_captured.Add(entry.Entity)) return;
        if (entry.State == EntityState.Unchanged)
            _capturedUnchanged.Add(entry.Entity);
        _items.Add(new Item(
            entry,
            entry.State,
            withValues ? entry.CurrentValues.Clone() : null,
            withValues ? new HashSet<string>(entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)) : null));
    }

    /// <summary>記下 store 自己新加進追蹤器的實體;還原時 detach。</summary>
    public void Added(object entity)
    {
        if (_addedSet.Add(entity))
            _added.Add(entity);
    }

    /// <summary>標明這個實體是這次呼叫要寫的對象(例如 UpdateAsync 的那個標籤);並發衝突時一律重讀。</summary>
    public void Own(object entity) => _owned.Add(entity);

    /// <summary>
    /// 並發衝突之後能不能把這個實體重讀成資料庫的值:這次呼叫的對象、store 新加的,
    /// 或記下時沒有未存變更(Unchanged)的才行。消費端自己改過、還沒存的項目不重讀,免得替他丟掉修改。
    /// </summary>
    public bool MayReload(object entity)
        => _owned.Contains(entity) || _addedSet.Contains(entity) || _capturedUnchanged.Contains(entity);

    /// <summary>
    /// 存檔前呼叫:記下 store 這次要寫出去的項目(新加的、這次呼叫的對象、狀態被這次呼叫改掉的)目前的待存狀態。
    /// </summary>
    public void ArmForSave()
    {
        var pending = new List<(EntityEntry Entry, EntityState State)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);   // 去重要 O(1):批次貼標一次就是幾萬條連結
        void Add(EntityEntry entry)
        {
            if ((entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                && seen.Add(entry.Entity))
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
    /// 判斷看的是「每一個都變成 Unchanged 或 detached」,不是「狀態變了」:SavingChanges 攔截器可能在送出前改狀態
    /// (例如 soft-delete 把刪除改成修改),之後資料庫才失敗,那不算接受。
    /// 消費端自己開了交易(顯式或 System.Transactions 的環境交易)時一律不算:EF 接受了也還沒真的提交,
    /// 消費端可能接著 rollback,這時留著「已接受」的狀態,追蹤器就和資料庫對不上(例如以為連結已存在、重貼時直接略過)。
    /// </summary>
    public bool SaveAccepted
        => _pending is { Count: > 0 }
           && _db.Database.CurrentTransaction is null
           && System.Transactions.Transaction.Current is null
           && _pending.All(p => p.Entry.State is EntityState.Unchanged or EntityState.Detached);

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
    /// 還原:store 新加的實體 detach;<paramref name="detachUncapturedLabels"/> 時,沒被記下的標籤
    /// (UpdateAsync 的 lambda 經導覽屬性帶進追蹤器的新標籤)也 detach,並從其他標籤的 Children 裡拿掉;
    /// 記下的項目依記錄順序放回當時的值、已修改欄位與狀態。
    /// </summary>
    public void Restore(bool detachUncapturedLabels = false)
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

            var strays = new List<Label>();
            if (detachUncapturedLabels)
            {
                foreach (var entry in tracker.Entries<Label>().Where(e => !_captured.Contains(e.Entity)).ToList())
                {
                    strays.Add(entry.Entity);
                    entry.State = EntityState.Detached;
                }
            }

            // 每一項各自還原:某一項失敗(例如外鍵改回去時,EF 的導覽修正碰到消費端不准改的集合)
            // 不能擋住其他項目,也不能蓋掉呼叫端要拿到的原本那個例外
            foreach (var item in _items)
            {
                try
                {
                    RestoreItem(item);
                }
                catch (Exception)
                {
                    // 盡力而為:這一項停在目前的樣子
                }
            }

            // detach 不會把它從別的標籤的 Children(CLR 集合)裡拿掉:留著的話畫面上看得到一個幽靈子標籤,
            // 之後的偵測也可能把它當成新加入的再追蹤回來。消費端的集合可能拒絕修改(綁畫面的集合、陣列),
            // 每次 Remove 各自吞掉失敗,而且放在最後做,不影響上面真正要緊的還原
            if (strays.Count > 0)
                RemoveFromChildren(tracker, new HashSet<Label>(strays, ReferenceEqualityComparer.Instance));
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private static void RestoreItem(Item item)
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

    private static void RemoveFromChildren(ChangeTracker tracker, HashSet<Label> strays)
    {
        foreach (var entry in tracker.Entries<Label>())
        {
            if (entry.Entity.Children is not { } children) continue;
            foreach (var stray in children.Where(strays.Contains).ToList())
            {
                try
                {
                    children.Remove(stray);
                }
                catch (Exception)
                {
                    // 集合不准改:留著,只差畫面上多一個已 detach 的子標籤
                }
            }
        }
    }
}
