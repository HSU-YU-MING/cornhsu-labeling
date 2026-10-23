using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>CancellationToken:每個方法都要把 token 傳到 EF,已取消的 token 拋 OperationCanceledException,且不留下寫入。</summary>
public class CancellationTests
{
    private static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public async Task 讀取方法_已取消的token_拋OperationCanceledException()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var ct = Cancelled();

        var find = () => db.Store.FindAsync("論文", ct);
        await find.Should().ThrowAsync<OperationCanceledException>();

        var getAll = () => db.Store.GetAllAsync(ct);
        await getAll.Should().ThrowAsync<OperationCanceledException>();

        var labelsOf = () => db.Store.GetLabelsOfAsync(note, ct);
        await labelsOf.Should().ThrowAsync<OperationCanceledException>();

        var labelsOfMany = () => db.Store.GetLabelsOfManyAsync(new[] { note }, ct);
        await labelsOfMany.Should().ThrowAsync<OperationCanceledException>();

        var findBy = () => db.Store.FindByLabelAsync("論文", ct: ct);
        await findBy.Should().ThrowAsync<OperationCanceledException>();

        var findByMany = () => db.Store.FindByLabelsAsync(new[] { "論文" }, ct: ct);
        await findByMany.Should().ThrowAsync<OperationCanceledException>();

        var query = () => db.Store.QueryByLabelAsync<TestNote>("論文", ct: ct);
        await query.Should().ThrowAsync<OperationCanceledException>();

        var queryMany = () => db.Store.QueryByLabelsAsync<TestNote>(new[] { "論文" }, ct: ct);
        await queryMany.Should().ThrowAsync<OperationCanceledException>();

        var usage = () => db.Store.GetUsageCountsAsync(ct);
        await usage.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 寫入方法_已取消的token_拋OperationCanceledException且資料庫不變()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        var note = await db.AddNoteAsync();
        var other = await db.AddNoteAsync();
        await db.Store.AttachAsync(other, "論文");
        var ct = Cancelled();

        var create = () => db.Store.CreateAsync("新標籤", ct: ct);
        await create.Should().ThrowAsync<OperationCanceledException>();

        var rename = () => db.Store.RenameAsync(label.Id, "研究", ct);
        await rename.Should().ThrowAsync<OperationCanceledException>();

        var delete = () => db.Store.DeleteAsync(label.Id, ct);
        await delete.Should().ThrowAsync<OperationCanceledException>();

        var attach = () => db.Store.AttachAsync(note, new[] { "論文", "自動" }, ct);
        await attach.Should().ThrowAsync<OperationCanceledException>();

        var attachMany = () => db.Store.AttachManyAsync(new[] { note }, new[] { "論文" }, ct);
        await attachMany.Should().ThrowAsync<OperationCanceledException>();

        var detach = () => db.Store.DetachAsync(other, new[] { "論文" }, ct);
        await detach.Should().ThrowAsync<OperationCanceledException>();

        var update = () => db.Store.UpdateAsync(label.Id, l => l.Color = "#FFFFFF", ct);
        await update.Should().ThrowAsync<OperationCanceledException>();

        // 從另一個 context 看資料庫:什麼都沒寫進去
        using var fresh = db.CreateSecondContext();
        var labels = await fresh.Set<Label>().AsNoTracking().ToListAsync();
        labels.Should().ContainSingle().Which.Should().Match<Label>(l => l.Name == "論文" && l.Color == null);
        var links = await fresh.Set<LabelLink<TestNote, Guid>>().AsNoTracking().Select(l => l.EntityId).ToListAsync();
        links.Should().Equal(other.Id);
    }
}
