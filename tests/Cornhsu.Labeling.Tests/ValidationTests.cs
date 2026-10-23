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
    public async Task null參數_ArgumentNullException的ParamName指出是哪一個參數()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        async Task ParamNameShouldBe(Func<Task> act, string expected)
            => (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be(expected);

        await ParamNameShouldBe(() => db.Store.AttachAsync<TestNote>(null!, "論文"), "entity");
        await ParamNameShouldBe(() => db.Store.DetachAsync<TestNote>(null!, "論文"), "entity");
        await ParamNameShouldBe(() => db.Store.GetLabelsOfAsync<TestNote>(null!), "entity");
        await ParamNameShouldBe(() => db.Store.AttachAsync(note, (IEnumerable<string>)null!), "labelNames");
        await ParamNameShouldBe(() => db.Store.DetachAsync(note, (IEnumerable<string>)null!), "labelNames");
        await ParamNameShouldBe(() => db.Store.AttachManyAsync(new[] { note }, null!), "labelNames");
        await ParamNameShouldBe(() => db.Store.FindByLabelsAsync(null!), "labelNames");
        await ParamNameShouldBe(() => db.Store.QueryByLabelsAsync<TestNote>(null!), "labelNames");
        await ParamNameShouldBe(() => db.Store.AttachManyAsync<TestNote>(null!, new[] { "論文" }), "entities");
        await ParamNameShouldBe(() => db.Store.GetLabelsOfManyAsync<TestNote>(null!), "entities");
    }

    [Fact]
    public async Task 實體序列含null_ArgumentException的ParamName為entities()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        var attachMany = () => db.Store.AttachManyAsync(new[] { note, null! }, new[] { "論文" });
        (await attachMany.Should().ThrowAsync<ArgumentException>().WithMessage("the entity sequence contains a null.*"))
            .Which.ParamName.Should().Be("entities");

        var getMany = () => db.Store.GetLabelsOfManyAsync(new[] { note, null! });
        (await getMany.Should().ThrowAsync<ArgumentException>().WithMessage("the entity sequence contains a null.*"))
            .Which.ParamName.Should().Be("entities");

        (await db.Store.GetAllAsync()).Should().BeEmpty("檢查要在任何寫入之前");
    }

    [Fact]
    public async Task CreateAsync_parentId不存在_拋出清楚的例外()
    {
        using var db = new TestDb();

        var missing = Guid.NewGuid();

        var act = () => db.Store.CreateAsync("孤兒", parentId: missing);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"parent label {missing} does not exist.");
        (await db.Store.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_名稱超過上限_拋出ArgumentException()
    {
        using var db = new TestDb();
        var tooLong = new string('標', Label.MaxNameLength + 1);

        var act = () => db.Store.CreateAsync(tooLong);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage(
            $"label name is {Label.MaxNameLength + 1} characters, over the {Label.MaxNameLength} limit: '{new string('標', 16)}…'");
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
