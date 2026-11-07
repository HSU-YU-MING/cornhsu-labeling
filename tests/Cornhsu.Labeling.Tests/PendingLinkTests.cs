using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 貼標時,追蹤器裡可能有消費端還沒存的連結。AttachAsync 呼叫完成就該貼著:
/// 連結正等著刪除時要取消刪除,不然存檔後標籤反而不見,而且沒有任何錯誤訊息。
/// </summary>
public class PendingLinkTests
{
    private static async Task<LabelLink<TestNote, Guid>> MarkLinkDeletedAsync(TestDb db)
    {
        var links = db.Context.Set<LabelLink<TestNote, Guid>>();
        var link = await links.SingleAsync();
        links.Remove(link);
        return link;
    }

    private static async Task<int> StoredLinkCountAsync(TestDb db)
    {
        using var other = db.CreateSecondContext();
        return await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync();
    }

    [Fact]
    public async Task AttachAsync_連結正等著刪除_取消刪除()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        await MarkLinkDeletedAsync(db);

        await db.Store.AttachAsync(note, "論文");

        (await StoredLinkCountAsync(db)).Should().Be(1, "呼叫完成就該貼著");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("論文");
    }

    [Fact]
    public async Task AttachManyAsync_連結正等著刪除_取消刪除()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        await MarkLinkDeletedAsync(db);

        await db.Store.AttachManyAsync(new[] { note }, new[] { "論文" });

        (await StoredLinkCountAsync(db)).Should().Be(1, "呼叫完成就該貼著");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("論文");
    }

    [Fact]
    public async Task AttachManyAsync_消費端還沒存的新連結_不重複新增()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var label = await db.Store.CreateAsync("論文");
        db.Context.Set<LabelLink<TestNote, Guid>>().Add(new LabelLink<TestNote, Guid>
        {
            LabelId = label.Id,
            EntityId = note.Id,
            AttachedAt = DateTimeOffset.UtcNow,
        });

        await db.Store.AttachManyAsync(new[] { note }, new[] { "論文" });

        (await StoredLinkCountAsync(db)).Should().Be(1);
    }

    [Fact]
    public async Task 取消刪除的連結_保留原本的AttachedAt()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var original = (await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().SingleAsync()).AttachedAt;
        await MarkLinkDeletedAsync(db);

        await db.Store.AttachAsync(note, "論文");

        using var other = db.CreateSecondContext();
        (await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().SingleAsync()).AttachedAt.Should().Be(original);
    }

    [Fact]
    public async Task AttachAsync_存檔失敗_連結回到等著刪除的狀態()
    {
        var failure = new FailOnNextSave();
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var link = await MarkLinkDeletedAsync(db);

        failure.Armed = true;
        var act = () => db.Store.AttachAsync(note, "論文");
        await act.Should().ThrowAsync<DbUpdateException>();

        db.Context.Entry(link).State.Should().Be(EntityState.Deleted, "失敗時要還原成消費端原本的狀態");
    }

    [Fact]
    public async Task AttachManyAsync_存檔失敗_連結回到等著刪除的狀態()
    {
        var failure = new FailOnNextSave();
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var link = await MarkLinkDeletedAsync(db);

        failure.Armed = true;
        var act = () => db.Store.AttachManyAsync(new[] { note }, new[] { "論文" });
        await act.Should().ThrowAsync<DbUpdateException>();

        db.Context.Entry(link).State.Should().Be(EntityState.Deleted, "失敗時要還原成消費端原本的狀態");
    }

    [Fact]
    public async Task AttachManyAsync_別的實體或別的標籤正等著刪除_不受影響()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync("這筆");
        var other = await db.AddNoteAsync("別筆");
        await db.Store.AttachAsync(other, "論文");
        await db.Store.AttachAsync(note, "急件");
        var links = db.Context.Set<LabelLink<TestNote, Guid>>();
        links.Remove(await links.SingleAsync(l => l.EntityId == other.Id));
        links.Remove(await links.SingleAsync(l => l.EntityId == note.Id));

        await db.Store.AttachManyAsync(new[] { note }, new[] { "論文" });

        (await db.Store.GetLabelsOfAsync(other)).Should().BeEmpty("別的實體的刪除照常生效");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("論文", "沒被要求貼上的標籤,刪除照常生效");
    }

    private sealed class FailOnNextSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new DbUpdateException("模擬的寫入失敗");
            }
            return ValueTask.FromResult(result);
        }
    }
}
