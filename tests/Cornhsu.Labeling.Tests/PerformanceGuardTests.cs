using System.Diagnostics;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 效能的退化防線。不設絕對時間(CI 機器快慢不一),而是跟「同一個程序裡、同樣筆數的純 EF 寫入」比倍數:
/// 實測(30,000 條連結):線性的實作約 1 倍,寫壞成 O(n²)(第二輪審查抓到的 ArmForSave 去重)約 4 倍,
/// 門檻 2.5 倍兩邊都有餘裕。
/// 只在 SQLite 上跑:伺服器型 provider 的往返時間會蓋掉要量的東西,而且慢。
/// </summary>
public class PerformanceGuardTests
{
    private static bool OnSqlite
        => (Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() ?? "sqlite") == "sqlite";

    private static async Task<List<TestNote>> AddNotesAsync(TestDb db, int count)
    {
        var notes = Enumerable.Range(0, count).Select(i => new TestNote { Id = Guid.NewGuid(), Title = $"n{i}" }).ToList();
        db.Context.AddRange(notes);
        await db.Context.SaveChangesAsync();
        return notes;
    }

    [Fact]
    public async Task AttachManyAsync大批連結_成本與同樣筆數的純EF寫入同一個量級()
    {
        if (!OnSqlite) return;
        const int notesPerBatch = 15_000;   // × 2 個標籤 = 30,000 條連結

        // 純 EF 寫入同樣筆數的連結:基準
        using var baselineDb = new TestDb();
        var baselineNotes = await AddNotesAsync(baselineDb, notesPerBatch);
        var x = await baselineDb.Store.CreateAsync("x");
        var y = await baselineDb.Store.CreateAsync("y");
        var links = baselineNotes.SelectMany(n => new[]
        {
            new LabelLink<TestNote, Guid> { LabelId = x.Id, EntityId = n.Id, AttachedAt = DateTimeOffset.UtcNow },
            new LabelLink<TestNote, Guid> { LabelId = y.Id, EntityId = n.Id, AttachedAt = DateTimeOffset.UtcNow },
        }).ToList();
        baselineDb.Context.AddRange(links.Take(10));   // 暖機:兩邊都先 JIT 掉第一次寫入的成本
        await baselineDb.Context.SaveChangesAsync();
        var baseline = Stopwatch.StartNew();
        baselineDb.Context.AddRange(links.Skip(10));
        await baselineDb.Context.SaveChangesAsync();
        baseline.Stop();

        // 同樣筆數走 AttachManyAsync
        using var db = new TestDb();
        var notes = await AddNotesAsync(db, notesPerBatch);
        await db.Store.CreateAsync("a");
        await db.Store.CreateAsync("b");
        await db.Store.AttachManyAsync(notes.Take(10), new[] { "a" });   // 暖機:JIT 掉第一次呼叫的成本
        var attach = Stopwatch.StartNew();
        await db.Store.AttachManyAsync(notes, new[] { "a", "b" });
        attach.Stop();

        (await db.Context.Set<LabelLink<TestNote, Guid>>().CountAsync()).Should().Be(notesPerBatch * 2);
        attach.Elapsed.TotalMilliseconds.Should().BeLessThan(
            Math.Max(baseline.Elapsed.TotalMilliseconds, 200) * 2.5,
            $"AttachManyAsync 花了 {attach.Elapsed.TotalMilliseconds:F0} ms,同樣筆數的純 EF 寫入花了 {baseline.Elapsed.TotalMilliseconds:F0} ms;"
            + "倍數拉開通常是存檔前後的記帳(TrackerSnapshot)寫成了 O(n²)");
    }
}
