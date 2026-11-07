using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>LabelRegistry.NameNormalizer:讓每個吃名稱的 API 都用同一套規則,不必依賴資料庫的 collation。</summary>
public class NameNormalizerTests
{
    private static TestDb Lowercasing() => new(r =>
    {
        r.Labelable<TestNote>(n => n.Title);
        r.NameNormalizer = n => n.ToLowerInvariant();
    });

    [Fact]
    public async Task 沒設定時_名稱照舊只去前後空白()
    {
        using var db = new TestDb();

        var label = await db.Store.CreateAsync("  Paper  ");

        label.Name.Should().Be("Paper");
        db.Registry.NameNormalizer.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync存的是正規化後的名稱_大小寫不同視為重複()
    {
        using var db = Lowercasing();

        var label = await db.Store.CreateAsync("  Paper  ");
        label.Name.Should().Be("paper");

        var act = () => db.Store.CreateAsync("PAPER");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 名稱查詢都走同一套規則()
    {
        using var db = Lowercasing();
        var note = await db.AddNoteAsync("note");
        await db.Store.AttachAsync(note, "Paper");

        (await db.Store.FindAsync("PAPER")).Should().NotBeNull();
        (await db.Store.FindByLabelAsync("PAPER")).Should().ContainSingle();
        (await db.Store.FindByLabelsAsync(new[] { "PAPER" }, LabelMatch.All)).Should().ContainSingle();
        (await (await db.Store.QueryByLabelAsync<TestNote>("PAPER")).CountAsync()).Should().Be(1);
        (await (await db.Store.QueryWithoutLabelsAsync<TestNote>(new[] { "PAPER" })).CountAsync()).Should().Be(0);
        (await db.Store.SuggestLabelsAsync("PAP")).Should().ContainSingle().Which.Name.Should().Be("paper");
    }

    [Fact]
    public async Task 貼標與撕標_大小寫不同是同一個標籤()
    {
        using var db = Lowercasing();
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "Paper", "PAPER", "paper");
        (await db.Store.GetAllAsync()).Should().ContainSingle().Which.Name.Should().Be("paper");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle();

        await db.Store.AttachManyAsync(new[] { note }, new[] { "PAPER" });
        (await db.Store.GetAllAsync()).Should().ContainSingle();

        await db.Store.DetachAsync(note, "PAPER");
        (await db.Store.GetLabelsOfAsync(note)).Should().BeEmpty();
    }

    [Fact]
    public async Task 整組取代_走同一套規則()
    {
        using var db = Lowercasing();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "paper");

        await db.Store.ReplaceLabelsAsync(note, new[] { "PAPER", "Urgent" });

        (await db.Store.GetLabelsOfAsync(note)).Select(l => l.Name).Should().BeEquivalentTo(new[] { "paper", "urgent" });
    }

    [Fact]
    public async Task 改名走同一套規則_撞名照常拋例外()
    {
        using var db = Lowercasing();
        var label = await db.Store.CreateAsync("old");
        await db.Store.CreateAsync("taken");

        await db.Store.RenameAsync(label.Id, "NEW");
        (await db.Store.FindAsync("new")).Should().NotBeNull();

        var act = () => db.Store.RenameAsync(label.Id, "TAKEN");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync改名走同一套規則_沒改名時不重寫名稱()
    {
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title);
            r.NameNormalizer = n => n.ToLowerInvariant();
        });
        // 模擬開啟正規化之前就存在的舊資料:繞過 store 直接寫進去
        var legacy = new Label
        {
            Id = Guid.NewGuid(),
            Name = "Legacy",
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid(),
        };
        db.Context.Set<Label>().Add(legacy);
        await db.Context.SaveChangesAsync();

        await db.Store.UpdateAsync(legacy.Id, l => l.Color = "#FF0000");
        legacy.Name.Should().Be("Legacy", "只改顏色時不該順手改名字,那可能撞到別的標籤");

        await db.Store.UpdateAsync(legacy.Id, l => l.Name = "Renamed");
        legacy.Name.Should().Be("renamed");
    }

    [Fact]
    public async Task 正規化結果會再去前後空白_變成空白就當成無效名稱()
    {
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title);
            r.NameNormalizer = n => n.Replace("#", " ");
        });
        var note = await db.AddNoteAsync();

        (await db.Store.CreateAsync("#paper#")).Name.Should().Be("paper");

        var create = () => db.Store.CreateAsync("###");
        await create.Should().ThrowAsync<ArgumentException>();
        (await db.Store.FindAsync("###")).Should().BeNull();

        await db.Store.AttachAsync(note, "###");
        (await db.Store.GetLabelsOfAsync(note)).Should().BeEmpty("跟空白名稱一樣被忽略");
    }

    [Fact]
    public async Task 長度上限看的是正規化之後的名稱()
    {
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title);
            r.NameNormalizer = n => n.Replace("-", "");
        });

        var fits = new string('a', Label.MaxNameLength) + "-";
        (await db.Store.CreateAsync(fits)).Name.Should().HaveLength(Label.MaxNameLength);

        var tooLong = new string('b', Label.MaxNameLength + 1);
        var act = () => db.Store.CreateAsync(tooLong);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task 正規化函式丟的例外_原樣傳給呼叫端()
    {
        var boom = new FormatException("名稱不合規則");
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title);
            r.NameNormalizer = _ => throw boom;
        });

        var act = () => db.Store.CreateAsync("x");

        (await act.Should().ThrowAsync<FormatException>()).Which.Should().BeSameAs(boom);
    }

    [Fact]
    public void 封存後不能換_設成同一個值可以()
    {
        Func<string, string> rule = n => n.ToLowerInvariant();
        var registry = new LabelRegistry { NameNormalizer = rule };
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title);
            r.NameNormalizer = rule;
        });

        db.Registry.NameNormalizer = rule;   // 同一個值:共用的設定程式重跑不該失敗

        var change = () => db.Registry.NameNormalizer = n => n.ToUpperInvariant();
        change.Should().Throw<InvalidOperationException>().WithMessage("*NameNormalizer*");
        var clear = () => db.Registry.NameNormalizer = null;
        clear.Should().Throw<InvalidOperationException>();

        registry.NameNormalizer = null;      // 還沒封存的可以改
        registry.NameNormalizer.Should().BeNull();
    }
}
