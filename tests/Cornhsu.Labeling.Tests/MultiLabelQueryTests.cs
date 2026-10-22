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
    public async Task 三個標籤_All要三個都有_Any有一個就算()
    {
        // 兩個名稱時 All 只有「第一組為基底 + 一個交集」;第三組起的每一組都要各自再收斂一次
        using var db = new TestDb();
        var all3 = await db.AddNoteAsync("三個都有");
        var two = await db.AddNoteAsync("缺第三個");
        var thirdOnly = await db.AddNoteAsync("只有第三個");
        await db.AddNoteAsync("都沒有");
        await db.Store.AttachAsync(all3, "論文", "急件", "待讀");
        await db.Store.AttachAsync(two, "論文", "急件");
        await db.Store.AttachAsync(thirdOnly, "待讀");
        var names = new[] { "論文", "急件", "待讀" };

        (await db.Store.FindByLabelsAsync(names, LabelMatch.All)).Should().ContainSingle()
            .Which.DisplayName.Should().Be("三個都有");
        (await (await db.Store.QueryByLabelsAsync<TestNote>(names, LabelMatch.All)).Select(n => n.Id).ToListAsync())
            .Should().Equal(all3.Id);

        // 以下是對照組:Any 的語意本身已由 Any_命中任一標籤都撈到_同實體不重複 涵蓋,
        // 這裡只確認同一份資料換成 Any 時,第三組照樣算數(「只有第三個」也要撈到)
        (await db.Store.FindByLabelsAsync(names, LabelMatch.Any)).Select(h => h.DisplayName)
            .Should().BeEquivalentTo(new[] { "三個都有", "缺第三個", "只有第三個" });
        (await (await db.Store.QueryByLabelsAsync<TestNote>(names, LabelMatch.Any)).Select(n => n.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { all3.Id, two.Id, thirdOnly.Id });
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
    public async Task 省略match與includeDescendants時_預設是Any且包含子孫()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);
        var viaChild = await db.AddNoteAsync("只貼子標籤");
        var urgentOnly = await db.AddNoteAsync("只貼急件");
        await db.Store.AttachAsync(viaChild, "文獻回顧");
        await db.Store.AttachAsync(urgentOnly, "急件");
        var names = new[] { "論文", "急件" };

        // 參數預設值會被編譯進呼叫端,屬公開介面:預設若變成 All,結果是空的;
        // 預設若變成不含子孫,只貼子標籤的那筆會不見
        (await db.Store.FindByLabelsAsync(names)).Select(h => h.DisplayName)
            .Should().BeEquivalentTo(new[] { "只貼子標籤", "只貼急件" });
        (await (await db.Store.QueryByLabelsAsync<TestNote>(names)).Select(n => n.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { viaChild.Id, urgentOnly.Id });
    }

    [Fact]
    public void LabelMatch的數值是對外契約()
    {
        // 列舉值會被編譯進消費端的組件:對調數值等於讓沒重新編譯的呼叫端 All/Any 互換
        ((int)LabelMatch.Any).Should().Be(0);
        ((int)LabelMatch.All).Should().Be(1);
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

    /// <summary>數送出的 SQL 指令。</summary>
    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 名稱很多時_名稱解析一次查回_往返次數不隨名稱數增加(bool includeDescendants)
    {
        // 名稱交給資料庫比對,但不是一個名稱一次往返:全部名稱以 UNION ALL 一次查回(每列帶名稱的序號),
        // 含子孫時再讀一次階層,之後每個註冊型別一次
        var counter = new CommandCounter();
        using var db = new TestDb(null, counter);
        var notes = new List<TestNote>();
        for (var i = 0; i < 50; i++)
            notes.Add(await db.AddNoteAsync($"n{i:D2}"));
        for (var i = 0; i < 50; i++)
            await db.Store.AttachAsync(notes[i], $"label{i:D2}");
        var names = Enumerable.Range(0, 50).Select(i => $"label{i:D2}").Append("missing").ToList();

        counter.Count = 0;
        var hits = await db.Store.FindByLabelsAsync(names, LabelMatch.Any, includeDescendants);

        hits.Should().HaveCount(50);
        counter.Count.Should().Be((includeDescendants ? 2 : 1) + 2, "名稱一次、階層一次(含子孫時)、兩個註冊型別各一次");

        (await db.Store.FindByLabelsAsync(new[] { "label00", "missing" }, LabelMatch.All, includeDescendants))
            .Should().BeEmpty("All 下缺一個名稱就是空的");
        (await db.Store.FindByLabelsAsync(new[] { "label01", "label00" }, LabelMatch.Any, includeDescendants))
            .Select(h => h.DisplayName).Should().BeEquivalentTo(new[] { "n00", "n01" });
    }
}
