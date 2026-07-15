using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>標籤名稱正規化:前後空白一律去除,避免產生視覺上相同的重複標籤。</summary>
public class NormalizationTests
{
    [Fact]
    public async Task 貼標時名稱帶前後空白_與去空白後的標籤是同一個()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(note, "  論文  ");   // 不應產生第二個標籤

        (await db.Store.GetAllAsync()).Should().ContainSingle().Which.Name.Should().Be("論文");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle();
    }

    [Fact]
    public async Task 建立標籤時自動去除前後空白()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("  急件  ");

        label.Name.Should().Be("急件");
        (await db.Store.FindAsync("急件")).Should().NotBeNull();
        (await db.Store.FindAsync("  急件"))!.Id.Should().Be(label.Id, "查詢輸入也要正規化");
    }

    [Fact]
    public async Task 查詢與撕標的名稱輸入也會正規化()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        (await db.Store.FindByLabelAsync(" 論文 ")).Should().ContainSingle();

        await db.Store.DetachAsync(note, " 論文 ");
        (await db.Store.GetLabelsOfAsync(note)).Should().BeEmpty();
    }

    [Fact]
    public async Task IEnumerable加CancellationToken的多載可用且行為一致()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, new List<string> { "論文", "急件" }, CancellationToken.None);
        (await db.Store.GetLabelsOfAsync(note)).Should().HaveCount(2);

        await db.Store.DetachAsync(note, new List<string> { "急件" }, CancellationToken.None);
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("論文");
    }
}
