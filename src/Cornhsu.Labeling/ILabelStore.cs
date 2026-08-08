namespace Cornhsu.Labeling;

/// <summary>The single entry point to the labeling system: label CRUD, attach/detach, and cross-type or strongly typed queries.</summary>
public interface ILabelStore
{
    // ---- 標籤 CRUD ----

    /// <summary>Creates a new label. Throws <see cref="InvalidOperationException"/> if the name already exists.</summary>
    /// <param name="name">Display name; globally unique (surrounding whitespace is trimmed).</param>
    /// <param name="color">Color, preferably as #RRGGBB.</param>
    /// <param name="icon">Icon (emoji, icon name or short code) — purely visual.</param>
    /// <param name="parentId">Parent label id; null means top level.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Label> CreateAsync(string name, string? color = null, string? icon = null, Guid? parentId = null, CancellationToken ct = default);

    /// <summary>Finds a label by name; returns null when there is no match.</summary>
    /// <param name="name">Label name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Label?> FindAsync(string name, CancellationToken ct = default);

    /// <summary>Returns every label, ordered by SortOrder then Name.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Label>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Renames a label. Links store the label id, so no link is affected.</summary>
    /// <param name="labelId">Label id.</param>
    /// <param name="newName">New name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RenameAsync(Guid labelId, string newName, CancellationToken ct = default);

    /// <summary>
    /// Updates a label's fields (color, sort order, parent — and the name too, following the same
    /// rules as <see cref="RenameAsync"/>). Changes made to the Label inside <paramref name="update"/>
    /// are persisted; changing the parent is checked for cycles.
    /// </summary>
    /// <param name="labelId">Label id.</param>
    /// <param name="update">The mutation to apply, e.g. <c>l =&gt; l.Color = "#FF5722"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Label> UpdateAsync(Guid labelId, Action<Label> update, CancellationToken ct = default);

    /// <summary>Deletes a label. Links on every type are removed by cascade delete; throws if the label has children.</summary>
    /// <param name="labelId">Label id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(Guid labelId, CancellationToken ct = default);

    // ---- 貼標 / 撕標 ----

    /// <summary>Attaches labels to an entity. Missing labels are created automatically (get-or-create);
    /// attaching the same label twice is idempotent. Surrounding whitespace in names is trimmed.</summary>
    /// <typeparam name="T">A registered labelable type (one implementing <see cref="ILabelable{TKey}"/>).</typeparam>
    /// <param name="entity">Target entity (must already exist in the database).</param>
    /// <param name="labelNames">One or more label names.</param>
    Task AttachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable;

    /// <summary>Same as <see cref="AttachAsync{T}(T, string[])"/>, but accepts a cancellation token.</summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entity">Target entity (must already exist in the database).</param>
    /// <param name="labelNames">Label names.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AttachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>
    /// Bulk attach: applies the same set of labels to many entities (the "select several rows, tag them all
    /// as urgent" case). Labels are resolved once, existing links are read in one query, and everything is
    /// saved in a single SaveChanges. Idempotent — combinations that already exist are skipped.
    /// Behaviour for labels that do not exist matches <see cref="AttachAsync{T}(T, string[])"/> (governed by AutoCreateLabels).
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entities">Target entities (all must already exist in the database); an empty sequence returns immediately.</param>
    /// <param name="labelNames">Label names; an empty sequence returns immediately.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AttachManyAsync<T>(IEnumerable<T> entities, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable;

    /// <summary>Detaches labels from an entity. Labels that do not exist, or are not attached, are ignored.</summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entity">Target entity.</param>
    /// <param name="labelNames">One or more label names.</param>
    Task DetachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable;

    /// <summary>Same as <see cref="DetachAsync{T}(T, string[])"/>, but accepts a cancellation token.</summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entity">Target entity.</param>
    /// <param name="labelNames">Label names.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DetachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>Returns every label currently attached to the entity.</summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entity">Target entity.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Label>> GetLabelsOfAsync<T>(T entity, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>
    /// Reads the labels of many entities in a single query, which is how a list view avoids the N+1
    /// problem of calling <see cref="GetLabelsOfAsync{T}"/> per row.
    /// The returned dictionary is keyed by the entity *instances* passed in (reference equality);
    /// every entity is guaranteed to have an entry, empty when it has no labels.
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="entities">Target entities; an empty sequence returns an empty dictionary.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<T, IReadOnlyList<Label>>> GetLabelsOfManyAsync<T>(
        IEnumerable<T> entities, CancellationToken ct = default) where T : class, ILabelable;

    // ---- 查詢 ----

    /// <summary>Cross-type query: returns every entity carrying the given label, as <see cref="LabelHit"/> values.</summary>
    /// <param name="labelName">Label name.</param>
    /// <param name="includeDescendants">Whether entities matched via descendant labels are included.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<LabelHit>> FindByLabelAsync(string labelName, bool includeDescendants = true, CancellationToken ct = default);

    /// <summary>Single-type, strongly typed query: returns an <see cref="IQueryable{T}"/> you can keep composing Where/OrderBy/Skip onto.</summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="labelName">Label name.</param>
    /// <param name="includeDescendants">Whether entities matched via descendant labels are included.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IQueryable<T>> QueryByLabelAsync<T>(string labelName, bool includeDescendants = true, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>
    /// Multi-label cross-type query: "tagged both paper and urgent" (<see cref="LabelMatch.All"/>) or
    /// "tagged either paper or urgent" (<see cref="LabelMatch.Any"/>).
    /// When <paramref name="includeDescendants"/> is true, each name means "that label or any of its descendants".
    /// An empty sequence returns an empty result; under <see cref="LabelMatch.All"/>, a single missing name
    /// necessarily makes the result empty.
    /// </summary>
    /// <param name="labelNames">Label names.</param>
    /// <param name="match">Match mode (AND / OR); defaults to <see cref="LabelMatch.Any"/>.</param>
    /// <param name="includeDescendants">Whether entities matched via descendant labels are included.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<LabelHit>> FindByLabelsAsync(
        IEnumerable<string> labelNames, LabelMatch match = LabelMatch.Any,
        bool includeDescendants = true, CancellationToken ct = default);

    /// <summary>
    /// Multi-label single-type strongly typed query with the same semantics as <see cref="FindByLabelsAsync"/>;
    /// returns an <see cref="IQueryable{T}"/> you can keep composing Where/OrderBy/Skip onto.
    /// </summary>
    /// <typeparam name="T">A registered labelable type.</typeparam>
    /// <param name="labelNames">Label names.</param>
    /// <param name="match">Match mode (AND / OR); defaults to <see cref="LabelMatch.Any"/>.</param>
    /// <param name="includeDescendants">Whether entities matched via descendant labels are included.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IQueryable<T>> QueryByLabelsAsync<T>(
        IEnumerable<string> labelNames, LabelMatch match = LabelMatch.Any,
        bool includeDescendants = true, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>Counts how often each label is used, summed across every registered type — for tag clouds and similar visualizations.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<Guid, int>> GetUsageCountsAsync(CancellationToken ct = default);
}
