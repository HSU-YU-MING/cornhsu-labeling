using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>LabelStoreFactory:沒有 DI 容器的應用程式(WPF singleton 服務等)的正門。</summary>
public class FactoryTests
{
    [Fact]
    public async Task 不經DI容器_以Factory建立store_完整流程可用()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        // 手動建 registry(模擬無 DI 的 WPF app:registry 是 app 全域靜態單例)
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>(n => n.Title);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(conn)
            .EnableServiceProviderCaching(false)
            .Options;
        using var context = new TestDbContext(options, registry);
        context.Database.EnsureCreated();

        var store = LabelStoreFactory.Create(context, registry);

        var note = new TestNote { Id = Guid.NewGuid(), Title = "無DI筆記" };
        context.Notes.Add(note);
        await context.SaveChangesAsync();

        await store.AttachAsync(note, "論文");
        var hits = await store.FindByLabelAsync("論文");

        hits.Should().ContainSingle().Which.DisplayName.Should().Be("無DI筆記");
    }

    [Fact]
    public void Factory_null參數_拋出ArgumentNullException()
    {
        var registry = new LabelRegistry();

        var actContext = () => LabelStoreFactory.Create<TestDbContext>(null!, registry);
        actContext.Should().Throw<ArgumentNullException>();

        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).Options;
        registry.Labelable<TestNote>();
        using var context = new TestDbContext(options, registry);

        var actRegistry = () => LabelStoreFactory.Create(context, null!);
        actRegistry.Should().Throw<ArgumentNullException>();
    }
}
