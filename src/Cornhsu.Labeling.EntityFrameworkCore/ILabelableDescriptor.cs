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

    /// <summary>
    /// Type key used for persistence; it becomes part of the link table's name. It defaults to the class name,
    /// so renaming the class changes it — and the table — unless it was pinned with the <c>typeKey</c> argument
    /// of <see cref="LabelRegistry.Labelable{TEntity}"/>.
    /// </summary>
    string TypeKey { get; }
}
