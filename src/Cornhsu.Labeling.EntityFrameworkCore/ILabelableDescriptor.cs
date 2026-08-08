namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// Public description of one registered labelable type.
/// The link-table operations themselves are an internal implementation detail, not public API.
/// </summary>
public interface ILabelableDescriptor
{
    /// <summary>CLR type of the labelable entity.</summary>
    Type ClrType { get; }

    /// <summary>The entity's key type, inferred from <see cref="ILabelable{TKey}"/>.</summary>
    Type KeyType { get; }

    /// <summary>Stable type key used for persistence; it becomes part of the table name and does not change when the class is renamed.</summary>
    string TypeKey { get; }
}
