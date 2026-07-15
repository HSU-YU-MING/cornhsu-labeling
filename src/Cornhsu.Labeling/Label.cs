namespace Cornhsu.Labeling;

/// <summary>
/// 標籤本體。名稱全域唯一、支援父子階層與同層排序。
/// 所有連結都以 <see cref="Id"/> 指向標籤,因此重新命名是一次 O(1) 的 UPDATE。
/// </summary>
public class Label
{
    /// <summary>主鍵。</summary>
    public Guid Id { get; set; }

    /// <summary>顯示名稱,全域唯一。</summary>
    public string Name { get; set; } = default!;

    /// <summary>顏色,建議 #RRGGBB。</summary>
    public string? Color { get; set; }

    /// <summary>父標籤 Id;null 表示頂層。</summary>
    public Guid? ParentId { get; set; }

    /// <summary>父標籤導覽屬性;null 表示頂層。</summary>
    public Label? Parent { get; set; }

    /// <summary>子標籤集合。</summary>
    public ICollection<Label> Children { get; set; } = new List<Label>();

    /// <summary>同層排序。</summary>
    public int SortOrder { get; set; }

    /// <summary>建立時間(UTC)。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
