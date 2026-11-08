using System.Reflection;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>SuggestLabelsAsync:標籤輸入框的自動完成,以前綴找標籤,不必把全部標籤載入。</summary>
public class SuggestLabelsTests
{
    private static async Task<string[]> SuggestNamesAsync(TestDb db, string prefix, int limit = 10)
        => (await db.Store.SuggestLabelsAsync(prefix, limit)).Select(l => l.Name).ToArray();

    [Fact]
    public async Task 只回傳前綴相符的標籤()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("paper");
        await db.Store.CreateAsync("parent");
        await db.Store.CreateAsync("urgent");
        await db.Store.CreateAsync("spa");

        (await SuggestNamesAsync(db, "pa")).Should().BeEquivalentTo(new[] { "paper", "parent" }, "spa 只是含有,不是開頭");
    }

    [Fact]
    public async Task 排序跟GetAllAsync一樣_先SortOrder再名稱()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("pa-b");
        var last = await db.Store.CreateAsync("pa-a");
        await db.Store.UpdateAsync(last.Id, l => l.SortOrder = 5);
        await db.Store.CreateAsync("pa-c");

        (await SuggestNamesAsync(db, "pa")).Should().Equal("pa-b", "pa-c", "pa-a");
    }

    [Fact]
    public async Task limit限制筆數_預設10筆()
    {
        using var db = new TestDb();
        for (var i = 0; i < 12; i++)
            await db.Store.CreateAsync($"pa-{i:00}");

        (await SuggestNamesAsync(db, "pa", limit: 3)).Should().Equal("pa-00", "pa-01", "pa-02");
        (await db.Store.SuggestLabelsAsync("pa")).Should().HaveCount(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task limit小於1_拋例外(int limit)
    {
        using var db = new TestDb();

        var act = () => db.Store.SuggestLabelsAsync("pa", limit);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("limit");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空白前綴_回傳空清單_不會把全部標籤倒出來(string? prefix)
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("paper");

        (await db.Store.SuggestLabelsAsync(prefix!)).Should().BeEmpty();
    }

    [Fact]
    public async Task 前綴會去前後空白()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("paper");

        (await SuggestNamesAsync(db, "  pa  ")).Should().Equal("paper");
    }

    [Theory]
    [InlineData("100%", "100% done", "1000 ways")]
    [InlineData("a_b", "a_b c", "axb c")]
    public async Task 前綴裡的萬用字元是字面值(string prefix, string match, string nonMatch)
    {
        using var db = new TestDb();
        await db.Store.CreateAsync(match);
        await db.Store.CreateAsync(nonMatch);

        (await SuggestNamesAsync(db, prefix)).Should().Equal(match);
    }

    [Fact]
    public async Task 中文前綴()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("論壇");
        await db.Store.CreateAsync("急件");

        (await SuggestNamesAsync(db, "論")).Should().BeEquivalentTo(new[] { "論文", "論壇" });
    }

    [Fact]
    public async Task 已取消的token_拋例外()
    {
        using var db = new TestDb();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => db.Store.SuggestLabelsAsync("pa", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 其他ILabelStore實作_拋NotSupported()
    {
        var other = DispatchProxy.Create<ILabelStore, ReplaceLabelsTests.UnimplementedStore>();

        var act = () => other.SuggestLabelsAsync("pa");

        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage($"*{nameof(ILabelSuggester)}*");
    }
}
