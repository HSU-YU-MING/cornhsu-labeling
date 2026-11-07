namespace Cornhsu.Labeling;

/// <summary>Operations added after 1.0. They are extension methods so that <see cref="ILabelStore"/> itself stays unchanged.</summary>
public static class LabelStoreExtensions
{
    /// <summary>
    /// Makes the entity carry exactly the given labels, in one save. See <see cref="ILabelSetReplacer.ReplaceLabelsAsync{T}"/> for the details.
    /// There is deliberately no <c>params</c> overload: leaving out the names must not clear the entity's labels.
    /// Throws <see cref="NotSupportedException"/> if the store does not implement <see cref="ILabelSetReplacer"/>.
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="store">The label store.</param>
    /// <param name="entity">Target entity (must already exist in the database when labels are being attached).</param>
    /// <param name="labelNames">The complete set of label names the entity should carry. Surrounding whitespace is trimmed.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task ReplaceLabelsAsync<T>(this ILabelStore store, T entity, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (store is not ILabelSetReplacer replacer)
            throw new NotSupportedException($"{store.GetType().Name} does not support replacing labels; implement {nameof(ILabelSetReplacer)}.");
        return replacer.ReplaceLabelsAsync(entity, labelNames, ct);
    }
}
