namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// The registry of labelable types. There must be exactly one per application (Singleton),
/// because EF Core's model cache is keyed by DbContext type — handing the same DbContext a
/// different registry would give you a wrongly cached model.
/// <see cref="ServiceCollectionExtensions.AddLabeling{TContext}"/> and <see cref="LabelStoreFactory.Create{TContext}"/>
/// seal it. From then on everything that goes into the EF model is fixed: registering a type, or changing
/// <see cref="LabelTableName"/> or <see cref="LinkTablePrefix"/> to a different value, throws
/// <see cref="InvalidOperationException"/>. <see cref="AutoCreateLabels"/> can still be changed at any time.
/// </summary>
public sealed class LabelRegistry
{
    private readonly List<ILabelableOperations> _descriptors = new();
    private volatile bool _sealed;   // 全 App 單例:封存與之後的註冊、設定可能在不同執行緒上
    private string _labelTableName = "Label";
    private string _linkTablePrefix = "LabelLink_";

    /// <summary>Descriptors of every registered type (only the descriptive information is public).</summary>
    public IReadOnlyList<ILabelableDescriptor> Descriptors => _descriptors;

    /// <summary>內部管線視角(建表與連結表操作)。</summary>
    internal IReadOnlyList<ILabelableOperations> Operations => _descriptors;

    /// <summary>
    /// Table name for Label itself; defaults to "Label". It goes into the EF model, so once the registry is
    /// sealed it cannot be changed (assigning the value it already has is allowed and does nothing).
    /// </summary>
    public string LabelTableName
    {
        get => _labelTableName;
        set => _labelTableName = Unsealed(nameof(LabelTableName), _labelTableName, value);
    }

    /// <summary>
    /// Table-name prefix for link tables; defaults to "LabelLink_". The full name is prefix + TypeKey.
    /// It goes into the EF model, so once the registry is sealed it cannot be changed (assigning the value it
    /// already has is allowed and does nothing).
    /// </summary>
    public string LinkTablePrefix
    {
        get => _linkTablePrefix;
        set => _linkTablePrefix = Unsealed(nameof(LinkTablePrefix), _linkTablePrefix, value);
    }

    /// <summary>
    /// Whether attaching an unknown label creates it automatically (get-or-create); defaults to true.
    /// Apps with a "curated" label set (labels carry colors/icons and are created deliberately through an
    /// admin screen) should set this to false: <c>AttachAsync</c> then throws a clear exception for an
    /// unknown label instead of silently creating a bare one with no color and no icon.
    /// It does not affect the EF model, so it can be changed at any time, also after the registry is sealed;
    /// the change applies to every store that shares the registry.
    /// </summary>
    public bool AutoCreateLabels { get; set; } = true;

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
    /// 封存後鎖住會進 EF model 的設定。表名與前綴會進 model,而 model 以 DbContext 型別快取:
    /// 封存後再改,名稱會和快取的 model 默默對不上(沒有任何錯誤訊息)。設成同一個值不會讓 model 對不上,
    /// 放行(共用的設定程式重跑、把預設值明寫出來都不該失敗)。AutoCreateLabels 不進 model,不在此列。
    /// </summary>
    private string Unsealed(string property, string current, string value)
        => _sealed && !string.Equals(current, value, StringComparison.Ordinal)
            ? throw new InvalidOperationException($"LabelRegistry is sealed; {property} can no longer be changed.")
            : value;

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
