using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 一次給幾萬個實體的批次 API。EF Core 10 把 <c>Contains</c> 展開成一個值一個參數,
/// 超過資料庫的參數上限(SQLite 32,766、SQL Server 2,100)就拋例外;store 要分批查。
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
