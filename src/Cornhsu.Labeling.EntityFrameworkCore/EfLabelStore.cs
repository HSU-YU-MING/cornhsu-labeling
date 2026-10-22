using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cornhsu.Labeling.EntityFrameworkCore;

internal sealed class EfLabelStore<TContext> : ILabelStore where TContext : DbContext
{
    private readonly TContext _db;
    private readonly LabelRegistry _registry;
    private readonly ILogger _logger;

    public EfLabelStore(TContext db, LabelRegistry registry, ILogger<EfLabelStore<TContext>>? logger = null)
        => (_db, _registry, _logger) = (db, registry, logger ?? NullLogger<EfLabelStore<TContext>>.Instance);

    // ---- 標籤 CRUD ----

    public async Task<Label> CreateAsync(string name, string? color = null, string? icon = null, Guid? parentId = null, CancellationToken ct = default)
    {
        var normalized = Normalize(name)
            ?? throw new ArgumentException("label name must not be blank.", nameof(name));
        ValidateNameLength(normalized);

        var existing = await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == normalized, ct).ConfigureAwait(false);
        if (existing is not null)
            throw new InvalidOperationException($"label '{normalized}' already exists (Id: {existing.Id}).");

        if (parentId is not null
            && !await _db.Set<Label>().AnyAsync(l => l.Id == parentId, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"parent label {parentId} does not exist.");

        var label = new Label
        {
            Id = Guid.NewGuid(),
            Name = normalized,
            Color = color,
            Icon = icon,
            ParentId = parentId,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid(),
        };
        var undo = new TrackerSnapshot(_db);
        _db.Set<Label>().Add(label);
        undo.Added(label);
        await SaveOrRestoreAsync(undo, null, ct).ConfigureAwait(false);
        _logger.LogDebug("created label '{Name}' (Id: {Id})", label.Name, label.Id);
        return label;
    }

    public Task<Label?> FindAsync(string name, CancellationToken ct = default)
    {
        var normalized = Normalize(name);
        return normalized is null
            ? Task.FromResult<Label?>(null)
            : _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == normalized, ct);
    }

    public async Task<IReadOnlyList<Label>> GetAllAsync(CancellationToken ct = default)
        => await _db.Set<Label>()
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task RenameAsync(Guid labelId, string newName, CancellationToken ct = default)
    {
        var normalized = Normalize(newName)
            ?? throw new ArgumentException("label name must not be blank.", nameof(newName));
        ValidateNameLength(normalized);

        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"label {labelId} not found.");

        var taken = await _db.Set<Label>().AnyAsync(l => l.Name == normalized && l.Id != labelId, ct).ConfigureAwait(false);
        if (taken)
            throw new InvalidOperationException($"label name '{normalized}' is already taken.");

        // 連結存的是 Id,改名是一次 O(1) 的 UPDATE,不需要任何 cascade
        var undo = new TrackerSnapshot(_db);
        undo.Capture(_db.Entry(label));
        undo.Own(label);
        label.Name = normalized;
        label.ConcurrencyStamp = Guid.NewGuid();   // 輪換並發戳記
        _db.Entry(label).DetectChanges();          // 消費端關掉自動偵測變更時,SaveChanges 不會自己發現這兩個欄位改了
        await SaveOrRestoreAsync(undo, null, ct).ConfigureAwait(false);
    }

    public async Task<Label> UpdateAsync(Guid labelId, Action<Label> update, CancellationToken ct = default)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));

        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"label {labelId} not found.");

        // lambda 改的是追蹤中的實體,還可能經導覽屬性動到別的標籤。先記下追蹤器裡每個標籤呼叫前的樣子,
        // 驗證或存檔失敗時整批還原——否則被擋下的值會留在追蹤器,在下一次 SaveChanges 落地。
        DetectLabelChanges();
        var snapshot = new TrackerSnapshot(_db);
        var before = new Dictionary<Label, (Guid? ParentId, string Name)>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _db.ChangeTracker.Entries<Label>())
        {
            snapshot.Capture(entry);
            before[entry.Entity] = (entry.Entity.ParentId, entry.Entity.Name);
        }
        snapshot.Own(label);

        // 名稱要不要驗證,比的是資料庫裡的名稱:存檔會寫入的名稱只要跟它不同就要驗
        var storedName = _db.Entry(label).Property(l => l.Name).OriginalValue;

        try
        {
            update(label);

            if (label.Id != labelId)
                throw new InvalidOperationException("a label's Id must not be changed inside UpdateAsync.");

            // 導覽屬性(Parent、Children)的變更要等偵測變更才會寫回 ParentId;不先做,循環檢查看到的是舊值
            DetectLabelChanges();

            // 改名走與 RenameAsync 相同的規則(正規化 + 唯一性)
            if (!string.Equals(label.Name, storedName, StringComparison.Ordinal))
                await NormalizeNameForSaveAsync(label, nameof(update), ct).ConfigureAwait(false);

            // lambda 經導覽屬性改了名稱、或帶進來的其他標籤,同一次存檔會一起寫出去:一樣驗,不能從這裡繞過
            var tracked = _db.ChangeTracker.Entries<Label>().Select(e => e.Entity).ToList();
            foreach (var other in tracked)
            {
                if (ReferenceEquals(other, label)) continue;
                if (before.TryGetValue(other, out var was)
                        ? string.Equals(other.Name, was.Name, StringComparison.Ordinal)   // lambda 沒改它的名稱
                        : _db.Entry(other).State == EntityState.Unchanged)                 // lambda 只是載入(讀)進來的既有標籤
                    continue;   // 不驗也不改寫:舊資料的名稱可能本來就不合現行規則,沒有人要寫它
                await NormalizeNameForSaveAsync(other, nameof(update), ct).ConfigureAwait(false);
            }

            // 要檢查的:這個標籤本身(有父標籤時),以及 lambda 換了父標籤的每一個標籤
            var moved = tracked
                .Where(l => ReferenceEquals(l, label)
                    || !before.TryGetValue(l, out var was)
                    || was.ParentId != l.ParentId)
                .ToList();
            await EnsureNoCycleAsync(moved, ct).ConfigureAwait(false);

            label.ConcurrencyStamp = Guid.NewGuid();   // 輪換並發戳記
            DetectLabelChanges();                      // 消費端關掉自動偵測變更時,SaveChanges 不會自己發現正規化後的名稱與新戳記
            snapshot.ArmForSave();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return label;
        }
        catch (Exception ex)
        {
            if (snapshot.SaveAccepted)
                throw;   // 已經提交,例外來自提交之後的步驟:寫進去的就是寫進去了,不還原

            // lambda 可能在上面那次偵測變更之前就失敗(自己拋例外,或改了 Id 被擋下):經導覽屬性做的修改
            // (l.Parent = x、x.Children.Add(l))那時還沒寫回外鍵,還原碰不到,下一次存檔照樣落地。
            // 先把被改掉的主鍵放回去(否則偵測會因主鍵被改而拋),再盡力偵測一次,讓它們變成還原得掉的外鍵值
            snapshot.RestoreKeys();
            DetectLabelChangesBestEffort();
            snapshot.Restore(detachUncapturedLabels: true);
            await ReloadConflictsAsync(ex, snapshot).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DeleteAsync(Guid labelId, CancellationToken ct = default)
    {
        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"label {labelId} not found.");

        var hasChildren = await _db.Set<Label>().AnyAsync(l => l.ParentId == labelId, ct).ConfigureAwait(false);
        if (hasChildren)
            throw new InvalidOperationException(
                $"label '{label.Name}' still has children; delete or move them first. (The database also blocks this via a Restrict foreign key.)");

        // Remove 的當下 EF 就會 cascade:追蹤中的連結標成刪除、參照它的可為 null 外鍵設成 null。
        // 先記下:直接參照這個標籤的項目連值一起記,其餘只記狀態(多層 cascade 也放得回去)
        var undo = new TrackerSnapshot(_db);
        undo.Capture(_db.Entry(label));
        undo.Own(label);
        var tracked = _db.ChangeTracker.Entries().ToList();
        foreach (var entry in tracked.Where(e => ReferencesLabel(e, labelId)))
            undo.Capture(entry);
        foreach (var entry in tracked)
            undo.Capture(entry, withValues: false);

        // 各型別的連結由 cascade delete 自動清除
        await SaveOrRestoreAsync(undo, () => { _db.Set<Label>().Remove(label); return Task.CompletedTask; }, ct).ConfigureAwait(false);
        _logger.LogDebug("deleted label '{Name}' (Id: {Id})", label.Name, labelId);
    }

    // ---- 貼標 / 撕標 ----

    public Task AttachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable
        => AttachAsync(entity, (IEnumerable<string>)labelNames, default);

    public async Task AttachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        if (labelNames is null) throw new ArgumentNullException(nameof(labelNames));

        var descriptor = _registry.Require<T>();                      // 未註冊 → 清楚的例外
        var labels = await GetOrCreateLabelsAsync(labelNames, ct).ConfigureAwait(false);

        var undo = new TrackerSnapshot(_db);
        await SaveOrRestoreAsync(undo, () => descriptor.AddMissingLinksAsync(_db, entity, labels, undo, ct), ct).ConfigureAwait(false);
    }

    public async Task AttachManyAsync<T>(IEnumerable<T> entities, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
        if (entities is null) throw new ArgumentNullException(nameof(entities));
        if (labelNames is null) throw new ArgumentNullException(nameof(labelNames));

        var descriptor = _registry.Require<T>();
        var list = new List<ILabelable>();
        foreach (var entity in entities)
            list.Add(entity ?? throw new ArgumentException("the entity sequence contains a null.", nameof(entities)));
        if (list.Count == 0) return;

        var labels = await GetOrCreateLabelsAsync(labelNames, ct).ConfigureAwait(false);
        if (labels.Count == 0) return;

        var undo = new TrackerSnapshot(_db);
        await SaveOrRestoreAsync(undo, () => descriptor.AddMissingLinksManyAsync(_db, list, labels, undo, ct), ct).ConfigureAwait(false);
    }

    public Task DetachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable
        => DetachAsync(entity, (IEnumerable<string>)labelNames, default);

    public async Task DetachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        if (labelNames is null) throw new ArgumentNullException(nameof(labelNames));

        var descriptor = _registry.Require<T>();

        var names = NormalizeMany(labelNames);
        if (names.Count == 0) return;

        var labelIds = await _db.Set<Label>()
            .Where(l => names.Contains(l.Name))
            .Select(l => l.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        if (labelIds.Count == 0) return;

        var undo = new TrackerSnapshot(_db);
        await SaveOrRestoreAsync(undo, () => descriptor.RemoveLinksAsync(_db, entity, labelIds, undo, ct), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Label>> GetLabelsOfAsync<T>(T entity, CancellationToken ct = default)
        where T : class, ILabelable
        => entity is null
            ? throw new ArgumentNullException(nameof(entity))
            : _registry.Require<T>().GetLabelsAsync(_db, entity, ct);

    public async Task<IReadOnlyDictionary<T, IReadOnlyList<Label>>> GetLabelsOfManyAsync<T>(
        IEnumerable<T> entities, CancellationToken ct = default) where T : class, ILabelable
    {
        if (entities is null) throw new ArgumentNullException(nameof(entities));

        var descriptor = _registry.Require<T>();
        var list = new List<ILabelable>();
        foreach (var entity in entities)
            list.Add(entity ?? throw new ArgumentException("the entity sequence contains a null.", nameof(entities)));

        var result = new Dictionary<T, IReadOnlyList<Label>>(ReferenceEqualityComparer.Instance);
        if (list.Count == 0) return result;

        var raw = await descriptor.GetLabelsManyAsync(_db, list, ct).ConfigureAwait(false);
        foreach (var pair in raw)
            result[(T)pair.Key] = pair.Value;
        return result;
    }

    // ---- 查詢 ----

    public Task<IReadOnlyList<LabelHit>> FindByLabelAsync(
        string labelName, bool includeDescendants = true, CancellationToken ct = default)
        => FindByLabelsAsync(new[] { labelName }, LabelMatch.Any, includeDescendants, ct);

    public Task<IQueryable<T>> QueryByLabelAsync<T>(
        string labelName, bool includeDescendants = true, CancellationToken ct = default)
        where T : class, ILabelable
        => QueryByLabelsAsync<T>(new[] { labelName }, LabelMatch.Any, includeDescendants, ct);

    public async Task<IReadOnlyList<LabelHit>> FindByLabelsAsync(
        IEnumerable<string> labelNames, LabelMatch match = LabelMatch.Any,
        bool includeDescendants = true, CancellationToken ct = default)
    {
        if (labelNames is null) throw new ArgumentNullException(nameof(labelNames));

        var groups = await ResolveLabelIdGroupsAsync(labelNames, includeDescendants, match, ct).ConfigureAwait(false);
        if (groups is null) return Array.Empty<LabelHit>();

        var hits = new List<LabelHit>();
        foreach (var d in _registry.Operations)                       // N 次查詢;benchmark 實測 10k×5 型別 ~19ms,夠用
            hits.AddRange(await d.QueryHitsAsync(_db, groups, match, ct).ConfigureAwait(false));
        return hits;
    }

    public async Task<IQueryable<T>> QueryByLabelsAsync<T>(
        IEnumerable<string> labelNames, LabelMatch match = LabelMatch.Any,
        bool includeDescendants = true, CancellationToken ct = default)
        where T : class, ILabelable
    {
        if (labelNames is null) throw new ArgumentNullException(nameof(labelNames));

        var descriptor = _registry.Require<T>();

        var groups = await ResolveLabelIdGroupsAsync(labelNames, includeDescendants, match, ct).ConfigureAwait(false);
        // 結果必為空時也要回 EF 支撐的查詢:呼叫端會接著用 ToListAsync / CountAsync,
        // 記憶體裡的空 IQueryable(Enumerable.Empty().AsQueryable())對那些運算子會拋例外
        if (groups is null) return _db.Set<T>().Where(_ => false);

        return (IQueryable<T>)descriptor.CreateQueryByLabels(_db, groups, match);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetUsageCountsAsync(CancellationToken ct = default)
    {
        var totals = new Dictionary<Guid, int>();
        foreach (var d in _registry.Operations)
        {
            var counts = await d.CountUsageAsync(_db, ct).ConfigureAwait(false);
            foreach (var pair in counts)
                totals[pair.Key] = totals.TryGetValue(pair.Key, out var n) ? n + pair.Value : pair.Value;
        }
        return totals;
    }

    // ---- 內部 ----

    /// <summary>
    /// 名稱正規化:去前後空白;空白字串視為無效(null)。大小寫不動。
    /// 多數 API 拿正規化後的名稱到資料庫比對(依 collation);以名稱查詢的 API 例外,見 <see cref="ResolveLabelIdGroupsAsync"/>。
    /// </summary>
    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name!.Trim();

    private static List<string> NormalizeMany(IEnumerable<string> names)
        => names.Select(Normalize).Where(n => n is not null).Distinct().Select(n => n!).ToList();

    /// <summary>
    /// 偵測追蹤器裡每個標籤的變更:導覽屬性(Parent、Children)要偵測過才會寫回 ParentId。
    /// 只掃標籤——消費端關掉自動偵測時,不替他偵測其他實體(那會改變哪些未存變更跟著送出)。
    /// </summary>
    private void DetectLabelChanges()
    {
        foreach (var entry in _db.ChangeTracker.Entries<Label>().ToList())
            entry.DetectChanges();
    }

    /// <summary>
    /// 還原前的偵測:同 <see cref="DetectLabelChanges"/>,但逐一偵測、各自吞掉失敗——
    /// 某個標籤偵測不了(例如身分衝突)不能擋住其他標籤,也不能蓋掉呼叫端要拿到的原本那個例外。
    /// 偵測失敗的那筆就照記下的值還原。
    /// </summary>
    private void DetectLabelChangesBestEffort()
    {
        var tracker = _db.ChangeTracker;
        var autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;   // 列舉時不要讓 EF 先掃整個 context(那一步失敗就什麼都偵測不到)
        try
        {
            foreach (var entry in tracker.Entries<Label>().ToList())
            {
                try
                {
                    entry.DetectChanges();
                }
                catch (Exception)
                {
                    // 只是還原前的準備:失敗就跳過這一筆
                }
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    /// <summary>
    /// 變更父標籤時的循環檢查:對每個有父標籤的標籤,沿父標籤的祖先鏈往上走,不可繞回自己。
    /// 祖先鏈以資料庫的階層為底,疊上追蹤器裡還沒存檔的 ParentId:同一次 SaveChanges 會把它們一起送出,
    /// 要檢查的是存檔之後的階層,不是資料庫現在的階層。
    /// </summary>
    private async Task EnsureNoCycleAsync(IReadOnlyCollection<Label> labels, CancellationToken ct)
    {
        var toCheck = labels.Where(l => l.ParentId is not null).ToList();
        if (toCheck.Count == 0) return;
        foreach (var label in toCheck)
        {
            if (label.ParentId == label.Id)
                throw new InvalidOperationException($"label '{label.Name}' cannot be its own parent.");
        }

        var all = await _db.Set<Label>().AsNoTracking()
            .Select(l => new { l.Id, l.ParentId })
            .ToListAsync(ct).ConfigureAwait(false);
        var parentOf = all.ToDictionary(x => x.Id, x => x.ParentId);
        foreach (var entry in _db.ChangeTracker.Entries<Label>())
        {
            if (entry.State == EntityState.Added
                || (entry.State == EntityState.Modified && entry.Property(l => l.ParentId).IsModified))
                parentOf[entry.Entity.Id] = entry.Entity.ParentId;
        }

        foreach (var label in toCheck)
        {
            var current = label.ParentId;
            var hops = 0;
            while (current is not null)
            {
                if (current == label.Id)
                    throw new InvalidOperationException(
                        $"label '{label.Name}' cannot be moved under one of its own descendants (that would form a cycle).");
                if (!parentOf.TryGetValue(current.Value, out var next))
                    throw new InvalidOperationException($"parent label {current} does not exist.");
                current = next;
                if (++hops > parentOf.Count) break;   // 資料已異常成環時的防呆,交給上面的檢查擋
            }
        }
    }

    /// <summary>
    /// 把名稱集合解析成標籤 Id 群組:每個名稱一組,若 includeDescendants 則走訪階層收集子孫。
    /// 回傳 null 表示「結果必為空」:沒有任何有效名稱、Any 模式下全部名稱都不存在,
    /// 或 All 模式下任一名稱不存在(缺一個就不可能同時滿足)。
    /// </summary>
    private async Task<List<IReadOnlyCollection<Guid>>?> ResolveLabelIdGroupsAsync(
        IEnumerable<string> labelNames, bool includeDescendants, LabelMatch match, CancellationToken ct)
    {
        var names = NormalizeMany(labelNames);
        if (names.Count == 0) return null;

        // 標籤數量通常很小,一次撈出 (Id, ParentId, Name) 在記憶體解析最簡單也最不容易寫錯
        var all = await _db.Set<Label>()
            .Select(l => new { l.Id, l.ParentId, l.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // 名稱在記憶體以 Ordinal 比對,不跟資料庫 collation 走(其他 API 在資料庫端比對)。
        // 這是現行的對外行為,ILabelStore.FindByLabelsAsync 的 XML 與 README Limitations 都寫明了;
        // 要不要改成跟 collation 走還沒拍板,CaseSensitivityTests 的「現行行為」測試釘著它。
        var byName = all.ToDictionary(l => l.Name, l => l.Id);
        var byParent = all.Where(l => l.ParentId != null).ToLookup(l => l.ParentId!.Value);

        var groups = new List<IReadOnlyCollection<Guid>>();
        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var rootId))
            {
                if (match == LabelMatch.All) return null;   // 缺一個名稱 → AND 不可能滿足
                continue;                                    // OR:略過不存在的名稱
            }

            if (!includeDescendants)
            {
                groups.Add(new[] { rootId });
                continue;
            }

            var group = new List<Guid>();
            var seen = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(rootId);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id)) continue;   // 防禦資料異常造成的循環
                group.Add(id);
                foreach (var child in byParent[id])
                    queue.Enqueue(child.Id);
            }
            groups.Add(group);
        }

        return groups.Count == 0 ? null : groups;
    }

    /// <summary>
    /// 在 <paramref name="undo"/> 的保護下做一段會動追蹤器的工作(<paramref name="mutate"/>,可省略),再呼叫消費端 context 的
    /// SaveChangesAsync。中途或存檔失敗時,先把 store 這次放進追蹤器的變更撤掉,遇到並發衝突再把衝突的項目重讀成資料庫的值,
    /// 然後原例外照拋。消費端的 context 常是長壽的:不撤掉的話,被拒絕的變更會留在追蹤器裡,
    /// 之後每一次存檔都撞同一個錯,或讓同一個 store 重讀時拿到沒存進去的值。
    /// </summary>
    private async Task SaveOrRestoreAsync(TrackerSnapshot undo, Func<Task>? mutate, CancellationToken ct)
    {
        try
        {
            if (mutate is not null)
                await mutate().ConfigureAwait(false);
            undo.ArmForSave();
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (undo.SaveAccepted)
                throw;   // 已經提交,例外來自提交之後的步驟(例如消費端的 SavedChanges 攔截器):不還原

            undo.Restore();
            await ReloadConflictsAsync(ex, undo).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 並發衝突之後,把衝突的項目裡 store 可以負責的那些重讀成資料庫的值(已被別人刪掉的會變成 detached),
    /// 同一個 store 接著重讀、重試才拿得到新值。消費端自己改過、還沒存的項目不碰(見 <see cref="TrackerSnapshot.MayReload"/>)。
    /// </summary>
    private static async Task ReloadConflictsAsync(Exception ex, TrackerSnapshot undo)
    {
        if (ex is not DbUpdateConcurrencyException conflict) return;
        foreach (var entry in conflict.Entries.Where(e => undo.MayReload(e.Entity)).ToList())
        {
            try
            {
                await entry.ReloadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 重讀只是盡力而為:失敗就停在已還原的狀態,不能讓清理時的錯蓋掉原本的例外
            }
        }
    }

    /// <summary>這個追蹤中的項目是否以外鍵直接參照指定的標籤(連結、子標籤,或消費端自己參照標籤的實體)。</summary>
    private static bool ReferencesLabel(EntityEntry entry, Guid labelId)
        => entry.Metadata.GetForeignKeys().Any(fk =>
            typeof(Label).IsAssignableFrom(fk.PrincipalEntityType.ClrType)
            && fk.Properties.Count == 1
            && Equals(entry.Property(fk.Properties[0].Name).CurrentValue, labelId));

    /// <summary>
    /// 依 <see cref="RenameAsync"/> 的規則驗證標籤目前的名稱(去前後空白、不可空白、長度、全域唯一),
    /// 通過就把正規化後的名稱寫回去。
    /// </summary>
    private async Task NormalizeNameForSaveAsync(Label label, string paramName, CancellationToken ct)
    {
        var normalized = Normalize(label.Name)
            ?? throw new ArgumentException("label name must not be blank.", paramName);
        ValidateNameLength(normalized);
        var id = label.Id;
        var taken = await _db.Set<Label>().AnyAsync(l => l.Name == normalized && l.Id != id, ct).ConfigureAwait(false);
        if (taken)
            throw new InvalidOperationException($"label name '{normalized}' is already taken.");
        label.Name = normalized;
    }

    /// <summary>建立與改名時主動驗證名稱長度(SQLite 不強制 HasMaxLength,不能只靠資料庫)。</summary>
    private static void ValidateNameLength(string normalized)
    {
        if (normalized.Length > Label.MaxNameLength)
            throw new ArgumentException(
                $"label name is {normalized.Length} characters, over the {Label.MaxNameLength} limit: '{normalized[..16]}…'");
    }

    /// <summary>
    /// 依名稱取得標籤;不存在時依 <see cref="LabelRegistry.AutoCreateLabels"/> 決定自動建立或拋例外。
    /// 先全部查過、驗證完要建立的名稱,才開始寫入:參數錯誤不能等到前面的標籤已經存檔才發現
    /// (那次存檔還會一起送出消費端 context 裡其他未存的變更)。
    /// </summary>
    private async Task<List<Label>> GetOrCreateLabelsAsync(IEnumerable<string> labelNames, CancellationToken ct)
    {
        var names = NormalizeMany(labelNames);

        // 第一輪只查不寫
        var found = new Label?[names.Count];
        var missing = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            found[i] = await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
            if (found[i] is null)
                missing.Add(name);
        }

        if (missing.Count > 0 && !_registry.AutoCreateLabels)
            throw new InvalidOperationException(   // 收集完所有缺的,一次報清楚
                $"label(s) {string.Join(", ", missing.Select(m => $"'{m}'"))} do not exist and AutoCreateLabels is disabled. " +
                $"Create them with CreateAsync first (optionally with a color and icon), or set LabelRegistry.AutoCreateLabels back to true.");
        foreach (var name in missing)
            ValidateNameLength(name);

        // 第二輪才建立,每個新標籤各自存檔
        var result = new List<Label>(names.Count);
        var createdAny = false;
        for (var i = 0; i < names.Count; i++)
        {
            if (found[i] is { } existing)
            {
                result.Add(existing);
                continue;
            }

            // 不分大小寫的 collation 上,同一批裡只差大小寫的名稱(如 "Paper"、"paper")是同一個標籤:
            // 這一批已經建過標籤的話先重查,沿用剛建好的那筆,不要等撞上唯一索引才發現
            var name = names[i];
            var label = createdAny
                ? await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false)
                : null;
            if (label is null)
            {
                label = await CreateForAttachAsync(name, ct).ConfigureAwait(false);
                createdAny = true;
            }
            result.Add(label);
        }

        return result;
    }

    /// <summary>
    /// 貼標時自動建立一個標籤並存檔。同名競態(unique index 撞擊)以重讀處理:
    /// 重讀得到同名標籤 → 用既有那筆;讀不到 → 不是競態,原例外照拋。
    /// </summary>
    private async Task<Label> CreateForAttachAsync(string name, CancellationToken ct)
    {
        var label = new Label
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid(),
        };
        _db.Set<Label>().Add(label);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "auto-created label '{Name}' while attaching (AutoCreateLabels is on; curated-label apps should set it to false)", name);
            return label;
        }
        catch (DbUpdateException)
        {
            // 可能是同名競態(另一條執行路徑剛建立了同名標籤、撞上 unique index)。
            // 重讀驗證:真的有同名標籤 → 用既有那筆;沒有 → 不是競態,原例外照拋。
            _db.Entry(label).State = EntityState.Detached;
            var winner = await _db.Set<Label>()
                .FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
            if (winner is null) throw;
            return winner;
        }
        catch
        {
            // 取消等其他失敗:同樣不留下新增中的標籤。已經不是 Added 就是存檔已提交、例外來自之後的步驟,留著
            if (_db.Entry(label).State == EntityState.Added)
                _db.Entry(label).State = EntityState.Detached;
            throw;
        }
    }
}
