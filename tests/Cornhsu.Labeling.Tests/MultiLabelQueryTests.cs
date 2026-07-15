using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>FindByLabelsAsync / QueryByLabelsAsync:多標籤 AND(All)/ OR(Any)查詢。</summary>
public class MultiLabelQueryTests
{
    [Fact]
    public async Task Any_命中任一標籤都撈到_同實體不重複()
    {
        using var db = new TestDb();
        var both = await db.AddNoteAsync("兩個都有");
        var onlyA = await db.AddNoteAsync("只有論文");
        var none = await db.AddNoteAsync("都沒有");
        await db.Store.AttachAsync(both, "論文", "急件");
        await db.Store.AttachAsync(onlyA, "論文");

        var hits = await db.Store.FindByLabelsAsync(new[] { "論文", "急件" }, LabelMatch.Any);

        hits.Should().HaveCount(2, "both 命中兩個標籤也只能出現一次");
        hits.Select(h => h.DisplayName).Should().BeEquivalentTo(new[] { "兩個都有", "只有論文" });
    }

    [Fact]
    public async Task All_只撈同時擁有全部標籤的實體()
    {
        using var db = new TestDb();
        var both = await db.AddNoteAsync("兩個都有");
        var onlyA = await db.AddNoteAsync("只有論文");
        await db.Store.AttachAsync(both, "論文", "急件");
        await db.Store.AttachAsync(onlyA, "論文");

        var hits = await db.Store.FindByLabelsAsync(new[] { "論文", "急件" }, LabelMatch.All);

        hits.Should().ContainSingle().Which.DisplayName.Should().Be("兩個都有");
    }

    [Fact]
    public async Task All_跨型別各自判定()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync("筆記");
        var todo = await db.AddTodoAsync("待辦");
        await db.Store.AttachAsync(note, "論文", "急件");
        await db.Store.AttachAsync(todo, "論文", "急件");

        var hits = await db.Store.FindByLabelsAsync(new[] { "論文", "急件" }, LabelMatch.All);

        hits.Select(h => h.EntityTypeKey).Should().BeEquivalentTo(new[] { "TestNote", "TestTodo" });
    }

    [Fact]
    public async Task All_任一名稱不存在_結果必為空()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        var hits = await db.Store.FindByLabelsAsync(new[] { "論文", "不存在的標籤" }, LabelMatch.All);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Any_不存在的名稱被忽略_不影響其他標籤()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        var hits = await db.Store.FindByLabelsAsync(new[] { "論文", "不存在的標籤" }, LabelMatch.Any);

        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task 空名稱集合_回傳空結果()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        (await db.Store.FindByLabelsAsync(Array.Empty<string>(), LabelMatch.Any)).Should().BeEmpty();
        (await db.Store.FindByLabelsAsync(new[] { "  ", "" }, LabelMatch.All)).Should().BeEmpty();
    }

    [Fact]
    public async Task All_包含子孫時_子標籤命中算該群組命中()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);
        await db.Store.CreateAsync("急件");

        var note = await db.AddNoteAsync("子標籤+急件");
        await db.Store.AttachAsync(note, "文獻回顧", "急件");

        var withDescendants = await db.Store.FindByLabelsAsync(
            new[] { "論文", "急件" }, LabelMatch.All, includeDescendants: true);
        var withoutDescendants = await db.Store.FindByLabelsAsync(
            new[] { "論文", "急件" }, LabelMatch.All, includeDescendants: false);

        withDescendants.Should().ContainSingle("「文獻回顧」是「論文」的子標籤,應算命中論文群組");
        withoutDescendants.Should().BeEmpty("不含子孫時,貼「文獻回顧」不等於貼「論文」");
    }

    [Fact]
    public async Task QueryByLabelsAsync_All_強型別且可續接過濾()
    {
        using var db = new TestDb();
        var match = await db.AddNoteAsync("目標");
        var other = await db.AddNoteAsync("另一個目標");
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(match, "論文", "急件");
        await db.Store.AttachAsync(other, "論文", "急件");
        await db.Store.AttachAsync(todo, "論文", "急件");

        var query = await db.Store.QueryByLabelsAsync<TestNote>(new[] { "論文", "急件" }, LabelMatch.All);
        var notes = await query.Where(n => n.Title == "目標").ToListAsync();

        notes.Should().ContainSingle().Which.Id.Should().Be(match.Id);
    }

    [Fact]
    public async Task 名稱正規化套用於多標籤查詢_前後空白與重複名稱不影響()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文", "急件");

        var hits = await db.Store.FindByLabelsAsync(new[] { " 論文 ", "論文", "急件" }, LabelMatch.All);

        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task null名稱集合_拋出ArgumentNullException()
    {
        using var db = new TestDb();

        var actFind = () => db.Store.FindByLabelsAsync(null!);
        await actFind.Should().ThrowAsync<ArgumentNullException>();

        var actQuery = () => db.Store.QueryByLabelsAsync<TestNote>(null!);
        await actQuery.Should().ThrowAsync<ArgumentNullException>();
    }
}
