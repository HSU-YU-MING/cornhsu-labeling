namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 可標記型別的註冊表。整個 App 必須只有一份(Singleton),
/// 因為 EF Core 的 model cache 以 DbContext 型別為 key,
/// 同一個 DbContext 拿到不同的 registry 會得到錯誤的快取 model。
/// </summary>
public sealed class LabelRegistry
{
    private readonly List<ILabelableOperations> _descriptors = new();
    private bool _sealed;

    /// <summary>所有已註冊型別的描述子(公開的僅為描述資訊)。</summary>
    public IReadOnlyList<ILabelableDescriptor> Descriptors => _descriptors;

    /// <summary>內部管線視角(建表與連結表操作)。</summary>
    internal IReadOnlyList<ILabelableOperations> Operations => _descriptors;

    /// <summary>Label 本體的表名,預設 "Label"。</summary>
    public string LabelTableName { get; set; } = "Label";

    /// <summary>連結表的表名前綴,預設 "LabelLink_";完整表名為前綴 + TypeKey。</summary>
    public string LinkTablePrefix { get; set; } = "LabelLink_";

    /// <summary>
    /// 貼標時遇到不存在的標籤是否自動建立(get-or-create),預設 true。
    /// 「策展式」標籤的 App(標籤帶顏色/圖示、由管理介面精心建立)建議設為 false:
    /// <c>AttachAsync</c> 遇到不存在的標籤會拋出清楚的例外,
    /// 而不是默默建立一個沒有顏色、沒有圖示的裸標籤。
    /// </summary>
    public bool AutoCreateLabels { get; set; } = true;

    /// <summary>
    /// 註冊一個可標記型別。主鍵型別自 <see cref="ILabelable{TKey}"/> 自動推斷,
    /// 所以只需要一個型別參數:<c>r.Labelable&lt;Note&gt;(n =&gt; n.Title)</c>。
    /// </summary>
    /// <typeparam name="TEntity">實作 <see cref="ILabelable{TKey}"/> 的實體型別。</typeparam>
    /// <param name="displayName">跨型別查詢時產生 <see cref="LabelHit.DisplayName"/> 的投影函式。</param>
    /// <param name="typeKey">持久化用的穩定型別鍵;預設用類別名稱,建議明確釘住以免改名時表名跟著變。</param>
    public LabelRegistry Labelable<TEntity>(
        Func<TEntity, string?>? displayName = null,
        string? typeKey = null)
        where TEntity : class, ILabelable
    {
        if (_sealed) throw new InvalidOperationException("LabelRegistry 已封存,無法再註冊型別。");

        var key = typeKey ?? typeof(TEntity).Name;
        if (_descriptors.Any(d => d.TypeKey == key))
            throw new InvalidOperationException($"TypeKey '{key}' 已被註冊。");
        if (_descriptors.Any(d => d.ClrType == typeof(TEntity)))
            throw new InvalidOperationException($"型別 {typeof(TEntity).Name} 已被註冊。");

        _descriptors.Add(CreateDescriptor<TEntity>(key, displayName));
        return this;
    }

    internal void Seal() => _sealed = true;

    internal ILabelableOperations Require<TEntity>() where TEntity : class, ILabelable
        => _descriptors.FirstOrDefault(d => d.ClrType == typeof(TEntity))
           ?? throw new InvalidOperationException(
               $"型別 {typeof(TEntity).Name} 未註冊。請在 AddLabeling 中呼叫 r.Labelable<{typeof(TEntity).Name}>()。");

    /// <summary>從 TEntity 實作的 ILabelable&lt;TKey&gt; 推斷主鍵型別,建立對應的封閉泛型描述子。</summary>
    private static ILabelableOperations CreateDescriptor<TEntity>(string typeKey, Func<TEntity, string?>? displayName)
        where TEntity : class, ILabelable
    {
        var keyInterfaces = typeof(TEntity).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ILabelable<>))
            .ToList();

        if (keyInterfaces.Count == 0)
            throw new InvalidOperationException(
                $"型別 {typeof(TEntity).Name} 只實作了非泛型的 ILabelable(marker)。" +
                $"請改實作 ILabelable<TKey> 並指定主鍵型別,例如 ILabelable<int> 或 ILabelable<Guid>。");

        if (keyInterfaces.Count > 1)
            throw new InvalidOperationException(
                $"型別 {typeof(TEntity).Name} 實作了多個 ILabelable<TKey>" +
                $"({string.Join("、", keyInterfaces.Select(i => i.GetGenericArguments()[0].Name))})," +
                $"無法推斷主鍵型別。請只保留一個。");

        var keyType = keyInterfaces[0].GetGenericArguments()[0];
        var descriptorType = typeof(LabelableDescriptor<,>).MakeGenericType(typeof(TEntity), keyType);
        return (ILabelableOperations)Activator.CreateInstance(descriptorType, typeKey, displayName)!;
    }
}
