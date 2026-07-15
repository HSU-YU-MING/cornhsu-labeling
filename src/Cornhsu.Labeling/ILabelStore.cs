namespace Cornhsu.Labeling;

/// <summary>標籤系統的統一入口:標籤 CRUD、貼標/撕標、跨型別與強型別查詢。</summary>
public interface ILabelStore
{
    // ---- 標籤 CRUD ----

    /// <summary>建立新標籤。名稱已存在時拋出 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="name">顯示名稱,全域唯一。</param>
    /// <param name="color">顏色,建議 #RRGGBB。</param>
    /// <param name="parentId">父標籤 Id;null 表示頂層。</param>
    /// <param name="ct">取消權杖。</param>
    Task<Label> CreateAsync(string name, string? color = null, Guid? parentId = null, CancellationToken ct = default);

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

    /// <summary>統計每個標籤的使用次數(跨所有已註冊型別加總),供 tag cloud 等視覺化使用。</summary>
    /// <param name="ct">取消權杖。</param>
    Task<IReadOnlyDictionary<Guid, int>> GetUsageCountsAsync(CancellationToken ct = default);
}
