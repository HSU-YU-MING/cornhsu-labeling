namespace Cornhsu.Labeling;

/// <summary>
/// The label itself. Names are globally unique; parent/child hierarchies and sibling ordering are supported.
/// Every link points at <see cref="Id"/>, which is what makes a rename a single O(1) UPDATE.
/// </summary>
public class Label
{
    /// <summary>Maximum name length. Validated up front on create and rename, so it does not depend on
    /// whether the database enforces it (SQLite does not).</summary>
    public const int MaxNameLength = 64;

    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name; globally unique.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Color, preferably as #RRGGBB.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Icon — purely visual, carries no business meaning: an emoji, an icon name or a short code,
    /// interpreted by your UI. It sits at the same level as <see cref="Color"/>: both are visual identity.
    /// Fields that do carry business meaning (type, module isolation, tenant…) belong in your own
    /// companion table — see "Extending Label" in the README.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>Parent label id; null means top level.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Parent navigation property; null means top level.</summary>
    public Label? Parent { get; set; }

    /// <summary>Child labels.</summary>
    public ICollection<Label> Children { get; set; } = new List<Label>();

    /// <summary>Ordering among siblings.</summary>
    public int SortOrder { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Concurrency token, rotated on every modification made through <c>ILabelStore</c>.
    /// When two threads or processes modify the same label, the one that saves second gets a
    /// <c>DbUpdateConcurrencyException</c> instead of silently overwriting the first one's change.
    /// It is not database-generated (unlike SQL Server rowversion), so behaviour is identical across providers.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; }
}
