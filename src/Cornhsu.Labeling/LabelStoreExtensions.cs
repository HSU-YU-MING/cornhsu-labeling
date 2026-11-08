namespace Cornhsu.Labeling;

/// <summary>
/// Operations added after 1.0. They are extension methods over optional interfaces so that <see cref="ILabelStore"/> itself stays
/// unchanged. Each throws <see cref="NotSupportedException"/> if the store does not implement the interface it needs.
/// </summary>
public static class LabelStoreExtensions
{
    /// <summary>
    /// Makes the entity carry exactly the given labels, in one save. See <see cref="ILabelSetReplacer.ReplaceLabelsAsync{T}"/> for the details.
    /// There is deliberately no <c>params</c> overload: leaving out the names must not clear the entity's labels.
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="store">The label store.</param>
    /// <param name="entity">Target entity (must already exist in the database when labels are being attached).</param>
    /// <param name="labelNames">The complete set of label names the entity should carry. Surrounding whitespace is trimmed.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task ReplaceLabelsAsync<T>(this ILabelStore store, T entity, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
        => Require<ILabelSetReplacer>(store, "replacing labels").ReplaceLabelsAsync(entity, labelNames, ct);

    /// <summary>Returns labels whose name starts with the prefix. See <see cref="ILabelSuggester.SuggestLabelsAsync"/> for the details.</summary>
    /// <param name="store">The label store.</param>
    /// <param name="prefix">What the user has typed so far. Surrounding whitespace is trimmed.</param>
    /// <param name="limit">The most labels to return; must be at least 1.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<IReadOnlyList<Label>> SuggestLabelsAsync(this ILabelStore store, string prefix, int limit = 10, CancellationToken ct = default)
        => Require<ILabelSuggester>(store, "suggesting labels").SuggestLabelsAsync(prefix, limit, ct);

    /// <summary>Returns the entities carrying none of the given labels. See <see cref="ILabelExclusionQuery.QueryWithoutLabelsAsync{T}"/> for the details.</summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="store">The label store.</param>
    /// <param name="labelNames">Label names to exclude.</param>
    /// <param name="includeDescendants">Whether entities carrying a descendant label are excluded too.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<IQueryable<T>> QueryWithoutLabelsAsync<T>(
        this ILabelStore store, IEnumerable<string> labelNames, bool includeDescendants = true, CancellationToken ct = default)
        where T : class, ILabelable
        => Require<ILabelExclusionQuery>(store, "excluding labels").QueryWithoutLabelsAsync<T>(labelNames, includeDescendants, ct);

    private static TCapability Require<TCapability>(ILabelStore store, string operation) where TCapability : class
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        return store as TCapability
            ?? throw new NotSupportedException($"{store.GetType().Name} does not support {operation}; implement {typeof(TCapability).Name}.");
    }
}
