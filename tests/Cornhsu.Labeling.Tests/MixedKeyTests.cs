using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>int 主鍵路徑的完整操作測試(Guid 路徑由其他測試涵蓋)。</summary>
public class MixedKeyTests
{
    [Fact]
    public async Task int主鍵實體_撕標正常運作()
    {
        using var db = new TestDb();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(todo, "論文", "急件");

        await db.Store.DetachAsync(todo, "論文");

        (await db.Store.GetLabelsOfAsync(todo)).Should().ContainSingle().Which.Name.Should().Be("急件");
    }

    [Fact]
    public async Task int主鍵實體_反查標籤正常運作()
    {
        using var db = new TestDb();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(todo, "論文", "急件");

        var labels = await db.Store.GetLabelsOfAsync(todo);

        labels.Select(l => l.Name).Should().BeEquivalentTo(new[] { "論文", "急件" });
    }

    [Fact]
    public async Task int主鍵實體_強型別查詢正常運作()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var todo1 = await db.AddTodoAsync("t1");
        var todo2 = await db.AddTodoAsync("t2");
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(todo1, "論文");
        await db.Store.AttachAsync(todo2, "其他");

        var query = await db.Store.QueryByLabelAsync<TestTodo>("論文");
        var todos = await query.ToListAsync();

        todos.Should().ContainSingle().Which.Id.Should().Be(todo1.Id);
    }
}
