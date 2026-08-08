namespace Cornhsu.Labeling;

/// <summary>
/// Non-generic marker base for labelable entities. Do not implement this interface directly —
/// implement <see cref="ILabelable{TKey}"/> with your primary key type instead, e.g.
/// <c>class Note : ILabelable&lt;int&gt;</c> or <c>class Memo : ILabelable&lt;Guid&gt;</c>.
/// It exists so the API needs only one type parameter (the key type is inferred at registration).
/// </summary>
public interface ILabelable
{
}

/// <summary>Implemented by any entity that wants to be labelable. Deliberately requires no base class.</summary>
/// <typeparam name="TKey">
/// The entity's primary key type. Any type with equality comparison works — <see cref="int"/>,
/// <see cref="long"/>, <see cref="Guid"/>, <see cref="string"/> and so on.
/// </typeparam>
public interface ILabelable<TKey> : ILabelable
    where TKey : notnull
{
    /// <summary>The entity's primary key.</summary>
    TKey Id { get; }
}
