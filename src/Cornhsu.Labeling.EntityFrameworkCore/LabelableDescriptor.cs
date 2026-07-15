using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

internal sealed class LabelableDescriptor<TEntity, TKey> : ILabelableOperations
    where TEntity : class, ILabelable<TKey>
    where TKey : notnull
{
    private readonly Func<TEntity, string?>? _displayName;

    public Type ClrType => typeof(TEntity);
    public Type KeyType => typeof(TKey);
    public string TypeKey { get; }

    public LabelableDescriptor(string typeKey, Func<TEntity, string?>? displayName)
        => (TypeKey, _displayName) = (typeKey, displayName);

    public void ConfigureModel(ModelBuilder b, LabelRegistry r)
    {
        b.Entity<LabelLink<TEntity, TKey>>(e =>
        {
            e.ToTable($"{r.LinkTablePrefix}{TypeKey}");
            e.HasKey(x => new { x.LabelId, x.EntityId });   // 複合主鍵 → 天然防重複

            e.HasOne(x => x.Label).WithMany()
             .HasForeignKey(x => x.LabelId)
             .OnDelete(DeleteBehavior.Cascade);             // 刪標籤 → 連結消失

            e.HasOne(x => x.Entity).WithMany()
             .HasForeignKey(x => x.EntityId)
             .OnDelete(DeleteBehavior.Cascade);             // 刪實體 → 連結消失,不留孤兒

            e.HasIndex(x => x.EntityId);                    // 反查「這個實體有哪些標籤」
        });
    }

    public async Task<IReadOnlyList<LabelHit>> QueryHitsAsync(
        DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> labelIdGroups, LabelMatch match, CancellationToken ct)
    {
        var entities = await BuildEntityQuery(db, labelIdGroups, match)
            .ToListAsync(ct).ConfigureAwait(false);

        return entities
            .Select(e => new LabelHit(typeof(TEntity), TypeKey, e.Id, _displayName?.Invoke(e)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(DbContext db, CancellationToken ct)
        => await db.Set<LabelLink<TEntity, TKey>>()
            .GroupBy(l => l.LabelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);

    public async Task AddMissingLinksAsync(
        DbContext db, ILabelable entity, IReadOnlyList<Label> labels, CancellationToken ct)
    {
        var id = ((TEntity)entity).Id;
        var links = db.Set<LabelLink<TEntity, TKey>>();

        foreach (var label in labels)
        {
            // 複合主鍵直接 Find:同時看得到資料庫與尚未存檔的追蹤項目 → 冪等
            var existing = await links.FindAsync(new object[] { label.Id, id }, ct).ConfigureAwait(false);
            if (existing is not null) continue;

            links.Add(new LabelLink<TEntity, TKey>
            {
                LabelId = label.Id,
                EntityId = id,
                AttachedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    public async Task RemoveLinksAsync(
        DbContext db, ILabelable entity, IReadOnlyCollection<Guid> labelIds, CancellationToken ct)
    {
        var id = ((TEntity)entity).Id;
        var links = db.Set<LabelLink<TEntity, TKey>>();

        foreach (var labelId in labelIds)
        {
            var link = await links.FindAsync(new object[] { labelId, id }, ct).ConfigureAwait(false);
            if (link is not null) links.Remove(link);
        }
    }

    public async Task<IReadOnlyList<Label>> GetLabelsAsync(DbContext db, ILabelable entity, CancellationToken ct)
        => await db.Set<LabelLink<TEntity, TKey>>()
            .Where(EntityIdEquals(((TEntity)entity).Id))
            .Select(l => l.Label)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<ILabelable, IReadOnlyList<Label>>> GetLabelsManyAsync(
        DbContext db, IReadOnlyList<ILabelable> entities, CancellationToken ct)
    {
        var typed = entities.Cast<TEntity>().ToList();
        var ids = typed.Select(e => e.Id).Distinct().ToList();

        var pairs = await db.Set<LabelLink<TEntity, TKey>>()
            .Where(l => ids.Contains(l.EntityId))
            .Select(l => new { l.EntityId, l.Label })
            .ToListAsync(ct).ConfigureAwait(false);

        var byId = pairs
            .GroupBy(p => p.EntityId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Label>)g.Select(p => p.Label)
                    .OrderBy(l => l.SortOrder).ThenBy(l => l.Name, StringComparer.Ordinal)
                    .ToList());

        // 以實例(參考相等)為鍵:呼叫端傳什麼就用什麼當 key,每個實體都保證有項目
        var result = new Dictionary<ILabelable, IReadOnlyList<Label>>(ReferenceEqualityComparer.Instance);
        foreach (var e in typed)
            result[e] = byId.TryGetValue(e.Id, out var labels) ? labels : Array.Empty<Label>();
        return result;
    }

    public IQueryable CreateQueryByLabels(DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> labelIdGroups, LabelMatch match)
        => BuildEntityQuery(db, labelIdGroups, match);

    /// <summary>
    /// 組出「貼著指定標籤的實體」查詢。
    /// Any(或只有一個群組):所有 Id 合併成一個 IN。
    /// All:第一個群組為基底,其餘群組各以 IN 子查詢做交集
    /// (benchmark 實測:子查詢比 GroupBy/HAVING 慢一些但語意能正確涵蓋子孫群組)。
    /// </summary>
    private IQueryable<TEntity> BuildEntityQuery(
        DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> groups, LabelMatch match)
    {
        var links = db.Set<LabelLink<TEntity, TKey>>();

        if (match == LabelMatch.Any || groups.Count == 1)
        {
            var ids = groups.SelectMany(g => g).Distinct().ToList();
            return links
                .Where(l => ids.Contains(l.LabelId))
                .Select(l => l.Entity)
                .Distinct();
        }

        var first = groups[0].ToList();
        var q = links.Where(l => first.Contains(l.LabelId));
        for (var i = 1; i < groups.Count; i++)
        {
            var ids = groups[i].ToList();
            var sub = links.Where(l2 => ids.Contains(l2.LabelId)).Select(l2 => l2.EntityId);
            q = q.Where(l => sub.Contains(l.EntityId));
        }
        return q.Select(l => l.Entity).Distinct();
    }

    /// <summary>
    /// 手工組 <c>l.EntityId == id</c> 的運算式:泛型 TKey 無法直接用 ==,
    /// 而以 StrongBox 包住值讓 EF 視為參數(而非常數),SQL 查詢計畫才能被快取。
    /// </summary>
    private static Expression<Func<LabelLink<TEntity, TKey>, bool>> EntityIdEquals(TKey id)
    {
        var parameter = Expression.Parameter(typeof(LabelLink<TEntity, TKey>), "l");
        var body = Expression.Equal(
            Expression.Property(parameter, nameof(LabelLink<TEntity, TKey>.EntityId)),
            Expression.Field(Expression.Constant(new StrongBox<TKey>(id)), nameof(StrongBox<TKey>.Value)));
        return Expression.Lambda<Func<LabelLink<TEntity, TKey>, bool>>(body, parameter);
    }
}
