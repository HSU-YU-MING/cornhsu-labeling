using Microsoft.EntityFrameworkCore;
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
            ?? throw new ArgumentException("標籤名稱不可為空白。", nameof(name));
        ValidateNameLength(normalized);

        var existing = await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == normalized, ct).ConfigureAwait(false);
        if (existing is not null)
            throw new InvalidOperationException($"標籤 '{normalized}' 已存在(Id: {existing.Id})。");

        if (parentId is not null
            && !await _db.Set<Label>().AnyAsync(l => l.Id == parentId, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"父標籤 {parentId} 不存在。");

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
        _db.Set<Label>().Add(label);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("已建立標籤 '{Name}'(Id: {Id})", label.Name, label.Id);
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
            ?? throw new ArgumentException("標籤名稱不可為空白。", nameof(newName));
        ValidateNameLength(normalized);

        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"找不到標籤 {labelId}。");

        var taken = await _db.Set<Label>().AnyAsync(l => l.Name == normalized && l.Id != labelId, ct).ConfigureAwait(false);
        if (taken)
            throw new InvalidOperationException($"標籤名稱 '{normalized}' 已被使用。");

        // 連結存的是 Id,改名是一次 O(1) 的 UPDATE,不需要任何 cascade
        label.Name = normalized;
        label.ConcurrencyStamp = Guid.NewGuid();   // 輪換並發戳記
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Label> UpdateAsync(Guid labelId, Action<Label> update, CancellationToken ct = default)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));

        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"找不到標籤 {labelId}。");

        var originalName = label.Name;
        update(label);

        if (label.Id != labelId)
            throw new InvalidOperationException("不可在 UpdateAsync 中修改標籤的 Id。");

        // 改名走與 RenameAsync 相同的規則(正規化 + 唯一性)
        if (!string.Equals(label.Name, originalName, StringComparison.Ordinal))
        {
            var normalized = Normalize(label.Name)
                ?? throw new ArgumentException("標籤名稱不可為空白。", nameof(update));
            ValidateNameLength(normalized);
            var taken = await _db.Set<Label>().AnyAsync(l => l.Name == normalized && l.Id != labelId, ct).ConfigureAwait(false);
            if (taken)
                throw new InvalidOperationException($"標籤名稱 '{normalized}' 已被使用。");
            label.Name = normalized;
        }

        await EnsureNoCycleAsync(label, ct).ConfigureAwait(false);
        label.ConcurrencyStamp = Guid.NewGuid();   // 輪換並發戳記
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return label;
    }

    public async Task DeleteAsync(Guid labelId, CancellationToken ct = default)
    {
        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"找不到標籤 {labelId}。");

        var hasChildren = await _db.Set<Label>().AnyAsync(l => l.ParentId == labelId, ct).ConfigureAwait(false);
        if (hasChildren)
            throw new InvalidOperationException(
                $"標籤 '{label.Name}' 尚有子標籤,請先刪除或移動子標籤。(資料庫層亦以 Restrict 外鍵擋下)");

        _db.Set<Label>().Remove(label);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);   // 各型別的連結由 cascade delete 自動清除
        _logger.LogDebug("已刪除標籤 '{Name}'(Id: {Id})", label.Name, labelId);
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

        await descriptor.AddMissingLinksAsync(_db, entity, labels, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AttachManyAsync<T>(IEnumerable<T> entities, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
        if (entities is null) throw new ArgumentNullException(nameof(entities));
        if (labelNames is null) throw new ArgumentNullException(nameof(labelNames));

        var descriptor = _registry.Require<T>();
        var list = new List<ILabelable>();
        foreach (var entity in entities)
            list.Add(entity ?? throw new ArgumentException("實體集合中含有 null。", nameof(entities)));
        if (list.Count == 0) return;

        var labels = await GetOrCreateLabelsAsync(labelNames, ct).ConfigureAwait(false);
        if (labels.Count == 0) return;

        await descriptor.AddMissingLinksManyAsync(_db, list, labels, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        await descriptor.RemoveLinksAsync(_db, entity, labelIds, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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
            list.Add(entity ?? throw new ArgumentException("實體集合中含有 null。", nameof(entities)));

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
        if (groups is null) return Enumerable.Empty<T>().AsQueryable();

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

    /// <summary>名稱正規化:去前後空白;空白字串視為無效(null)。大小寫不動,交由資料庫 collation 決定。</summary>
    private static string? Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name!.Trim();

    private static List<string> NormalizeMany(IEnumerable<string> names)
        => names.Select(Normalize).Where(n => n is not null).Distinct().Select(n => n!).ToList();

    /// <summary>變更父標籤時的循環檢查:沿新父標籤的祖先鏈往上走,不可繞回自己。</summary>
    private async Task EnsureNoCycleAsync(Label label, CancellationToken ct)
    {
        if (label.ParentId is null) return;
        if (label.ParentId == label.Id)
            throw new InvalidOperationException($"標籤 '{label.Name}' 不可以是自己的父標籤。");

        var all = await _db.Set<Label>().AsNoTracking()
            .Select(l => new { l.Id, l.ParentId })
            .ToListAsync(ct).ConfigureAwait(false);
        var parentOf = all.ToDictionary(x => x.Id, x => x.ParentId);

        var current = label.ParentId;
        var hops = 0;
        while (current is not null)
        {
            if (current == label.Id)
                throw new InvalidOperationException(
                    $"不可把標籤 '{label.Name}' 移到自己的子孫標籤底下(會形成循環)。");
            if (!parentOf.TryGetValue(current.Value, out var next))
                throw new InvalidOperationException($"父標籤 {current} 不存在。");
            current = next;
            if (++hops > parentOf.Count) break;   // 資料已異常成環時的防呆,交給上面的檢查擋
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

    /// <summary>建立與改名時主動驗證名稱長度(SQLite 不強制 HasMaxLength,不能只靠資料庫)。</summary>
    private static void ValidateNameLength(string normalized)
    {
        if (normalized.Length > Label.MaxNameLength)
            throw new ArgumentException(
                $"標籤名稱長度 {normalized.Length} 超過上限 {Label.MaxNameLength}:'{normalized[..16]}…'");
    }

    /// <summary>
    /// 依名稱取得標籤;不存在時依 <see cref="LabelRegistry.AutoCreateLabels"/> 決定自動建立或拋例外。
    /// 同名競態(unique index 撞擊)以重讀處理。
    /// </summary>
    private async Task<List<Label>> GetOrCreateLabelsAsync(IEnumerable<string> labelNames, CancellationToken ct)
    {
        var result = new List<Label>();
        var missing = new List<string>();
        foreach (var name in NormalizeMany(labelNames))
        {
            var label = await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
            if (label is null)
            {
                if (!_registry.AutoCreateLabels)
                {
                    missing.Add(name);   // 先收集完所有缺的,一次報清楚
                    continue;
                }

                ValidateNameLength(name);
                label = new Label
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
                        "貼標時自動建立了標籤 '{Name}'(AutoCreateLabels 已啟用;策展式 App 建議設為 false)", name);
                }
                catch (DbUpdateException)
                {
                    // 可能是同名競態(另一條執行路徑剛建立了同名標籤、撞上 unique index)。
                    // 重讀驗證:真的有同名標籤 → 用既有那筆;沒有 → 不是競態,原例外照拋。
                    _db.Entry(label).State = EntityState.Detached;
                    var winner = await _db.Set<Label>()
                        .FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
                    if (winner is null) throw;
                    label = winner;
                }
            }
            result.Add(label);
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"標籤 {string.Join("、", missing.Select(m => $"'{m}'"))} 不存在,而 AutoCreateLabels 已停用。" +
                $"請先用 CreateAsync 建立(可帶顏色與圖示),或將 LabelRegistry.AutoCreateLabels 設回 true。");

        return result;
    }
}
