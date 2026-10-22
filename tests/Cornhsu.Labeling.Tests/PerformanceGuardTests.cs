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
/// 效能的退化防線:直接量 <c>TrackerSnapshot.ArmForSave</c>(經 InternalsVisibleTo)。每次成功的寫入都會走它,
/// 第二輪審查抓到它的去重寫成了 O(n²)(AttachManyAsync 4 萬條連結從 1.9 s 變成 8.5 s)。
/// 6 萬個待存項目,線性實測 20–70 ms,O(n²) 要十幾秒,門檻 3 秒兩邊餘裕都很大,不受 provider 與機器快慢影響。
/// <para>
/// 沒有改量端到端的 AttachManyAsync:試過「連結數加倍、時間變幾倍」(線性應約 2、平方約 4),
/// 但 EF 10 把大量的 Contains 展開成一個值一個參數,SQL 那一段本身就不是線性,線性的實作在 EF 10 上量到 2.5–2.85,
/// 找不到一個既不會假紅、又抓得到 O(n²) 的門檻。
/// </para>
/// </summary>
[Collection(PerformanceGuardCollection.Name)]
public class PerformanceGuardTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceGuardTests(ITestOutputHelper output) => _output = output;

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
}
