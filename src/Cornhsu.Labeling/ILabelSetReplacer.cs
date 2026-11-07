namespace Cornhsu.Labeling;

/// <summary>
/// Optional capability of an <see cref="ILabelStore"/>: replacing an entity's whole label set.
/// It is a separate interface so that adding it did not change <see cref="ILabelStore"/> or break other implementations of it.
/// Callers normally use <see cref="LabelStoreExtensions.ReplaceLabelsAsync{T}"/>; the EF Core store implements this interface.
/// </summary>
public interface ILabelSetReplacer
{
    /// <summary>
    /// Makes the entity carry exactly the given labels: labels not listed are detached, missing ones are attached,
    /// and links that stay keep their <c>AttachedAt</c>. All link changes go in one SaveChanges, so they apply together or not at all.
    /// An empty sequence, or one with only blank names, detaches every label; the labels themselves are never deleted.
    /// Labels that do not exist are handled as in <see cref="ILabelStore.AttachAsync{T}(T, IEnumerable{string}, CancellationToken)"/>
    /// (governed by AutoCreateLabels): labels that have to be auto-created are each saved first, on their own,
    /// so they remain even if saving the links then fails.
    /// If the entity already carries exactly these labels, nothing is saved.
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entity">Target entity (must already exist in the database when labels are being attached).</param>
    /// <param name="labelNames">The complete set of label names the entity should carry. Surrounding whitespace is trimmed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceLabelsAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default) where T : class, ILabelable;
}
