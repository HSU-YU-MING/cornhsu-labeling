namespace Cornhsu.Labeling;

/// <summary>
/// 跨型別查詢的回傳單位。不同型別的主鍵型別可能不同(int、Guid…),
/// 所以 <see cref="EntityId"/> 是 <see cref="object"/>;需要強型別時用 <see cref="EntityIdAs{TKey}"/>。
/// </summary>
/// <param name="EntityClrType">命中實體的 CLR 型別。</param>
/// <param name="EntityTypeKey">命中實體的型別鍵(持久化用的穩定字串)。</param>
/// <param name="EntityId">命中實體的主鍵(boxed;實際型別為該實體的主鍵型別)。</param>
/// <param name="DisplayName">顯示名稱;由註冊時提供的投影函式產生,可能為 null。</param>
public sealed record LabelHit(
    Type EntityClrType,
    string EntityTypeKey,
    object EntityId,
    string? DisplayName)
{
    /// <summary>取出強型別的主鍵。型別不符時拋出 <see cref="InvalidCastException"/>。</summary>
    /// <typeparam name="TKey">預期的主鍵型別。</typeparam>
    public TKey EntityIdAs<TKey>() where TKey : notnull
        => EntityId is TKey key
            ? key
            : throw new InvalidCastException(
                $"這筆命中({EntityTypeKey})的主鍵是 {EntityId.GetType().Name},不是 {typeof(TKey).Name}。");
}
