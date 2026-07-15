using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

internal sealed class LabelableDescriptor<TEntity> : ILabelableDescriptor
    where TEntity : class, ILabelable
{
    private readonly Func<TEntity, string?>? _displayName;

    public Type ClrType => typeof(TEntity);
    public string TypeKey { get; }

    public LabelableDescriptor(string typeKey, Func<TEntity, string?>? displayName)
        => (TypeKey, _displayName) = (typeKey, displayName);

    public void ConfigureModel(ModelBuilder b, LabelRegistry r)
    {
        b.Entity<LabelLink<TEntity>>(e =>
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
        var entities = await db.Set<LabelLink<TEntity>>()
            .Where(l => labelIds.Contains(l.LabelId))
            .Select(l => l.Entity)
            .Distinct()
            .ToListAsync(ct);

        return entities
            .Select(e => new LabelHit(typeof(TEntity), TypeKey, e.Id, _displayName?.Invoke(e)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(DbContext db, CancellationToken ct)
        => await db.Set<LabelLink<TEntity>>()
            .GroupBy(l => l.LabelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
