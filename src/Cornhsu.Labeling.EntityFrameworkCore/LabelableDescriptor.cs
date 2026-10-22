using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

internal sealed class LabelableDescriptor<TEntity, TKey> : ILabelableOperations
    where TEntity : class, ILabelable<TKey>
    where TKey : notnull
{
    private readonly Func<TEntity, string?>? _displayName;

    /// <summary>
    /// 一次查詢裡 Contains 放的實體主鍵數上限。EF Core 10 把 Contains 展開成一個值一個參數,
    /// 超過資料庫的參數上限(SQLite 32,766、SQL Server 2,100)就拋例外;1,000 對所有 provider 都安全,
    /// 每批一次往返,幾萬筆也只是幾十次。
    /// </summary>
    internal const int MaxIdsPerQuery = 1_000;

    public Type ClrType => typeof(TEntity);
    public Type KeyType => typeof(TKey);
    public string TypeKey { get; }

    public LabelableDescriptor(string typeKey, Func<TEntity, string?>? displayName)
        => (TypeKey, _displayName) = (typeKey, displayName);

    public void ConfigureModel(ModelBuilder b, LabelRegistry r)
    {
        b.Entity<LabelLink<TEntity, TKey>>(e =>
        {
            e.ToTable($"{r.LinkTablePrefix}{TypeKey}");
            e.HasKey(x => new { x.LabelId, x.EntityId });   // 複合主鍵 → 天然防重複

            e.HasOne(x => x.Label).WithMany()
             .HasForeignKey(x => x.LabelId)
             .OnDelete(DeleteBehavior.Cascade);             // 刪標籤 → 連結消失

            e.HasOne(x => x.Entity).WithMany()
             .HasForeignKey(x => x.EntityId)
             .OnDelete(DeleteBehavior.Cascade);             // 刪實體 → 連結消失,不留孤兒

            e.HasIndex(x => x.EntityId);                    // 反查「這個實體有哪些標籤」
        });
    }

    public async Task<IReadOnlyList<LabelHit>> QueryHitsAsync(
        DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> labelIdGroups, LabelMatch match, CancellationToken ct)
    {
        var entities = await BuildEntityQuery(db, labelIdGroups, match)
            .ToListAsync(ct).ConfigureAwait(false);

        return entities
            .Select(e => new LabelHit(typeof(TEntity), TypeKey, e.Id, _displayName?.Invoke(e)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(DbContext db, CancellationToken ct)
        => await db.Set<LabelLink<TEntity, TKey>>()
            .GroupBy(l => l.LabelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);

    public async Task AddMissingLinksAsync(
        DbContext db, ILabelable entity, IReadOnlyList<Label> labels, TrackerSnapshot undo, CancellationToken ct)
    {
        var id = ((TEntity)entity).Id;
        var links = db.Set<LabelLink<TEntity, TKey>>();

        foreach (var label in labels)
        {
            // 複合主鍵直接 Find:同時看得到資料庫與尚未存檔的追蹤項目 → 冪等
            var existing = await links.FindAsync(new object[] { label.Id, id }, ct).ConfigureAwait(false);
            if (existing is not null) continue;

            var link = new LabelLink<TEntity, TKey>
            {
                LabelId = label.Id,
                EntityId = id,
                AttachedAt = DateTimeOffset.UtcNow,
            };
            links.Add(link);
            undo.Added(link);
        }
    }

    public async Task AddMissingLinksManyAsync(
        DbContext db, IReadOnlyList<ILabelable> entities, IReadOnlyList<Label> labels, TrackerSnapshot undo, CancellationToken ct)
    {
        var typed = entities.Cast<TEntity>().ToList();
        var entityIds = typed.Select(e => e.Id).Distinct().ToList();
        var labelIds = labels.Select(l => l.Id).ToList();
        var links = db.Set<LabelLink<TEntity, TKey>>();

        // 既有連結整批撈回(依實體分批,見 MaxIdsPerQuery),避免 N×M 次逐鍵查詢
        var have = new HashSet<(Guid LabelId, TKey EntityId)>();
        foreach (var batch in entityIds.Chunk(MaxIdsPerQuery))
        {
            var existing = await links.AsNoTracking()
                .Where(l => labelIds.Contains(l.LabelId) && batch.Contains(l.EntityId))
                .Select(l => new { l.LabelId, l.EntityId })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var x in existing)
                have.Add((x.LabelId, x.EntityId));
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entity in typed)
        {
            foreach (var label in labels)
            {
                if (!have.Add((label.Id, entity.Id))) continue;   // 已存在或本批已加 → 略過(冪等)
                var link = new LabelLink<TEntity, TKey>
                {
                    LabelId = label.Id,
                    EntityId = entity.Id,
                    AttachedAt = now,
                };
                links.Add(link);
                undo.Added(link);
            }
        }
    }

    public async Task RemoveLinksAsync(
        DbContext db, ILabelable entity, IReadOnlyCollection<Guid> labelIds, TrackerSnapshot undo, CancellationToken ct)
    {
        var id = ((TEntity)entity).Id;
        var links = db.Set<LabelLink<TEntity, TKey>>();

        foreach (var labelId in labelIds)
        {
            var link = await links.FindAsync(new object[] { labelId, id }, ct).ConfigureAwait(false);
            if (link is null) continue;
            undo.Capture(db.Entry(link), withValues: false);
            links.Remove(link);
        }
    }

    public async Task<IReadOnlyList<Label>> GetLabelsAsync(DbContext db, ILabelable entity, CancellationToken ct)
        => await db.Set<LabelLink<TEntity, TKey>>()
            .Where(EntityIdEquals(((TEntity)entity).Id))
            .Select(l => l.Label)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<ILabelable, IReadOnlyList<Label>>> GetLabelsManyAsync(
        DbContext db, IReadOnlyList<ILabelable> entities, CancellationToken ct)
    {
        var typed = entities.Cast<TEntity>().ToList();
        var ids = typed.Select(e => e.Id).Distinct().ToList();

        // 排序在資料庫做,和 GetLabelsAsync、GetAllAsync 一樣依 collation 排名稱;
        // 在記憶體用 Ordinal 排的話,不分大小寫的 collation 上同一個實體兩種讀法的順序會不同。
        // 依實體分批查(見 MaxIdsPerQuery):同一個實體的連結一定落在同一批,排序不受影響
        var byId = new Dictionary<TKey, IReadOnlyList<Label>>();
        foreach (var batch in ids.Chunk(MaxIdsPerQuery))
        {
            var pairs = await db.Set<LabelLink<TEntity, TKey>>()
                .Where(l => batch.Contains(l.EntityId))
                .Select(l => new { l.EntityId, l.Label })
                .OrderBy(p => p.Label.SortOrder).ThenBy(p => p.Label.Name)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var group in pairs.GroupBy(p => p.EntityId))   // GroupBy 保留群組內元素的原始順序,也就是資料庫排好的順序
                byId[group.Key] = group.Select(p => p.Label).ToList();
        }

        // 以實例(參考相等)為鍵:呼叫端傳什麼就用什麼當 key,每個實體都保證有項目
        var result = new Dictionary<ILabelable, IReadOnlyList<Label>>(ReferenceEqualityComparer.Instance);
        foreach (var e in typed)
            result[e] = byId.TryGetValue(e.Id, out var labels) ? labels : Array.Empty<Label>();
        return result;
    }

    public IQueryable CreateQueryByLabels(DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> labelIdGroups, LabelMatch match)
        => BuildEntityQuery(db, labelIdGroups, match);

    /// <summary>
    /// 組出「貼著指定標籤的實體」查詢。
    /// Any(或只有一個群組):所有 Id 合併成一個 IN。
    /// All:第一個群組為基底,其餘群組各以 IN 子查詢做交集
    /// (benchmark 實測:子查詢比 GroupBy/HAVING 慢一些但語意能正確涵蓋子孫群組)。
    /// </summary>
    private IQueryable<TEntity> BuildEntityQuery(
        DbContext db, IReadOnlyList<IReadOnlyCollection<Guid>> groups, LabelMatch match)
    {
        var lists = match == LabelMatch.Any || groups.Count == 1
            ? new List<List<Guid>> { groups.SelectMany(g => g).Distinct().ToList() }
            : groups.Select(g => g.ToList()).ToList();

        if (EfParameterLookup.ForGuidList is null || lists.All(ids => ids.Count <= MaxIdsPerQuery))
            return BuildEntityQuery(db, lists, asOneParameter: false);

        // 大集合改用 EF.Parameter 以一個參數送出(見 LabelIdIn)。但那要資料庫能拆 JSON 陣列:
        // SQL Server 的相容性層級低於 130 時沒有 OPENJSON,EF 會在翻譯時拋例外。先翻譯一次看看,
        // 翻不出來就退回 EF 的預設做法(EF 9 在那裡是內嵌常數、EF 10 是一個值一個參數),跟 1.1.0 一樣
        var query = BuildEntityQuery(db, lists, asOneParameter: true);
        try
        {
            query.ToQueryString();
            return query;
        }
        catch (InvalidOperationException)
        {
            return BuildEntityQuery(db, lists, asOneParameter: false);
        }
    }

    /// <summary>
    /// <paramref name="lists"/> 只有一份時是 Any(或單一群組):所有 Id 合併成一個 IN。
    /// 多份時是 All:第一份為基底,其餘各以 IN 子查詢做交集。
    /// </summary>
    private static IQueryable<TEntity> BuildEntityQuery(DbContext db, List<List<Guid>> lists, bool asOneParameter)
    {
        var links = db.Set<LabelLink<TEntity, TKey>>();
        var q = links.Where(LabelIdIn(lists[0], asOneParameter));
        for (var i = 1; i < lists.Count; i++)
        {
            var sub = links.Where(LabelIdIn(lists[i], asOneParameter)).Select(l2 => l2.EntityId);
            q = q.Where(l => sub.Contains(l.EntityId));
        }
        return q.Select(l => l.Entity).Distinct();
    }

    /// <summary>
    /// 組 <c>l =&gt; ids.Contains(l.LabelId)</c>。標籤 Id 集合(含子孫的一整棵子樹)可能有幾萬個,
    /// 而 EF Core 10 預設把 Contains 展開成一個值一個參數,超過資料庫的參數上限就拋例外。
    /// 這裡回傳的是要讓呼叫端接著組合的 IQueryable(QueryByLabelsAsync),只能是一條 SQL,不能像批次 API 那樣分批往返;
    /// 所以超過 <see cref="MaxIdsPerQuery"/> 時改用 <c>EF.Parameter(ids)</c>,讓整個集合以一個參數送出
    /// (EF Core 10 文件寫明的逐查詢覆寫:SQLite 的 json_each、SQL Server 的 OPENJSON、PostgreSQL 的陣列參數)。
    /// EF Core 8 沒有 EF.Parameter,也不需要:它的預設本來就是一個參數(SQL Server 相容性層級低於 130 時是內嵌常數)。
    /// 數量少時維持 EF 的預設(EF 10 的多參數對查詢計畫較好)。<paramref name="asOneParameter"/> 由呼叫端確認翻譯得出來才給 true。
    /// </summary>
    private static Expression<Func<LabelLink<TEntity, TKey>, bool>> LabelIdIn(List<Guid> ids, bool asOneParameter)
    {
        var link = Expression.Parameter(typeof(LabelLink<TEntity, TKey>), "l");
        Expression values = Expression.Field(
            Expression.Constant(new StrongBox<List<Guid>>(ids)), nameof(StrongBox<List<Guid>>.Value));
        if (asOneParameter && ids.Count > MaxIdsPerQuery && EfParameterLookup.ForGuidList is { } efParameter)
            values = Expression.Call(efParameter, values);
        var body = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(Guid) },
            values, Expression.Property(link, nameof(LabelLink<TEntity, TKey>.LabelId)));
        return Expression.Lambda<Func<LabelLink<TEntity, TKey>, bool>>(body, link);
    }

    /// <summary>
    /// 手工組 <c>l.EntityId == id</c> 的運算式:泛型 TKey 無法直接用 ==,
    /// 而以 StrongBox 包住值讓 EF 視為參數(而非常數),SQL 查詢計畫才能被快取。
    /// </summary>
    private static Expression<Func<LabelLink<TEntity, TKey>, bool>> EntityIdEquals(TKey id)
    {
        var parameter = Expression.Parameter(typeof(LabelLink<TEntity, TKey>), "l");
        var body = Expression.Equal(
            Expression.Property(parameter, nameof(LabelLink<TEntity, TKey>.EntityId)),
            Expression.Field(Expression.Constant(new StrongBox<TKey>(id)), nameof(StrongBox<TKey>.Value)));
        return Expression.Lambda<Func<LabelLink<TEntity, TKey>, bool>>(body, parameter);
    }
}
