using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// SQL Server 的相容性層級低於 130 時沒有 OPENJSON:EF 9/10 的 <c>EF.Parameter(集合)</c> 在那裡翻譯不出來。
/// 名稱查詢遇到子樹超過 1,000 個 Id 時要退回 EF 的預設做法,跟 1.1.0 一樣照常完成(第五輪審查第 1 條)。
/// 只在 <c>CORNHSU_TEST_PROVIDER=sqlserver</c> 時跑;其他 provider 沒有相容性層級這回事。
/// </summary>
[Collection(PerformanceGuardCollection.Name)]   // 一次建一千多個標籤,不跟其他測試搶
public class CompatibilityLevelTests
{
    private static bool OnSqlServer
        => Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() == "sqlserver";

    [Fact]
    public async Task 相容性層級120上_子樹超過1000個Id的名稱查詢_照常完成()
    {
        if (!OnSqlServer) return;

        var server = Environment.GetEnvironmentVariable("CORNHSU_TEST_SQLSERVER")
            ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>(n => n.Title);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer($"{server};Database=cornhsu_test_{Guid.NewGuid():N}", o => o.UseCompatibilityLevel(120))
            .EnableServiceProviderCaching(false)
            .Options;
        using var context = new TestDbContext(options, registry);
        await context.Database.EnsureCreatedAsync();
        try
        {
            var store = LabelStoreFactory.Create(context, registry);
            var root = await store.CreateAsync("root");
            await store.CreateAsync("other");
            var leaves = Enumerable.Range(0, 1_200)
                .Select(i => new Label
                {
                    Id = Guid.NewGuid(), Name = $"leaf{i:D4}", ParentId = root.Id,
                    CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid(),
                })
                .ToList();
            context.AddRange(leaves);
            await context.SaveChangesAsync();

            var notes = Enumerable.Range(0, 30).Select(i => new TestNote { Id = Guid.NewGuid(), Title = $"n{i:D2}" }).ToList();
            context.AddRange(notes);
            await context.SaveChangesAsync();
            for (var i = 0; i < notes.Count; i++)
                await store.AttachAsync(notes[i], leaves[i * 40].Name);
            await store.AttachAsync(notes[0], "other");

            var query = await store.QueryByLabelAsync<TestNote>("root");   // root + 1,200 個子標籤 = 1,201 個 Id
            (await query.CountAsync()).Should().Be(30);
            (await query.Where(n => n.Title != "n00").OrderBy(n => n.Title).Skip(1).Take(2).Select(n => n.Title).ToListAsync())
                .Should().Equal("n02", "n03");
            (await query.AnyAsync(n => n.Title == "n29")).Should().BeTrue();
            (await store.FindByLabelAsync("root")).Should().HaveCount(30);
            (await store.FindByLabelsAsync(new[] { "root", "other" }, LabelMatch.All)).Should().ContainSingle();
            (await (await store.QueryByLabelsAsync<TestNote>(new[] { "other", "root" }, LabelMatch.All)).CountAsync())
                .Should().Be(1);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }
}
