namespace Cornhsu.Labeling;

/// <summary>跨型別查詢的回傳單位。</summary>
/// <param name="EntityClrType">命中實體的 CLR 型別。</param>
/// <param name="EntityTypeKey">命中實體的型別鍵(持久化用的穩定字串)。</param>
/// <param name="EntityId">命中實體的主鍵。</param>
/// <param name="DisplayName">顯示名稱;由註冊時提供的投影函式產生,可能為 null。</param>
public sealed record LabelHit(
    Type EntityClrType,
    string EntityTypeKey,
    Guid EntityId,
    string? DisplayName);
