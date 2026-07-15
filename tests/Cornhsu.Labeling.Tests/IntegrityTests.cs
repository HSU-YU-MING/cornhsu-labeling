using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>§9.2 #1–#4:外鍵完整性——本套件的賣點。</summary>
public class IntegrityTests
{
    [Fact] // #1
    public async Task 刪除實體後_連結列由資料庫cascade自動消失()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        // 用 ExecuteDelete 繞過 EF change tracker,驗證的是資料庫層的 FK cascade
        await db.Context.Notes.Where(n => n.Id == note.Id).ExecuteDeleteAsync();

        var remaining = await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync();
        remaining.Should().Be(0, "刪掉 Note 之後 LabelLink_TestNote 應該是空的");

        // 標籤本體不受影響
        (await db.Store.FindAsync("論文")).Should().NotBeNull();
    }

    [Fact] // #2
    public async Task 刪除標籤後_所有型別的連結列全部消失()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(todo, "論文");

        var label = await db.Store.FindAsync("論文");
        await db.Store.DeleteAsync(label!.Id);

        (await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync()).Should().Be(0);
        (await db.Context.Set<LabelLink<TestTodo, int>>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact] // #3(Guid 主鍵)
    public async Task 插入不存在的EntityId_應噴FK違反_Guid主鍵()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        db.Context.Set<LabelLink<TestNote, Guid>>().Add(new LabelLink<TestNote, Guid>
        {
            LabelId = label.Id,
            EntityId = Guid.NewGuid(),   // 不存在的 Note
            AttachedAt = DateTimeOffset.UtcNow,
        });

        var act = () => db.Context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("EntityId 有真外鍵,資料庫必須擋下孤兒連結");
    }

    [Fact] // #3(int 主鍵)
    public async Task 插入不存在的EntityId_應噴FK違反_int主鍵()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        db.Context.Set<LabelLink<TestTodo, int>>().Add(new LabelLink<TestTodo, int>
        {
            LabelId = label.Id,
            EntityId = 999_999,   // 不存在的 Todo
            AttachedAt = DateTimeOffset.UtcNow,
        });

        var act = () => db.Context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("int 主鍵的連結表同樣要有真外鍵把關");
    }

    [Fact] // #4
    public async Task 有子標籤時刪除父標籤_應被擋下()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);

        var act = () => db.Store.DeleteAsync(parent.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*子標籤*");

        (await db.Store.FindAsync("論文")).Should().NotBeNull("父標籤不應被刪掉");
    }
}
