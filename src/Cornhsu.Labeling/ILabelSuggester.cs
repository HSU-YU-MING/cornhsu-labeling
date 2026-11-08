namespace Cornhsu.Labeling;

/// <summary>
/// Optional capability of an <see cref="ILabelStore"/>: suggesting labels by name prefix.
/// Callers normally use <see cref="LabelStoreExtensions.SuggestLabelsAsync"/>; the EF Core store implements this interface.
/// </summary>
public interface ILabelSuggester
{
    /// <summary>
    /// Returns labels whose name starts with the prefix, ordered by SortOrder then Name, without loading every label:
    /// what an autocomplete box needs. A null or blank prefix returns an empty list.
    /// Matching is done by the database, so case sensitivity depends on the provider and collation
    /// (for example, SQLite's LIKE ignores ASCII case and PostgreSQL's does not).
    /// </summary>
    /// <param name="prefix">What the user has typed so far. Surrounding whitespace is trimmed.</param>
    /// <param name="limit">The most labels to return; must be at least 1.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Label>> SuggestLabelsAsync(string prefix, int limit = 10, CancellationToken ct = default);
}
