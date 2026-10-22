using System.Diagnostics;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Cornhsu.Labeling.Tests;

/// <summary>效能防線不跟其他測試平行跑:平行時的 CPU 競爭會讓計時失真(release.yml 也跑這些測試,假紅會擋發佈)。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceGuardCollection
{
    public const string Name = "效能防線";
}

/// <summary>
/// 效能的退化防線。
/// <list type="bullet">
/// <item>主防線直接量 <c>TrackerSnapshot.ArmForSave</c>(經 InternalsVisibleTo):每次成功的寫入都會走它,
/// 第二輪審查抓到它的去重寫成了 O(n²)。6 萬個待存項目,線性約百毫秒,O(n²) 要十幾秒,門檻 3 秒兩邊餘裕都很大,
/// 也不受 provider 與機器快慢影響。</item>
/// <item>端到端那條量 AttachManyAsync 的「筆數加倍、時間變幾倍」:線性約 2 倍,平方約 4 倍。
/// 只當粗略的防線(門檻 3 倍),抓的是整條路徑上任何地方退化成平方;取三次交錯量測的中位數。
/// 只在 SQLite 上跑:伺服器型 provider 的往返時間會蓋掉要量的東西,而且慢。</item>
/// </list>
/// </summary>
[Collection(PerformanceGuardCollection.Name)]
public class PerformanceGuardTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceGuardTests(ITestOutputHelper output) => _output = output;

    private static bool OnSqlite
        => (Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() ?? "sqlite") == "sqlite";

    [Fact]
    public void ArmForSave_幾萬個待存項目_去重是線性的()
    {
        using var db = new TestDb();
        var snapshot = new TrackerSnapshot(db.Context);
        const int count = 60_000;
        for (var i = 0; i < count; i++)
        {
            var note = new TestNote { Id = Guid.NewGuid(), Title = "" };
            db.Context.Notes.Add(note);
            snapshot.Added(note);
        }
        snapshot.ArmForSave();   // 暖機:JIT 掉第一次的成本(結果會被下一次覆蓋)

        var watch = Stopwatch.StartNew();
        snapshot.ArmForSave();
        watch.Stop();

        _output.WriteLine($"PERFGUARD ArmForSave {count} items: {watch.Elapsed.TotalMilliseconds:F1} ms");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
            $"ArmForSave 處理 {count:N0} 個待存項目花了 {watch.Elapsed.TotalMilliseconds:F0} ms;線性的去重約百毫秒,O(n²) 要十幾秒");
    }

    private static async Task<TimeSpan> TimeAttachManyAsync(int notes)
    {
        using var db = new TestDb();
        var list = Enumerable.Range(0, notes).Select(i => new TestNote { Id = Guid.NewGuid(), Title = $"n{i}" }).ToList();
        db.Context.AddRange(list);
        await db.Context.SaveChangesAsync();
        await db.Store.CreateAsync("a");
        await db.Store.CreateAsync("b");
        await db.Store.AttachManyAsync(list.Take(10), new[] { "a" });   // 暖機

        var watch = Stopwatch.StartNew();
        await db.Store.AttachManyAsync(list, new[] { "a", "b" });
        watch.Stop();
        return watch.Elapsed;
    }

    [Fact]
    public async Task AttachManyAsync連結數加倍_時間大致也只加倍()
    {
        if (!OnSqlite) return;
        const int small = 5_000;   // × 2 個標籤 = 10,000 條連結;加倍後 20,000 條

        var smallRuns = new List<double>();
        var largeRuns = new List<double>();
        for (var round = 0; round < 3; round++)   // 交錯量:兩種大小輪流,機器忽快忽慢時兩邊一起受影響
        {
            smallRuns.Add((await TimeAttachManyAsync(small)).TotalMilliseconds);
            largeRuns.Add((await TimeAttachManyAsync(small * 2)).TotalMilliseconds);
        }

        static double Median(List<double> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);
        var growth = Median(largeRuns) / Math.Max(Median(smallRuns), 1);
        _output.WriteLine($"PERFGUARD AttachMany growth: {growth:F2} (small {string.Join("/", smallRuns.Select(v => v.ToString("F0")))} ms, large {string.Join("/", largeRuns.Select(v => v.ToString("F0")))} ms)");

        growth.Should().BeLessThan(3.0,
            $"連結數加倍,AttachManyAsync 從 {Median(smallRuns):F0} ms 變成 {Median(largeRuns):F0} ms({growth:F2} 倍);"
            + "線性約 2 倍,平方約 4 倍");
    }
}
