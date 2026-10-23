using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// AttachManyAsync:批次貼標(標籤解析一次、既有連結一次查詢、連結在一次 SaveChanges 寫入;
/// 要自動建立的標籤在那之前各自存檔)。
/// </summary>
public class AttachManyTests
{
    [Fact]
    public async Task 批次貼標_連結在一次存檔寫入_要自動建立的標籤各自先存一次()
    {
        var saves = new SaveCounter();
        using var db = new TestDb(null, saves);
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");
        var c = await db.AddNoteAsync("c");
        await db.Store.CreateAsync("既有");

        saves.Count = 0;
        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "既有", "新一", "新二" });
        saves.Count.Should().Be(3, "兩個要自動建立的標籤各存一次,所有連結再一起存一次:整個操作不是原子的");

        saves.Count = 0;
        await db.Store.AttachManyAsync(new[] { c }, new[] { "既有", "新一" });
        saves.Count.Should().Be(1, "標籤都已存在時,只有寫連結那一次");
    }

    [Fact]
    public async Task 批次貼標_多實體多標籤一次完成()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");
        var c = await db.AddNoteAsync("c");

        await db.Store.AttachManyAsync(new[] { a, b, c }, new[] { "論文", "急件" });

        var map = await db.Store.GetLabelsOfManyAsync(new[] { a, b, c });
        foreach (var note in new[] { a, b, c })
            map[note].Select(l => l.Name).Should().BeEquivalentTo(new[] { "論文", "急件" });
    }

    [Fact]
    public async Task 批次貼標_冪等_已貼過的組合不重複也不報錯()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");
        await db.Store.AttachAsync(a, "論文");                       // a 已有論文

        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "論文", "急件" });
        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "論文", "急件" });   // 再來一次

        var count = await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync();
        count.Should().Be(4, "2 實體 × 2 標籤,不多不少");
    }

    [Fact]
    public async Task 批次貼標_同一實體重複出現_只算一次()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync("a");

        await db.Store.AttachManyAsync(new[] { a, a, a }, new[] { "論文" });

        (await db.Store.GetLabelsOfAsync(a)).Should().ContainSingle();
    }

    [Fact]
    public async Task 批次貼標_空集合直接返回_不建立任何東西()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync();

        await db.Store.AttachManyAsync(Array.Empty<TestNote>(), new[] { "論文" });
        await db.Store.AttachManyAsync(new[] { a }, Array.Empty<string>());

        (await db.Store.GetAllAsync()).Should().BeEmpty("沒有實體或沒有名稱時不該建標籤");
    }

    [Fact]
    public async Task 批次貼標_AutoCreate停用且標籤缺漏_拋出例外且不留半套()
    {
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title);
            r.Labelable<TestTodo>(t => t.Content);
            r.AutoCreateLabels = false;
        });
        await db.Store.CreateAsync("論文");
        var a = await db.AddNoteAsync();

        var act = () => db.Store.AttachManyAsync(new[] { a }, new[] { "論文", "不存在" });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*do not exist*");

        (await db.Store.GetLabelsOfAsync(a)).Should().BeEmpty("整批失敗,不能貼一半");
    }

    [Fact]
    public async Task 批次貼標_null與含null實體_拋出例外()
    {
        using var db = new TestDb();
        var a = await db.AddNoteAsync();

        var actNull = () => db.Store.AttachManyAsync<TestNote>(null!, new[] { "x" });
        await actNull.Should().ThrowAsync<ArgumentNullException>();

        var actNames = () => db.Store.AttachManyAsync(new[] { a }, null!);
        await actNames.Should().ThrowAsync<ArgumentNullException>();

        var actItem = () => db.Store.AttachManyAsync(new[] { a, null! }, new[] { "x" });
        await actItem.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>數成功的 SaveChanges 次數。</summary>
    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
