using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>參數驗證:null 實體、parentId 存在性、名稱長度上限。</summary>
public class ValidationTests
{
    [Fact]
    public async Task Attach與Detach與GetLabelsOf_null實體_拋出ArgumentNullException()
    {
        using var db = new TestDb();

        var attach = () => db.Store.AttachAsync<TestNote>(null!, "論文");
        await attach.Should().ThrowAsync<ArgumentNullException>();

        var detach = () => db.Store.DetachAsync<TestNote>(null!, "論文");
        await detach.Should().ThrowAsync<ArgumentNullException>();

        var get = () => db.Store.GetLabelsOfAsync<TestNote>(null!);
        await get.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Attach_null名稱集合_拋出ArgumentNullException()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        var act = () => db.Store.AttachAsync(note, (IEnumerable<string>)null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreateAsync_parentId不存在_拋出清楚的例外()
    {
        using var db = new TestDb();

        var act = () => db.Store.CreateAsync("孤兒", parentId: Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*父標籤*不存在*");
    }

    [Fact]
    public async Task CreateAsync_名稱超過上限_拋出ArgumentException()
    {
        using var db = new TestDb();
        var tooLong = new string('標', Label.MaxNameLength + 1);

        var act = () => db.Store.CreateAsync(tooLong);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage($"*{Label.MaxNameLength}*");
    }

    [Fact]
    public async Task CreateAsync_名稱剛好在上限_成功()
    {
        using var db = new TestDb();
        var exact = new string('標', Label.MaxNameLength);

        var label = await db.Store.CreateAsync(exact);
        label.Name.Should().HaveLength(Label.MaxNameLength);
    }

    [Fact]
    public async Task RenameAsync_名稱超過上限_拋出ArgumentException()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("原名");

        var act = () => db.Store.RenameAsync(label.Id, new string('長', Label.MaxNameLength + 1));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Attach自動建立標籤時_名稱超過上限_拋出ArgumentException()
    {
        using var db = new TestDb();   // AutoCreateLabels 預設 true
        var note = await db.AddNoteAsync();

        var act = () => db.Store.AttachAsync(note, new string('長', Label.MaxNameLength + 1));
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
