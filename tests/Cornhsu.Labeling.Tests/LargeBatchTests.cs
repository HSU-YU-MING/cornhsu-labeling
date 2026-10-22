using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 一次給幾萬個實體的批次 API,以及子樹裡有幾萬個標籤的名稱查詢。EF Core 10 把 <c>Contains</c> 展開成一個值一個參數,
/// SQLite 上超過參數上限(32,766)就拋例外(EF 10 + SQL Server 參數太多時會自己改用一個 JSON 參數,不會拋)。
/// 筆數依 provider 取剛好跨過那個上限的量,SQLite 那條在 EF 10 線上才量得到差別。
/// </summary>
[Collection(PerformanceGuardCollection.Name)]   // 筆數大,不跟其他測試搶 CPU
public class LargeBatchTests
{
    private static int EntityCount
        => (Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() ?? "sqlite") == "sqlite"
            ? 33_000
            : 3_000;

    [Fact]
    public async Task 以名稱查詢_子樹裡的標籤數超過資料庫的參數上限_照常完成()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("root");
        await db.Store.CreateAsync("other");
        var leaves = Enumerable.Range(0, EntityCount)
            .Select(i => new Label
            {
                Id = Guid.NewGuid(), Name = $"leaf{i}", ParentId = root.Id,
                CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid(),
            })
            .ToList();
        db.Context.AddRange(leaves);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        var deep = await db.AddNoteAsync("deep");
        var top = await db.AddNoteAsync("top");
        await db.Store.AttachAsync(deep, leaves[^1].Name, "other");
        await db.Store.AttachAsync(top, "root");

        (await db.Store.FindByLabelAsync("root")).Should().HaveCount(2);
        (await (await db.Store.QueryByLabelAsync<TestNote>("root")).CountAsync()).Should().Be(2);
        foreach (var names in new[] { new[] { "root", "other" }, new[] { "other", "root" } })   // 大的那組在基底或在子查詢
        {
            (await db.Store.FindByLabelsAsync(names, LabelMatch.All)).Should().ContainSingle()
                .Which.EntityId.Should().Be(deep.Id);
            (await (await db.Store.QueryByLabelsAsync<TestNote>(names, LabelMatch.All)).Select(n => n.Id).ToListAsync())
                .Should().Equal(deep.Id);
        }
    }

    [Fact]
    public async Task AttachManyAsync與GetLabelsOfManyAsync_實體數超過資料庫的參數上限_照常完成()
    {
        using var db = new TestDb();
        var notes = Enumerable.Range(0, EntityCount).Select(i => new TestNote { Id = Guid.NewGuid(), Title = $"n{i}" }).ToList();
        db.Context.AddRange(notes);
        await db.Context.SaveChangesAsync();

        await db.Store.AttachManyAsync(notes, new[] { "急件" });
        await db.Store.AttachManyAsync(notes, new[] { "急件", "論文" });   // 第二次:既有連結要整批查得出來,只補「論文」
        var map = await db.Store.GetLabelsOfManyAsync(notes);

        map.Should().HaveCount(EntityCount);
        map.Values.Should().OnlyContain(labels => labels.Count == 2);
        using var other = db.CreateSecondContext();
        (await other.Set<Cornhsu.Labeling.EntityFrameworkCore.LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync())
            .Should().Be(EntityCount * 2);
    }
}
