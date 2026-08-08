namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// The registry of labelable types. There must be exactly one per application (Singleton),
/// because EF Core's model cache is keyed by DbContext type — handing the same DbContext a
/// different registry would give you a wrongly cached model.
/// </summary>
public sealed class LabelRegistry
{
    private readonly List<ILabelableOperations> _descriptors = new();
    private bool _sealed;

    /// <summary>Descriptors of every registered type (only the descriptive information is public).</summary>
    public IReadOnlyList<ILabelableDescriptor> Descriptors => _descriptors;

    /// <summary>內部管線視角(建表與連結表操作)。</summary>
    internal IReadOnlyList<ILabelableOperations> Operations => _descriptors;

    /// <summary>Table name for Label itself; defaults to "Label".</summary>
    public string LabelTableName { get; set; } = "Label";

    /// <summary>Table-name prefix for link tables; defaults to "LabelLink_". The full name is prefix + TypeKey.</summary>
    public string LinkTablePrefix { get; set; } = "LabelLink_";

    /// <summary>
    /// Whether attaching an unknown label creates it automatically (get-or-create); defaults to true.
    /// Apps with a "curated" label set (labels carry colors/icons and are created deliberately through an
    /// admin screen) should set this to false: <c>AttachAsync</c> then throws a clear exception for an
    /// unknown label instead of silently creating a bare one with no color and no icon.
    /// </summary>
    public bool AutoCreateLabels { get; set; } = true;

    /// <summary>
    /// Registers a labelable type. The key type is inferred from <see cref="ILabelable{TKey}"/>,
    /// so only one type parameter is needed: <c>r.Labelable&lt;Note&gt;(n =&gt; n.Title)</c>.
    /// </summary>
    /// <typeparam name="TEntity">An entity type implementing <see cref="ILabelable{TKey}"/>.</typeparam>
    /// <param name="displayName">Projection that produces <see cref="LabelHit.DisplayName"/> for cross-type queries.</param>
    /// <param name="typeKey">Stable type key used for persistence; defaults to the class name. Pinning it explicitly is
    /// recommended so renaming the class does not rename the table.</param>
    public LabelRegistry Labelable<TEntity>(
        Func<TEntity, string?>? displayName = null,
        string? typeKey = null)
        where TEntity : class, ILabelable
    {
        if (_sealed) throw new InvalidOperationException("LabelRegistry is sealed; no further types can be registered.");

        var key = typeKey ?? typeof(TEntity).Name;
        if (_descriptors.Any(d => d.TypeKey == key))
            throw new InvalidOperationException($"TypeKey '{key}' is already registered.");
        if (_descriptors.Any(d => d.ClrType == typeof(TEntity)))
            throw new InvalidOperationException($"type {typeof(TEntity).Name} is already registered.");

        _descriptors.Add(CreateDescriptor<TEntity>(key, displayName));
        return this;
    }

    internal void Seal() => _sealed = true;

    internal ILabelableOperations Require<TEntity>() where TEntity : class, ILabelable
        => _descriptors.FirstOrDefault(d => d.ClrType == typeof(TEntity))
           ?? throw new InvalidOperationException(
               $"type {typeof(TEntity).Name} is not registered. Call r.Labelable<{typeof(TEntity).Name}>() inside AddLabeling.");

    /// <summary>從 TEntity 實作的 ILabelable&lt;TKey&gt; 推斷主鍵型別,建立對應的封閉泛型描述子。</summary>
    private static ILabelableOperations CreateDescriptor<TEntity>(string typeKey, Func<TEntity, string?>? displayName)
        where TEntity : class, ILabelable
    {
        var keyInterfaces = typeof(TEntity).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ILabelable<>))
            .ToList();

        if (keyInterfaces.Count == 0)
            throw new InvalidOperationException(
                $"type {typeof(TEntity).Name} only implements the non-generic ILabelable marker. " +
                $"Implement ILabelable<TKey> with your key type instead, e.g. ILabelable<int> or ILabelable<Guid>.");

        if (keyInterfaces.Count > 1)
            throw new InvalidOperationException(
                $"type {typeof(TEntity).Name} implements several ILabelable<TKey> interfaces " +
                $"({string.Join(", ", keyInterfaces.Select(i => i.GetGenericArguments()[0].Name))}), " +
                $"so the key type cannot be inferred. Keep exactly one.");

        var keyType = keyInterfaces[0].GetGenericArguments()[0];
        var descriptorType = typeof(LabelableDescriptor<,>).MakeGenericType(typeof(TEntity), keyType);
        return (ILabelableOperations)Activator.CreateInstance(descriptorType, typeKey, displayName)!;
    }
}
