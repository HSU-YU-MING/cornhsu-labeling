namespace Cornhsu.Labeling;

/// <summary>多標籤查詢的匹配模式。</summary>
public enum LabelMatch
{
    /// <summary>符合任一標籤即命中(OR)。不存在的標籤名稱不影響其他標籤的結果。</summary>
    Any = 0,

    /// <summary>必須同時符合所有標籤才命中(AND)。任一標籤名稱不存在時,結果必為空。</summary>
    All = 1,
}
