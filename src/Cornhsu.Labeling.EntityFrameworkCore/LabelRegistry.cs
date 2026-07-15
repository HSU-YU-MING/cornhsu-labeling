namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 可標記型別的註冊表。整個 App 必須只有一份(Singleton),
/// 因為 EF Core 的 model cache 以 DbContext 型別為 key,
/// 同一個 DbContext 拿到不同的 registry 會得到錯誤的快取 model。
/// </summary>
public sealed class LabelRegistry
{
    private readonly List<ILabelableDescriptor> _descriptors = new();
    private bool _sealed;

    /// <summary>所有已註冊型別的描述子。</summary>
    public IReadOnlyList<ILabelableDescriptor> Descriptors => _descriptors;

    /// <summary>Label 本體的表名,預設 "Label"。</summary>
    public string LabelTableName { get; set; } = "Label";

    /// <summary>連結表的表名前綴,預設 "LabelLink_";完整表名為前綴 + TypeKey。</summary>
    public string LinkTablePrefix { get; set; } = "LabelLink_";

    /// <summary>註冊一個可標記型別。</summary>
    /// <typeparam name="TEntity">實作 <see cref="ILabelable"/> 的實體型別。</typeparam>
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

        _descriptors.Add(new LabelableDescriptor<TEntity>(key, displayName));
        return this;
    }

    internal void Seal() => _sealed = true;

    internal ILabelableDescriptor Require<TEntity>() where TEntity : class, ILabelable
        => _descriptors.FirstOrDefault(d => d.ClrType == typeof(TEntity))
           ?? throw new InvalidOperationException(
               $"型別 {typeof(TEntity).Name} 未註冊。請在 AddLabeling 中呼叫 r.Labelable<{typeof(TEntity).Name}>()。");
}
