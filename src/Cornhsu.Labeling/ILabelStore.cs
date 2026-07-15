namespace Cornhsu.Labeling;

/// <summary>標籤系統的統一入口:標籤 CRUD、貼標/撕標、跨型別與強型別查詢。</summary>
public interface ILabelStore
{
    // ---- 標籤 CRUD ----

    /// <summary>建立新標籤。名稱已存在時拋出 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="name">顯示名稱,全域唯一(自動去除前後空白)。</param>
    /// <param name="color">顏色,建議 #RRGGBB。</param>
    /// <param name="icon">圖示(emoji / 圖示名稱 / 短碼,純視覺)。</param>
    /// <param name="parentId">父標籤 Id;null 表示頂層。</param>
    /// <param name="ct">取消權杖。</param>
    Task<Label> CreateAsync(string name, string? color = null, string? icon = null, Guid? parentId = null, CancellationToken ct = default);

    /// <summary>依名稱尋找標籤;找不到回傳 null。</summary>
    /// <param name="name">標籤名稱。</param>
    /// <param name="ct">取消權杖。</param>
    Task<Label?> FindAsync(string name, CancellationToken ct = default);

    /// <summary>取得所有標籤(依 SortOrder、Name 排序)。</summary>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<Label>> GetAllAsync(CancellationToken ct = default);

    /// <summary>重新命名標籤。因為連結存的是 Id,所有連結不受影響。</summary>
    /// <param name="labelId">標籤 Id。</param>
    /// <param name="newName">新名稱。</param>
    /// <param name="ct">取消權杖。</param>
    Task RenameAsync(Guid labelId, string newName, CancellationToken ct = default);

    /// <summary>
    /// 更新標籤欄位(顏色、排序、父標籤,也允許改名——改名規則與 <see cref="RenameAsync"/> 相同)。
    /// 對 <paramref name="update"/> 內的 Label 所做的修改會被存檔;
    /// 變更父標籤時會檢查不可形成循環。
    /// </summary>
    /// <param name="labelId">標籤 Id。</param>
    /// <param name="update">修改動作,例如 <c>l =&gt; l.Color = "#FF5722"</c>。</param>
    /// <param name="ct">取消權杖。</param>
    Task<Label> UpdateAsync(Guid labelId, Action<Label> update, CancellationToken ct = default);

    /// <summary>刪除標籤。所有型別上的連結由 cascade delete 自動清除;有子標籤時拋出例外。</summary>
    /// <param name="labelId">標籤 Id。</param>
    /// <param name="ct">取消權杖。</param>
    Task DeleteAsync(Guid labelId, CancellationToken ct = default);

    // ---- 貼標 / 撕標 ----

    /// <summary>把標籤貼到實體上。標籤不存在會自動建立(get-or-create);重複貼標為冪等操作。
    /// 名稱會自動去除前後空白。</summary>
    /// <typeparam name="T">已註冊的可標記型別(實作 <see cref="ILabelable{TKey}"/>)。</typeparam>
    /// <param name="entity">目標實體(必須已存在於資料庫)。</param>
    /// <param name="labelNames">標籤名稱,可多個。</param>
    Task AttachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable;

    /// <summary>同 <see cref="AttachAsync{T}(T, string[])"/>,可傳入取消權杖。</summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="entity">目標實體(必須已存在於資料庫)。</param>
    /// <param name="labelNames">標籤名稱集合。</param>
    /// <param name="ct">取消權杖。</param>
    Task AttachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>
    /// 批次貼標:把同一組標籤貼到多個實體上(清單多選後「全部加上急件」的場景)。
    /// 標籤只解析一次、既有連結一次查詢、單次 SaveChanges;冪等,已貼過的組合自動略過。
    /// 標籤不存在時的行為與 <see cref="AttachAsync{T}(T, string[])"/> 相同(依 AutoCreateLabels)。
    /// </summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="entities">目標實體集合(都必須已存在於資料庫);空集合直接返回。</param>
    /// <param name="labelNames">標籤名稱集合;空集合直接返回。</param>
    /// <param name="ct">取消權杖。</param>
    Task AttachManyAsync<T>(IEnumerable<T> entities, IEnumerable<string> labelNames, CancellationToken ct = default)
        where T : class, ILabelable;

    /// <summary>把標籤從實體上撕下。不存在的標籤或未貼上的標籤會被忽略。</summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="entity">目標實體。</param>
    /// <param name="labelNames">標籤名稱,可多個。</param>
    Task DetachAsync<T>(T entity, params string[] labelNames) where T : class, ILabelable;

    /// <summary>同 <see cref="DetachAsync{T}(T, string[])"/>,可傳入取消權杖。</summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="entity">目標實體。</param>
    /// <param name="labelNames">標籤名稱集合。</param>
    /// <param name="ct">取消權杖。</param>
    Task DetachAsync<T>(T entity, IEnumerable<string> labelNames, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>取得實體目前貼著的所有標籤。</summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="entity">目標實體。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<Label>> GetLabelsOfAsync<T>(T entity, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>
    /// 批次取得多個實體各自的標籤(一次查詢),解掉清單畫面逐筆呼叫
    /// <see cref="GetLabelsOfAsync{T}"/> 的 N+1 問題。
    /// 回傳字典以傳入的實體「實例」為鍵(參考相等),每個實體都保證有對應項目,
    /// 沒有標籤時為空清單。
    /// </summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="entities">目標實體集合;空集合回傳空字典。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyDictionary<T, IReadOnlyList<Label>>> GetLabelsOfManyAsync<T>(
        IEnumerable<T> entities, CancellationToken ct = default) where T : class, ILabelable;

    // ---- 查詢 ----

    /// <summary>跨型別查詢:回傳所有貼著指定標籤的實體(以 <see cref="LabelHit"/> 表示)。</summary>
    /// <param name="labelName">標籤名稱。</param>
    /// <param name="includeDescendants">是否包含子孫標籤命中的實體。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<LabelHit>> FindByLabelAsync(string labelName, bool includeDescendants = true, CancellationToken ct = default);

    /// <summary>單型別強型別查詢:回傳可續接 Where/OrderBy/Skip 的 <see cref="IQueryable{T}"/>。</summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="labelName">標籤名稱。</param>
    /// <param name="includeDescendants">是否包含子孫標籤命中的實體。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IQueryable<T>> QueryByLabelAsync<T>(string labelName, bool includeDescendants = true, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>
    /// 多標籤跨型別查詢:「同時標了論文+急件」(<see cref="LabelMatch.All"/>)或
    /// 「標了論文或急件任一」(<see cref="LabelMatch.Any"/>)。
    /// <paramref name="includeDescendants"/> 為 true 時,每個名稱代表「該標籤或其任一子孫」。
    /// 空集合回傳空結果;<see cref="LabelMatch.All"/> 模式下任一名稱不存在時結果必為空。
    /// </summary>
    /// <param name="labelNames">標籤名稱集合。</param>
    /// <param name="match">匹配模式(AND / OR),預設 <see cref="LabelMatch.Any"/>。</param>
    /// <param name="includeDescendants">是否包含子孫標籤命中的實體。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyList<LabelHit>> FindByLabelsAsync(
        IEnumerable<string> labelNames, LabelMatch match = LabelMatch.Any,
        bool includeDescendants = true, CancellationToken ct = default);

    /// <summary>
    /// 多標籤單型別強型別查詢,語意同 <see cref="FindByLabelsAsync"/>;
    /// 回傳可續接 Where/OrderBy/Skip 的 <see cref="IQueryable{T}"/>。
    /// </summary>
    /// <typeparam name="T">已註冊的可標記型別。</typeparam>
    /// <param name="labelNames">標籤名稱集合。</param>
    /// <param name="match">匹配模式(AND / OR),預設 <see cref="LabelMatch.Any"/>。</param>
    /// <param name="includeDescendants">是否包含子孫標籤命中的實體。</param>
    /// <param name="ct">取消權杖。</param>
    Task<IQueryable<T>> QueryByLabelsAsync<T>(
        IEnumerable<string> labelNames, LabelMatch match = LabelMatch.Any,
        bool includeDescendants = true, CancellationToken ct = default) where T : class, ILabelable;

    /// <summary>統計每個標籤的使用次數(跨所有已註冊型別加總),供 tag cloud 等視覺化使用。</summary>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyDictionary<Guid, int>> GetUsageCountsAsync(CancellationToken ct = default);
}
