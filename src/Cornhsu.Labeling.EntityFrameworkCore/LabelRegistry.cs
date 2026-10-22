namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// The registry of labelable types. There must be exactly one per application (Singleton),
/// because EF Core's model cache is keyed by DbContext type — handing the same DbContext a
/// different registry would give you a wrongly cached model.
/// <see cref="ServiceCollectionExtensions.AddLabeling{TContext}"/> and <see cref="LabelStoreFactory.Create{TContext}"/>
/// seal it: from then on the registry is immutable, and registering a type or setting any property throws
/// <see cref="InvalidOperationException"/>. Configure everything before that point.
/// </summary>
public sealed class LabelRegistry
{
    private readonly List<ILabelableOperations> _descriptors = new();
    private bool _sealed;
    private string _labelTableName = "Label";
    private string _linkTablePrefix = "LabelLink_";
    private bool _autoCreateLabels = true;

    /// <summary>Descriptors of every registered type (only the descriptive information is public).</summary>
    public IReadOnlyList<ILabelableDescriptor> Descriptors => _descriptors;

    /// <summary>內部管線視角(建表與連結表操作)。</summary>
    internal IReadOnlyList<ILabelableOperations> Operations => _descriptors;

    /// <summary>Table name for Label itself; defaults to "Label". Cannot be set once the registry is sealed.</summary>
    public string LabelTableName
    {
        get => _labelTableName;
        set => _labelTableName = Unsealed(nameof(LabelTableName), value);
    }

    /// <summary>
    /// Table-name prefix for link tables; defaults to "LabelLink_". The full name is prefix + TypeKey.
    /// Cannot be set once the registry is sealed.
    /// </summary>
    public string LinkTablePrefix
    {
        get => _linkTablePrefix;
        set => _linkTablePrefix = Unsealed(nameof(LinkTablePrefix), value);
    }

    /// <summary>
    /// Whether attaching an unknown label creates it automatically (get-or-create); defaults to true.
    /// Apps with a "curated" label set (labels carry colors/icons and are created deliberately through an
    /// admin screen) should set this to false: <c>AttachAsync</c> then throws a clear exception for an
    /// unknown label instead of silently creating a bare one with no color and no icon.
    /// Cannot be set once the registry is sealed.
    /// </summary>
    public bool AutoCreateLabels
    {
        get => _autoCreateLabels;
        set => _autoCreateLabels = Unsealed(nameof(AutoCreateLabels), value);
    }

    /// <summary>
    /// Registers a labelable type. The key type is inferred from <see cref="ILabelable{TKey}"/>,
    /// so only one type parameter is needed: <c>r.Labelable&lt;Note&gt;(n =&gt; n.Title)</c>.
    /// </summary>
    /// <typeparam name="TEntity">An entity type implementing <see cref="ILabelable{TKey}"/>.</typeparam>
    /// <param name="displayName">Projection that produces <see cref="LabelHit.DisplayName"/> for cross-type queries.</param>
    /// <param name="typeKey">Stable type key used for persistence; defaults to the class name. Pinning it explicitly is
    /// recommended so renaming the class does not rename the table. Generic entity types must pin it: the default
    /// is the CLR name (<c>Box`1</c> for <c>Box&lt;int&gt;</c>), which puts a backtick in the table name and is
    /// shared by every construction of the type, so a second one (<c>Box&lt;string&gt;</c>) cannot be registered.</param>
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

    /// <summary>
    /// 封存後 registry 整個不可變。表名與前綴會進 EF 的 model,而 model 以 DbContext 型別快取:
    /// 封存後再改,名稱會和快取的 model 默默對不上(沒有任何錯誤訊息);AutoCreateLabels 不影響 model,
    /// 但設定物件「封存後全部不可改」比較好理解。
    /// </summary>
    private T Unsealed<T>(string property, T value)
        => _sealed ? throw new InvalidOperationException($"LabelRegistry is sealed; {property} can no longer be changed.") : value;

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
