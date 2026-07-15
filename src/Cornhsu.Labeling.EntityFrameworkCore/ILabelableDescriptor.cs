using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>一個已註冊可標記型別的描述子:負責建表配置與該型別的查詢。</summary>
public interface ILabelableDescriptor
{
    /// <summary>可標記實體的 CLR 型別。</summary>
    Type ClrType { get; }

    /// <summary>持久化用的穩定型別鍵;會成為表名的一部分,不隨類別改名而變。</summary>
    string TypeKey { get; }

    /// <summary>把這個型別的 LabelLink_* 表配置到 model 上。</summary>
    /// <param name="builder">EF Core 的 ModelBuilder。</param>
    /// <param name="registry">標籤註冊表(提供表名前綴等設定)。</param>
    void ConfigureModel(ModelBuilder builder, LabelRegistry registry);

    /// <summary>跨型別查詢時,撈出這個型別命中的實體。</summary>
    /// <param name="db">DbContext。</param>
    /// <param name="labelIds">要比對的標籤 Id 集合。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<LabelHit>> QueryHitsAsync(
        DbContext db, IReadOnlyCollection<Guid> labelIds, CancellationToken ct);

    /// <summary>統計這個型別對每個標籤的使用次數。</summary>
    /// <param name="db">DbContext。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(DbContext db, CancellationToken ct);
}
