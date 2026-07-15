using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>GetLabelsOfManyAsync:批次讀取多個實體的標籤(解清單畫面 N+1)。</summary>
public class BatchReadTests
{
    [Fact]
    public async Task 批次讀_每個實體都有項目_沒有標籤的實體是空清單()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");
        var c = await db.AddNoteAsync("c");
        await db.Store.AttachAsync(a, "論文", "急件");
        await db.Store.AttachAsync(b, "論文");

        var map = await db.Store.GetLabelsOfManyAsync(new[] { a, b, c });

        map.Should().HaveCount(3);
        map[a].Select(l => l.Name).Should().BeEquivalentTo(new[] { "論文", "急件" });
        map[b].Select(l => l.Name).Should().BeEquivalentTo(new[] { "論文" });
        map[c].Should().BeEmpty();
    }

    [Fact]
    public async Task 批次讀_結果與逐筆GetLabelsOfAsync一致_含排序()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "乙標籤", "甲標籤", "丙標籤");

        var single = await db.Store.GetLabelsOfAsync(note);
        var batch = (await db.Store.GetLabelsOfManyAsync(new[] { note }))[note];

        batch.Select(l => l.Id).Should().Equal(single.Select(l => l.Id));
    }

    [Fact]
    public async Task 批次讀_int主鍵型別也可用()
    {
        using var db = new TestDb();
        var t1 = await db.AddTodoAsync("t1");
        var t2 = await db.AddTodoAsync("t2");
        await db.Store.AttachAsync(t1, "急件");

        var map = await db.Store.GetLabelsOfManyAsync(new[] { t1, t2 });

        map[t1].Should().ContainSingle().Which.Name.Should().Be("急件");
        map[t2].Should().BeEmpty();
    }

    [Fact]
    public async Task 批次讀_空集合回傳空字典()
    {
        using var db = new TestDb();
        var map = await db.Store.GetLabelsOfManyAsync(Array.Empty<TestNote>());
        map.Should().BeEmpty();
    }

    [Fact]
    public async Task 批次讀_字典以實例為鍵_同Id的不同實例各自有項目()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var copy = new TestNote { Id = note.Id, Title = note.Title };   // 同 Id、不同實例

        var map = await db.Store.GetLabelsOfManyAsync(new[] { note, copy });

        map.Should().HaveCount(2);
        map[note].Select(l => l.Name).Should().BeEquivalentTo(new[] { "論文" });
        map[copy].Select(l => l.Name).Should().BeEquivalentTo(new[] { "論文" });
    }

    [Fact]
    public async Task 批次讀_null集合或含null實體_拋出例外()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        var actNull = () => db.Store.GetLabelsOfManyAsync<TestNote>(null!);
        await actNull.Should().ThrowAsync<ArgumentNullException>();

        var actNullItem = () => db.Store.GetLabelsOfManyAsync(new[] { note, null! });
        await actNullItem.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task 批次讀_未註冊型別_拋出清楚的例外()
    {
        using var db = new TestDb();
        var act = () => db.Store.GetLabelsOfManyAsync(new[] { new TestOrphan { Id = Guid.NewGuid() } });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*未註冊*");
    }
}
