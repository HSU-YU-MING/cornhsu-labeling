using System.Reflection;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>QueryWithoutLabelsAsync:撈出「沒貼這些標籤」的實體,回傳可以繼續組合的 IQueryable。</summary>
public class QueryWithoutLabelsTests
{
    private static async Task<string[]> TitlesAsync(IQueryable<TestNote> query)
        => (await query.Select(n => n.Title).ToListAsync()).OrderBy(t => t, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task 排除貼了標籤的實體_沒貼任何標籤的也算在內()
    {
        using var db = new TestDb();
        var tagged = await db.AddNoteAsync("a貼了");
        var other = await db.AddNoteAsync("b貼別的");
        await db.AddNoteAsync("c沒貼");
        await db.Store.AttachAsync(tagged, "排除");
        await db.Store.AttachAsync(other, "別的");

        var query = await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "排除" });

        (await TitlesAsync(query)).Should().Equal("b貼別的", "c沒貼");
    }

    [Fact]
    public async Task 多個名稱_貼了任何一個都排除()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");
        await db.AddNoteAsync("c");
        await db.Store.AttachAsync(a, "一");
        await db.Store.AttachAsync(b, "二");

        var query = await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "一", "二" });

        (await TitlesAsync(query)).Should().Equal("c");
    }

    [Fact]
    public async Task 預設含子孫標籤_可以關掉()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("父");
        await db.Store.CreateAsync("子", parentId: parent.Id);
        var child = await db.AddNoteAsync("貼子");
        await db.AddNoteAsync("沒貼");
        await db.Store.AttachAsync(child, "子");

        (await TitlesAsync(await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "父" })))
            .Should().Equal(new[] { "沒貼" }, "排除父標籤時,貼子標籤的也算");
        (await TitlesAsync(await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "父" }, includeDescendants: false)))
            .Should().BeEquivalentTo(new[] { "沒貼", "貼子" });
    }

    [Fact]
    public async Task 不存在的名稱被忽略_其他名稱照常排除()
    {
        using var db = new TestDb();
        var tagged = await db.AddNoteAsync("貼了");
        await db.AddNoteAsync("沒貼");
        await db.Store.AttachAsync(tagged, "排除");

        var query = await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "不存在", "排除" });

        (await TitlesAsync(query)).Should().Equal("沒貼");
    }

    [Theory]
    [InlineData]
    [InlineData("不存在")]
    [InlineData("  ")]
    public async Task 沒有東西要排除_回傳全部實體(params string[] names)
    {
        using var db = new TestDb();
        var tagged = await db.AddNoteAsync("貼了");
        await db.AddNoteAsync("沒貼");
        await db.Store.AttachAsync(tagged, "標籤");

        var query = await db.Store.QueryWithoutLabelsAsync<TestNote>(names);

        (await query.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task 回傳的查詢可以繼續組合()
    {
        using var db = new TestDb();
        var tagged = await db.AddNoteAsync("b貼了");
        await db.AddNoteAsync("a留下");
        await db.AddNoteAsync("c留下");
        await db.AddNoteAsync("d別的");
        await db.Store.AttachAsync(tagged, "排除");

        var query = await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "排除" });
        var titles = await query
            .Where(n => n.Title.EndsWith("留下"))
            .OrderByDescending(n => n.Title)
            .Skip(1)
            .Select(n => n.Title)
            .ToListAsync();

        titles.Should().Equal("a留下");
    }

    [Fact]
    public async Task 只看這個型別自己的連結()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync("note");
        var todo = await db.AddTodoAsync("todo");
        await db.Store.AttachAsync(todo, "排除");

        (await TitlesAsync(await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "排除" }))).Should().Equal(note.Title);
        (await (await db.Store.QueryWithoutLabelsAsync<TestTodo>(new[] { "排除" })).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task 主鍵不叫Id的實體也能查()
    {
        using var db = new TestDb(r => r.Labelable<TestSku>(s => s.Name));
        var tagged = new TestSku { Code = "A-1", Name = "貼了" };
        var kept = new TestSku { Code = "A-2", Name = "留下" };
        db.Context.Set<TestSku>().AddRange(tagged, kept);
        await db.Context.SaveChangesAsync();
        await db.Store.AttachAsync(tagged, "排除");

        var query = await db.Store.QueryWithoutLabelsAsync<TestSku>(new[] { "排除" });

        (await query.ToListAsync()).Should().ContainSingle().Which.Code.Should().Be("A-2");
    }

    [Fact]
    public async Task 子樹超過一千個標籤_EF各版都翻譯得出來()
    {
        // 標籤 Id 集合要整個放進一條 SQL;EF 10 預設一個值一個參數,要走 EF.Parameter(見 LabelIdIn)
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("大分類");
        var last = Guid.Empty;
        for (var i = 0; i < 1_200; i++)
        {
            last = Guid.NewGuid();
            db.Context.Set<Label>().Add(new Label
            {
                Id = last,
                Name = $"子-{i}",
                ParentId = parent.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                ConcurrencyStamp = Guid.NewGuid(),
            });
        }
        await db.Context.SaveChangesAsync();
        var tagged = await db.AddNoteAsync("貼了");
        await db.AddNoteAsync("沒貼");
        await db.Store.AttachAsync(tagged, "子-1199");

        var query = await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "大分類" });

        (await TitlesAsync(query)).Should().Equal("沒貼");
    }

    [Fact]
    public async Task 參數是null_或型別沒註冊_拋清楚的例外()
    {
        using var db = new TestDb();

        var nullNames = () => db.Store.QueryWithoutLabelsAsync<TestNote>(null!);
        (await nullNames.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("labelNames");

        var orphan = () => db.Store.QueryWithoutLabelsAsync<TestOrphan>(new[] { "x" });
        await orphan.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 其他ILabelStore實作_拋NotSupported()
    {
        var other = DispatchProxy.Create<ILabelStore, ReplaceLabelsTests.UnimplementedStore>();

        var act = () => other.QueryWithoutLabelsAsync<TestNote>(new[] { "x" });

        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage($"*{nameof(ILabelExclusionQuery)}*");
    }

    [Fact]
    public void LabelMatch列舉沒有變_別人的switch不受影響()
    {
        Enum.GetNames<LabelMatch>().Should().Equal("Any", "All");
    }
}
