using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 名稱大小寫:套件只去前後空白、不動大小寫,比對交給資料庫 collation(README Limitations 明文:
/// SQLite 預設區分大小寫,SQL Server 預設不區分)。同一套測試在三個 provider 上都要成立。
/// </summary>
public class CaseSensitivityTests
{
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
