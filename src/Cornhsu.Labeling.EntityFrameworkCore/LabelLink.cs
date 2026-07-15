namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 「標籤貼在實體上」的連結紀錄。
/// EF Core 把每個封閉泛型(如 <c>LabelLink&lt;Note&gt;</c>)視為獨立實體,
/// 各自對應一張具備真外鍵的 join table——這就是把「每型別一張表」自動化的鑰匙。
/// </summary>
/// <typeparam name="TEntity">可標記的實體型別。</typeparam>
public class LabelLink<TEntity> where TEntity : class, ILabelable
{
    /// <summary>標籤 Id(外鍵 → Label.Id)。</summary>
    public Guid LabelId { get; set; }

    /// <summary>標籤導覽屬性。</summary>
    public Label Label { get; set; } = default!;

    /// <summary>實體 Id(外鍵 → TEntity.Id)。</summary>
    public Guid EntityId { get; set; }

    /// <summary>實體導覽屬性。</summary>
    public TEntity Entity { get; set; } = default!;

    /// <summary>貼標時間(UTC)。</summary>
    public DateTimeOffset AttachedAt { get; set; }
}
