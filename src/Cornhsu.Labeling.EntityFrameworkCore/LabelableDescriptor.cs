using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

internal sealed class LabelableDescriptor<TEntity, TKey> : ILabelableDescriptor
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
        DbContext db, IReadOnlyCollection<Guid> labelIds, CancellationToken ct)
    {
        var entities = await db.Set<LabelLink<TEntity, TKey>>()
            .Where(l => labelIds.Contains(l.LabelId))
            .Select(l => l.Entity)
            .Distinct()
            .ToListAsync(ct);

        return entities
            .Select(e => new LabelHit(typeof(TEntity), TypeKey, e.Id, _displayName?.Invoke(e)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(DbContext db, CancellationToken ct)
        => await db.Set<LabelLink<TEntity, TKey>>()
            .GroupBy(l => l.LabelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task AddMissingLinksAsync(
        DbContext db, ILabelable entity, IReadOnlyList<Label> labels, CancellationToken ct)
    {
        var id = ((TEntity)entity).Id;
        var links = db.Set<LabelLink<TEntity, TKey>>();

        foreach (var label in labels)
        {
            // 複合主鍵直接 Find:同時看得到資料庫與尚未存檔的追蹤項目 → 冪等
            var existing = await links.FindAsync(new object[] { label.Id, id }, ct);
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
            var link = await links.FindAsync(new object[] { labelId, id }, ct);
            if (link is not null) links.Remove(link);
        }
    }

    public async Task<IReadOnlyList<Label>> GetLabelsAsync(DbContext db, ILabelable entity, CancellationToken ct)
        => await db.Set<LabelLink<TEntity, TKey>>()
            .Where(EntityIdEquals(((TEntity)entity).Id))
            .Select(l => l.Label)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync(ct);

    public IQueryable CreateQueryByLabels(DbContext db, IReadOnlyCollection<Guid> labelIds)
        => db.Set<LabelLink<TEntity, TKey>>()
            .Where(l => labelIds.Contains(l.LabelId))
            .Select(l => l.Entity)
            .Distinct();

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
