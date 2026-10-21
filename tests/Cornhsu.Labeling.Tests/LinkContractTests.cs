using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 連結列的內容契約。LabelLink&lt;TEntity, TKey&gt; 是公開型別、連結表是消費端查得到的表,
/// 所以 AttachedAt 也是對外的資料:貼標當下以 UTC 寫入;重貼已存在的組合是冪等的,不改寫原本的貼標時間。
/// </summary>
public class LinkContractTests
{
    private static readonly DateTimeOffset Sentinel = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AttachAsync與AttachManyAsync_AttachedAt寫入貼標當下的UTC時間()
    {
        using var db = new TestDb();
        var single = await db.AddNoteAsync("single");
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");

        var before = DateTimeOffset.UtcNow;
        await db.Store.AttachAsync(single, "論文");
        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "論文", "急件" });
        var after = DateTimeOffset.UtcNow;

        // 從另一個 context 讀:驗的是資料庫裡的值
        using var other = db.CreateSecondContext();
        var links = await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().ToListAsync();
        links.Should().HaveCount(5);
        foreach (var link in links)
        {
            // Offset 斷言只在非 UTC 時區的機器上分得出 UtcNow 與 Now(CI runner 是 UTC;Npgsql 讀回一律是 offset 0)。
            // 「漏了賦值」這類回歸由下面的時間窗斷言負責,在任何時區都抓得到。
            link.AttachedAt.Offset.Should().Be(TimeSpan.Zero, "貼標時間以 UTC 寫入");
            // PostgreSQL 只存到微秒,上下各留 1 ms 的餘裕
            link.AttachedAt.Should().BeOnOrAfter(before.AddMilliseconds(-1)).And.BeOnOrBefore(after.AddMilliseconds(1));
        }
    }

    [Fact]
    public async Task 重貼已存在的組合_不改寫原本的AttachedAt_新的組合照常寫入()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");
        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "論文" });

        // 把既有連結的貼標時間改成一個不可能是「現在」的值,改寫與否就一目了然,不必靠等待時間流逝
        using (var other = db.CreateSecondContext())
        {
            foreach (var link in await other.Set<LabelLink<TestNote, Guid>>().ToListAsync())
                link.AttachedAt = Sentinel;
            await other.SaveChangesAsync();
        }

        // 模擬另一個請求:新的 context 與 store 重貼同樣的組合,外加一個新標籤
        using (var other = db.CreateSecondContext())
        {
            var store = LabelStoreFactory.Create(other, db.Registry);
            await store.AttachAsync(a, "論文");
            await store.AttachManyAsync(new[] { a, b }, new[] { "論文", "急件" });
        }

        using var check = db.CreateSecondContext();
        var links = await check.Set<LabelLink<TestNote, Guid>>().AsNoTracking()
            .Select(l => new { l.Label.Name, l.AttachedAt })
            .ToListAsync();
        links.Should().HaveCount(4);
        links.Where(l => l.Name == "論文").Select(l => l.AttachedAt)
            .Should().HaveCount(2).And.OnlyContain(t => t == Sentinel, "冪等:已存在的連結原封不動");
        links.Where(l => l.Name == "急件").Select(l => l.AttachedAt)
            .Should().HaveCount(2).And.OnlyContain(t => t > Sentinel, "新的連結寫入貼標當下的時間");
    }
}
