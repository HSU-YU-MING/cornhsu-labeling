using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>讀取的契約:標籤清單的排序(SortOrder 先、Name 後)與使用次數統計的內容。</summary>
public class ReadContractTests
{
    // 名稱只用小寫 ASCII:各 provider 的 collation 對它們的先後一致,
    // 這裡測的是排序鍵本身,不是 collation(大小寫見 CaseSensitivityTests)。
    // 建立順序刻意和預期順序不同,少了 ThenBy(Name) 時同 SortOrder 的標籤會照建立順序出來。
    private static async Task<IReadOnlyList<Label>> SeedSortedLabelsAsync(TestDb db)
    {
        var labels = new List<Label>();
        foreach (var (name, sortOrder) in new[] { ("delta", 1), ("echo", 0), ("charlie", 1), ("alpha", 2), ("bravo", 1) })
        {
            var label = await db.Store.CreateAsync(name);
            if (sortOrder != 0)
                await db.Store.UpdateAsync(label.Id, l => l.SortOrder = sortOrder);
            labels.Add(label);
        }
        return labels;
    }

    private static readonly string[] ExpectedOrder = { "echo", "bravo", "charlie", "delta", "alpha" };

    [Fact]
    public async Task GetAllAsync_先依SortOrder再依名稱排序()
    {
        using var db = new TestDb();
        await SeedSortedLabelsAsync(db);

        (await db.Store.GetAllAsync()).Select(l => l.Name).Should().Equal(ExpectedOrder);
    }

    [Fact]
    public async Task GetLabelsOfAsync與GetLabelsOfManyAsync_先依SortOrder再依名稱排序()
    {
        using var db = new TestDb();
        await SeedSortedLabelsAsync(db);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "alpha", "delta", "bravo", "echo", "charlie");

        (await db.Store.GetLabelsOfAsync(note)).Select(l => l.Name).Should().Equal(ExpectedOrder);
        (await db.Store.GetLabelsOfManyAsync(new[] { note }))[note].Select(l => l.Name).Should().Equal(ExpectedOrder);
    }

    [Fact]
    public async Task GetUsageCountsAsync_沒有任何連結時回傳空字典()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("論文");
        await db.AddNoteAsync();

        (await db.Store.GetUsageCountsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsageCountsAsync_只列出至少用過一次的標籤_撕光的標籤也不出現()
    {
        using var db = new TestDb();
        var idle = await db.Store.CreateAsync("閒置");
        var note = await db.AddNoteAsync();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(note, "論文", "暫時");
        await db.Store.AttachAsync(todo, "論文");
        await db.Store.DetachAsync(note, "暫時");

        var counts = await db.Store.GetUsageCountsAsync();

        var paper = (await db.Store.FindAsync("論文"))!;
        var temporary = (await db.Store.FindAsync("暫時"))!;
        counts.Should().ContainSingle().Which.Should().Be(new KeyValuePair<Guid, int>(paper.Id, 2));
        counts.ContainsKey(idle.Id).Should().BeFalse("沒用過的標籤不以 0 出現");
        counts.ContainsKey(temporary.Id).Should().BeFalse();
    }
}
