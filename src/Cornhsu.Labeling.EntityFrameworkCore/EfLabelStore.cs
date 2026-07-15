using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

internal sealed class EfLabelStore<TContext> : ILabelStore where TContext : DbContext
{
    private readonly TContext _db;
    private readonly LabelRegistry _registry;

    public EfLabelStore(TContext db, LabelRegistry registry) => (_db, _registry) = (db, registry);

    // ---- 標籤 CRUD ----

    public async Task<Label> CreateAsync(string name, string? color = null, Guid? parentId = null, CancellationToken ct = default)
    {
        var normalized = Normalize(name)
            ?? throw new ArgumentException("標籤名稱不可為空白。", nameof(name));

        var existing = await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == normalized, ct).ConfigureAwait(false);
        if (existing is not null)
            throw new InvalidOperationException($"標籤 '{normalized}' 已存在(Id: {existing.Id})。");

        var label = new Label
        {
            Id = Guid.NewGuid(),
            Name = normalized,
            Color = color,
            ParentId = parentId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Set<Label>().Add(label);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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

        var label = await _db.Set<Label>().FindAsync(new object[] { labelId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"找不到標籤 {labelId}。");

        var taken = await _db.Set<Label>().AnyAsync(l => l.Name == normalized && l.Id != labelId, ct).ConfigureAwait(false);
        if (taken)
            throw new InvalidOperationException($"標籤名稱 '{normalized}' 已被使用。");

        // 連結存的是 Id,改名是一次 O(1) 的 UPDATE,不需要任何 cascade
        label.Name = normalized;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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
    }

    // ---- 貼標 / 撕標 ----

    public Task AttachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable
        => AttachAsync(entity, (IEnumerable<string>)labelNames, default);

    public async Task AttachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
        var descriptor = _registry.Require<T>();                      // 未註冊 → 清楚的例外
        var labels = await GetOrCreateLabelsAsync(labelNames, ct).ConfigureAwait(false);

        await descriptor.AddMissingLinksAsync(_db, entity, labels, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task DetachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable
        => DetachAsync(entity, (IEnumerable<string>)labelNames, default);

    public async Task DetachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
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
        => _registry.Require<T>().GetLabelsAsync(_db, entity, ct);

    // ---- 查詢 ----

    public async Task<IReadOnlyList<LabelHit>> FindByLabelAsync(
        string labelName, bool includeDescendants = true, CancellationToken ct = default)
    {
        var ids = await ResolveLabelIdsAsync(labelName, includeDescendants, ct).ConfigureAwait(false);
        if (ids.Count == 0) return Array.Empty<LabelHit>();

        var hits = new List<LabelHit>();
        foreach (var d in _registry.Operations)                       // N 次查詢,M4 實測後再決定是否優化
            hits.AddRange(await d.QueryHitsAsync(_db, ids, ct).ConfigureAwait(false));
        return hits;
    }

    public async Task<IQueryable<T>> QueryByLabelAsync<T>(
        string labelName, bool includeDescendants = true, CancellationToken ct = default)
        where T : class, ILabelable
    {
        var descriptor = _registry.Require<T>();

        var ids = await ResolveLabelIdsAsync(labelName, includeDescendants, ct).ConfigureAwait(false);
        if (ids.Count == 0) return Enumerable.Empty<T>().AsQueryable();

        return (IQueryable<T>)descriptor.CreateQueryByLabels(_db, ids);
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

    /// <summary>找到標籤本身;若 includeDescendants 則走訪階層收集所有子孫標籤 Id。</summary>
    private async Task<List<Guid>> ResolveLabelIdsAsync(string labelName, bool includeDescendants, CancellationToken ct)
    {
        var normalized = Normalize(labelName);
        if (normalized is null) return new List<Guid>();

        if (!includeDescendants)
        {
            var id = await _db.Set<Label>()
                .Where(l => l.Name == normalized)
                .Select(l => (Guid?)l.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return id is null ? new List<Guid>() : new List<Guid> { id.Value };
        }

        // 標籤數量通常很小,一次撈出 (Id, ParentId) 在記憶體走訪最簡單也最不容易寫錯
        var all = await _db.Set<Label>()
            .Select(l => new { l.Id, l.ParentId, l.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var root = all.FirstOrDefault(l => l.Name == normalized);
        if (root is null) return new List<Guid>();

        var byParent = all.Where(l => l.ParentId != null).ToLookup(l => l.ParentId!.Value);
        var result = new List<Guid>();
        var seen = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(root.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;   // 防禦資料異常造成的循環
            result.Add(id);
            foreach (var child in byParent[id])
                queue.Enqueue(child.Id);
        }
        return result;
    }

    /// <summary>依名稱取得標籤,不存在就建立。同名競態(unique index 撞擊)以重讀處理。</summary>
    private async Task<List<Label>> GetOrCreateLabelsAsync(IEnumerable<string> labelNames, CancellationToken ct)
    {
        var result = new List<Label>();
        foreach (var name in NormalizeMany(labelNames))
        {
            var label = await _db.Set<Label>().FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
            if (label is null)
            {
                label = new Label { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTimeOffset.UtcNow };
                _db.Set<Label>().Add(label);
                try
                {
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // 競態:另一條執行路徑剛建立了同名標籤 → 放棄本次新增,重讀既有那筆
                    _db.Entry(label).State = EntityState.Detached;
                    label = await _db.Set<Label>().FirstAsync(l => l.Name == name, ct).ConfigureAwait(false);
                }
            }
            result.Add(label);
        }
        return result;
    }
}
