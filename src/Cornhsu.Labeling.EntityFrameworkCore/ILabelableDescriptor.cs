using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 一個已註冊可標記型別的描述子:封裝該型別的主鍵型別、建表配置與所有針對連結表的操作。
/// 主鍵型別在註冊時自動推斷,所以 <see cref="ILabelStore"/> 的公開 API 不需要第二個型別參數。
/// </summary>
public interface ILabelableDescriptor
{
    /// <summary>可標記實體的 CLR 型別。</summary>
    Type ClrType { get; }

    /// <summary>實體的主鍵型別(自 <see cref="ILabelable{TKey}"/> 推斷)。</summary>
    Type KeyType { get; }

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

    /// <summary>為實體補上尚未存在的連結(冪等;不呼叫 SaveChanges)。</summary>
    /// <param name="db">DbContext。</param>
    /// <param name="entity">目標實體(實際型別必須是 <see cref="ClrType"/>)。</param>
    /// <param name="labels">要貼上的標籤。</param>
    /// <param name="ct">取消權杖。</param>
    Task AddMissingLinksAsync(DbContext db, ILabelable entity, IReadOnlyList<Label> labels, CancellationToken ct);

    /// <summary>移除實體與指定標籤之間的連結(不呼叫 SaveChanges)。</summary>
    /// <param name="db">DbContext。</param>
    /// <param name="entity">目標實體。</param>
    /// <param name="labelIds">要撕下的標籤 Id 集合。</param>
    /// <param name="ct">取消權杖。</param>
    Task RemoveLinksAsync(DbContext db, ILabelable entity, IReadOnlyCollection<Guid> labelIds, CancellationToken ct);

    /// <summary>取得實體目前貼著的所有標籤。</summary>
    /// <param name="db">DbContext。</param>
    /// <param name="entity">目標實體。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<Label>> GetLabelsAsync(DbContext db, ILabelable entity, CancellationToken ct);

    /// <summary>建立「貼著指定標籤的實體」查詢;實際回傳 <c>IQueryable&lt;TEntity&gt;</c>。</summary>
    /// <param name="db">DbContext。</param>
    /// <param name="labelIds">要比對的標籤 Id 集合。</param>
    IQueryable CreateQueryByLabels(DbContext db, IReadOnlyCollection<Guid> labelIds);
}
