using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 描述子的內部管線:建表配置與所有針對連結表的操作。
/// 刻意不公開——公開後每個成員都是終身 API 承諾,而這些簽名純屬實作細節。
/// </summary>
internal interface ILabelableOperations : ILabelableDescriptor
{
    /// <summary>把這個型別的 LabelLink_* 表配置到 model 上。</summary>
    void ConfigureModel(ModelBuilder builder, LabelRegistry registry);

    /// <summary>
    /// 跨型別查詢時,撈出這個型別命中的實體。
    /// 每個群組代表一個名稱解析出的標籤 Id(含子孫);
    /// <see cref="LabelMatch.All"/> 表示每個群組都至少要命中一個標籤。
    /// </summary>
    Task<IReadOnlyList<LabelHit>> QueryHitsAsync(
        DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> labelIdGroups, LabelMatch match, CancellationToken ct);

    /// <summary>統計這個型別對每個標籤的使用次數。</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(DbContext db, CancellationToken ct);

    /// <summary>為實體補上尚未存在的連結(冪等;不呼叫 SaveChanges)。</summary>
    Task AddMissingLinksAsync(DbContext db, ILabelable entity, IReadOnlyList<Label> labels, CancellationToken ct);

    /// <summary>移除實體與指定標籤之間的連結(不呼叫 SaveChanges)。</summary>
    Task RemoveLinksAsync(DbContext db, ILabelable entity, IReadOnlyCollection<Guid> labelIds, CancellationToken ct);

    /// <summary>取得實體目前貼著的所有標籤。</summary>
    Task<IReadOnlyList<Label>> GetLabelsAsync(DbContext db, ILabelable entity, CancellationToken ct);

    /// <summary>
    /// 批次取得多個實體各自的標籤(一次查詢)。
    /// 回傳字典以傳入的實體實例為鍵(參考相等),每個實體都有對應項目。
    /// </summary>
    Task<IReadOnlyDictionary<ILabelable, IReadOnlyList<Label>>> GetLabelsManyAsync(
        DbContext db, IReadOnlyList<ILabelable> entities, CancellationToken ct);

    /// <summary>建立「貼著指定標籤的實體」查詢;群組語意同 <see cref="QueryHitsAsync"/>。
    /// 實際回傳 <c>IQueryable&lt;TEntity&gt;</c>。</summary>
    IQueryable CreateQueryByLabels(DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> labelIdGroups, LabelMatch match);
}
