namespace Cornhsu.Labeling;

/// <summary>
/// Optional capability of an <see cref="ILabelStore"/>: querying entities that do not carry certain labels.
/// Callers normally use <see cref="LabelStoreExtensions.QueryWithoutLabelsAsync{T}"/>; the EF Core store implements this interface.
/// </summary>
public interface ILabelExclusionQuery
{
    /// <summary>
    /// Returns the entities carrying none of the given labels, as an <see cref="IQueryable{T}"/> you can keep composing onto.
    /// Entities with no labels at all are included. Names that do not exist are ignored, so if none of the names exist,
    /// or the sequence is empty, every entity is returned.
    /// When <paramref name="includeDescendants"/> is true, each name means "that label or any of its descendants".
    /// As with <see cref="ILabelStore.QueryByLabelsAsync{T}"/>, names and their descendants are resolved during the call;
    /// for current results after labels change, call the method again.
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="labelNames">Label names to exclude.</param>
    /// <param name="includeDescendants">Whether entities carrying a descendant label are excluded too.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IQueryable<T>> QueryWithoutLabelsAsync<T>(
        IEnumerable<string> labelNames, bool includeDescendants = true, CancellationToken ct = default) where T : class, ILabelable;
}
