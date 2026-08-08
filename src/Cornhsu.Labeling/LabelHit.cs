namespace Cornhsu.Labeling;

/// <summary>
/// One result of a cross-type query. Different types may use different key types (int, Guid, …),
/// so <see cref="EntityId"/> is an <see cref="object"/>; use <see cref="EntityIdAs{TKey}"/> when you want it typed.
/// </summary>
/// <param name="EntityClrType">CLR type of the matched entity.</param>
/// <param name="EntityTypeKey">Type key of the matched entity (the stable string used for persistence).</param>
/// <param name="EntityId">Primary key of the matched entity (boxed; its runtime type is that entity's key type).</param>
/// <param name="DisplayName">Display name, produced by the projection supplied at registration; may be null.</param>
public sealed record LabelHit(
    Type EntityClrType,
    string EntityTypeKey,
    object EntityId,
    string? DisplayName)
{
    /// <summary>Returns the primary key, typed. Throws <see cref="InvalidCastException"/> on a type mismatch.</summary>
    /// <typeparam name="TKey">The expected key type.</typeparam>
    public TKey EntityIdAs<TKey>() where TKey : notnull
        => EntityId is TKey key
            ? key
            : throw new InvalidCastException(
                $"the key of this hit ({EntityTypeKey}) is {EntityId.GetType().Name}, not {typeof(TKey).Name}.");
}
