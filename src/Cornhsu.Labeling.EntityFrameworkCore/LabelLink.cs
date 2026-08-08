namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// The record of "this label is attached to this entity".
/// EF Core treats each closed generic (e.g. <c>LabelLink&lt;Note, int&gt;</c>) as a distinct entity type,
/// each mapping to its own join table with real foreign keys — which is what automates "one table per type".
/// </summary>
/// <typeparam name="TEntity">The labelable entity type.</typeparam>
/// <typeparam name="TKey">The entity's primary key type.</typeparam>
public class LabelLink<TEntity, TKey>
    where TEntity : class, ILabelable<TKey>
    where TKey : notnull
{
    /// <summary>Label id (foreign key → Label.Id).</summary>
    public Guid LabelId { get; set; }

    /// <summary>Label navigation property.</summary>
    public Label Label { get; set; } = default!;

    /// <summary>Entity id (foreign key → TEntity.Id).</summary>
    public TKey EntityId { get; set; } = default!;

    /// <summary>Entity navigation property.</summary>
    public TEntity Entity { get; set; } = default!;

    /// <summary>When the label was attached (UTC).</summary>
    public DateTimeOffset AttachedAt { get; set; }
}
