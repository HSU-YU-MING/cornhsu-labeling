namespace Cornhsu.Labeling;

/// <summary>
/// 標籤本體。名稱全域唯一、支援父子階層與同層排序。
/// 所有連結都以 <see cref="Id"/> 指向標籤,因此重新命名是一次 O(1) 的 UPDATE。
/// </summary>
public class Label
{
    /// <summary>名稱長度上限。建立與改名時主動驗證,不依賴資料庫是否強制(SQLite 不強制)。</summary>
    public const int MaxNameLength = 64;

    /// <summary>主鍵。</summary>
    public Guid Id { get; set; }

    /// <summary>顯示名稱,全域唯一。</summary>
    public string Name { get; set; } = default!;

    /// <summary>顏色,建議 #RRGGBB。</summary>
    public string? Color { get; set; }

    /// <summary>
    /// 圖示(純視覺,無業務語意):可放 emoji、圖示名稱或短碼,由 UI 自行解讀。
    /// 與 <see cref="Color"/> 同層級——都是標籤的視覺識別。
    /// 帶業務語意的欄位(型別、模組隔離、租戶…)請放你自己的伴生表,見 README「擴充 Label」。
    /// </summary>
    public string? Icon { get; set; }

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
