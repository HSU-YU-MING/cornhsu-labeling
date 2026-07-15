using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>§9.2 #5–#10、#14:多型查詢、冪等、階層、改名、統計。</summary>
public class QueryTests
{
    [Fact] // #5
    public async Task 同一標籤貼在Note和Todo上_跨型別查詢兩個都撈到()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync("讀論文");
        var todo = await db.AddTodoAsync("整理文獻");
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(todo, "論文");

        var hits = await db.Store.FindByLabelAsync("論文");

        hits.Should().HaveCount(2);
        hits.Select(h => h.EntityTypeKey).Should().BeEquivalentTo(new[] { "TestNote", "TestTodo" });
        hits.Single(h => h.EntityTypeKey == "TestNote").DisplayName.Should().Be("讀論文");
        hits.Single(h => h.EntityTypeKey == "TestTodo").DisplayName.Should().Be("整理文獻");

        // 混合主鍵:同一次查詢裡 Guid 命中與 int 命中並存,EntityIdAs 取回強型別
        hits.Single(h => h.EntityTypeKey == "TestNote").EntityIdAs<Guid>().Should().Be(note.Id);
        hits.Single(h => h.EntityTypeKey == "TestTodo").EntityIdAs<int>().Should().Be(todo.Id);
    }

    [Fact]
    public async Task EntityIdAs指定錯誤型別_拋出InvalidCastException()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        var hit = (await db.Store.FindByLabelAsync("論文")).Single();

        var act = () => hit.EntityIdAs<int>();
        act.Should().Throw<InvalidCastException>().WithMessage("*Guid*Int32*");
    }

    [Fact] // #6
    public async Task 強型別查詢只回指定型別_不會混進其他型別()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(todo, "論文");

        var query = await db.Store.QueryByLabelAsync<TestNote>("論文");
        var notes = await query.ToListAsync();

        notes.Should().ContainSingle().Which.Id.Should().Be(note.Id);
    }

    [Fact] // #7
    public async Task 重複貼同一個標籤_不產生重複列()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(note, "論文", "論文");

        var count = await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync();
        count.Should().Be(1, "AttachAsync 必須是冪等的");
    }

    [Fact] // #8
    public async Task 包含子孫標籤時_子標籤命中的實體也要撈到()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);

        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "文獻回顧");   // 只貼子標籤

        var hits = await db.Store.FindByLabelAsync("論文", includeDescendants: true);
        hits.Should().ContainSingle().Which.EntityId.Should().Be(note.Id);
    }

    [Fact] // #9
    public async Task 不包含子孫標籤時_不能撈到子標籤的實體()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);

        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "文獻回顧");

        var hits = await db.Store.FindByLabelAsync("論文", includeDescendants: false);
        hits.Should().BeEmpty();
    }

    [Fact] // #10
    public async Task 重新命名標籤後_所有連結仍有效()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        var label = await db.Store.FindAsync("論文");
        await db.Store.RenameAsync(label!.Id, "研究");

        var labels = await db.Store.GetLabelsOfAsync(note);
        labels.Should().ContainSingle().Which.Name.Should().Be("研究");

        var hits = await db.Store.FindByLabelAsync("研究");
        hits.Should().ContainSingle().Which.EntityId.Should().Be(note.Id);
    }

    [Fact] // #14
    public async Task 使用次數統計_跨型別加總正確()
    {
        using var db = new TestDb();
        var note1 = await db.AddNoteAsync("n1");
        var note2 = await db.AddNoteAsync("n2");
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(note1, "論文", "急件");
        await db.Store.AttachAsync(note2, "論文");
        await db.Store.AttachAsync(todo, "論文");

        var counts = await db.Store.GetUsageCountsAsync();

        var paper = await db.Store.FindAsync("論文");
        var urgent = await db.Store.FindAsync("急件");
        counts[paper!.Id].Should().Be(3, "論文貼在 2 個 Note + 1 個 Todo 上");
        counts[urgent!.Id].Should().Be(1);
    }

    [Fact] // 撕標(DetachAsync 的基本行為,DoD 需要)
    public async Task 撕標後_連結消失但標籤本體仍在()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文", "急件");

        await db.Store.DetachAsync(note, "論文");

        var labels = await db.Store.GetLabelsOfAsync(note);
        labels.Should().ContainSingle().Which.Name.Should().Be("急件");
        (await db.Store.FindAsync("論文")).Should().NotBeNull();
    }
}
