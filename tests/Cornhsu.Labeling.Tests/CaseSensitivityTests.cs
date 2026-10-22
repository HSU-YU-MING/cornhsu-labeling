using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 名稱大小寫:套件只去前後空白、不動大小寫,比對一律交給資料庫 collation(README Limitations 明文:
/// SQLite 預設區分大小寫,SQL Server 預設不區分)。同一套測試在三個 provider 上都要成立。
/// 以名稱查詢的 FindByLabel(s)Async / QueryByLabel(s)Async 在 1.x 是在記憶體以 Ordinal 比對,
/// 2.0 起跟其他 API 一樣在資料庫端比對(決策 H3 選項 A)。
/// </summary>
public class CaseSensitivityTests
{
    [Fact]
    public async Task 不分大小寫的collation上_以名稱查詢的API跟其他API一樣依collation比對名稱()
    {
        using var db = new CaseInsensitiveDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "paper");
        await db.Store.AttachAsync(note, "Paper");   // 依 collation 是同一個標籤:沿用,不另建

        (await db.Store.GetAllAsync()).Should().ContainSingle().Which.Name.Should().Be("paper");
        (await db.Store.FindAsync("PAPER"))!.Name.Should().Be("paper", "FindAsync 在資料庫端比對,跟 collation 走");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle();

        // 以名稱查詢的四支 API 也一樣:FindAsync 找得到的標籤,它們就查得到
        (await db.Store.FindByLabelAsync("paper")).Should().ContainSingle();
        (await db.Store.FindByLabelAsync("Paper")).Should().ContainSingle();
        (await db.Store.FindByLabelAsync("PAPER", includeDescendants: false)).Should().ContainSingle();
        (await db.Store.FindByLabelsAsync(new[] { "paper", "PAPER" }, LabelMatch.All)).Should().ContainSingle();
        (await db.Store.FindByLabelsAsync(new[] { "Paper", "missing" }, LabelMatch.Any)).Should().ContainSingle();
        (await db.Store.QueryByLabelAsync<TestNote>("Paper")).ToList().Should().ContainSingle();
        (await db.Store.QueryByLabelsAsync<TestNote>(new[] { "PAPER" })).ToList().Should().ContainSingle();
    }

    [Fact]
    public async Task 不分大小寫的collation上_以名稱查詢含子孫標籤_根標籤的名稱大小寫不同也展開子孫()
    {
        using var db = new CaseInsensitiveDb();
        var root = await db.Store.CreateAsync("Work");
        await db.Store.CreateAsync("Review", parentId: root.Id);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "review");

        (await db.Store.FindByLabelAsync("WORK")).Should().ContainSingle("子標籤 Review 上的筆記也算");
        (await db.Store.FindByLabelAsync("WORK", includeDescendants: false)).Should().BeEmpty();
        (await (await db.Store.QueryByLabelsAsync<TestNote>(new[] { "work", "REVIEW" }, LabelMatch.All)).CountAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task 不分大小寫的collation上_同一批貼標裡只差大小寫的新名稱_只建一個標籤()
    {
        using var db = new CaseInsensitiveDb();
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "Paper", "paper");
        await db.Store.AttachManyAsync(new[] { note }, new[] { "URGENT", "urgent" });

        (await db.Store.GetAllAsync()).Select(l => l.Name).Should().BeEquivalentTo(new[] { "Paper", "URGENT" });
        (await db.Store.GetLabelsOfAsync(note)).Should().HaveCount(2);
    }

    [Fact]
    public async Task 名稱保留原始大小寫_不做大小寫正規化()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        var created = await db.Store.CreateAsync("Paper");
        await db.Store.AttachAsync(note, " MiXeD Case ");

        created.Name.Should().Be("Paper");
        (await db.Store.GetAllAsync()).Select(l => l.Name).Should().BeEquivalentTo(new[] { "Paper", "MiXeD Case" });
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("MiXeD Case");
    }

    [Fact]
    public async Task 只差大小寫的名稱_建立查找貼標撕標都依資料庫collation且彼此一致()
    {
        using var db = new TestDb();
        var paper = await db.Store.CreateAsync("Paper");
        var note = await db.AddNoteAsync();

        // 以資料庫自己的字串比較當基準,不寫死 provider:這正是 README 承諾的「交給 collation」
        var collationIgnoresCase = await db.Context.Set<Label>().AsNoTracking().AnyAsync(l => l.Name == "paper");

        if (collationIgnoresCase)
        {
            // SQL Server 預設 collation:Paper 與 paper 是同一個標籤
            var duplicate = () => db.Store.CreateAsync("paper");
            await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
            (await db.Store.FindAsync("PAPER"))!.Id.Should().Be(paper.Id);

            await db.Store.AttachAsync(note, "paper");
            (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Id.Should().Be(paper.Id);
            (await db.Store.GetAllAsync()).Should().ContainSingle("貼標的 get-or-create 也要認得同一個標籤");

            (await db.Store.FindByLabelAsync("PAPER")).Should().ContainSingle("以名稱查詢也依 collation 比對");
            (await (await db.Store.QueryByLabelAsync<TestNote>("pAPER")).CountAsync()).Should().Be(1);

            await db.Store.DetachAsync(note, "PAPER");
            (await db.Store.GetLabelsOfAsync(note)).Should().BeEmpty();
        }
        else
        {
            // SQLite / PostgreSQL 預設:Paper 與 paper 是兩個標籤
            var lower = await db.Store.CreateAsync("paper");
            lower.Id.Should().NotBe(paper.Id);
            (await db.Store.FindAsync("paper"))!.Id.Should().Be(lower.Id);
            (await db.Store.FindAsync("PAPER")).Should().BeNull();

            await db.Store.AttachAsync(note, "paper");
            (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Id.Should().Be(lower.Id);
            (await db.Store.GetAllAsync()).Should().HaveCount(2, "貼 paper 不能貼到 Paper 身上");
            (await db.Store.FindByLabelAsync("Paper")).Should().BeEmpty();
            (await db.Store.FindByLabelAsync("paper")).Should().ContainSingle();

            await db.Store.DetachAsync(note, "Paper");   // 另一個標籤,撕不到 paper
            (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Id.Should().Be(lower.Id);
        }
    }
}
