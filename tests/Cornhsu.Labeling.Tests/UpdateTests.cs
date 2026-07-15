using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>UpdateAsync:欄位更新、改名規則、父標籤循環防護。</summary>
public class UpdateTests
{
    [Fact]
    public async Task 建立時可帶入顏色與圖示()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文", color: "#2196F3", icon: "📄");

        var reloaded = await db.Store.FindAsync("論文");
        reloaded!.Color.Should().Be("#2196F3");
        reloaded.Icon.Should().Be("📄");
    }

    [Fact]
    public async Task 可透過Update修改圖示()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        await db.Store.UpdateAsync(label.Id, l => l.Icon = "🔖");

        (await db.Store.FindAsync("論文"))!.Icon.Should().Be("🔖");
    }

    [Fact]
    public async Task 更新顏色與排序_存檔成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        await db.Store.UpdateAsync(label.Id, l =>
        {
            l.Color = "#FF5722";
            l.SortOrder = 3;
        });

        var reloaded = await db.Store.FindAsync("論文");
        reloaded!.Color.Should().Be("#FF5722");
        reloaded.SortOrder.Should().Be(3);
    }

    [Fact]
    public async Task 透過Update改名_與Rename同樣受唯一性檢查()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("研究");
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Name = "研究");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已被使用*");
    }

    [Fact]
    public async Task 把標籤設成自己的父標籤_拋例外()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.ParentId = label.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*自己的父標籤*");
    }

    [Fact]
    public async Task 把標籤移到自己的子孫底下_拋例外防止循環()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("論文");
        var child = await db.Store.CreateAsync("文獻回顧", parentId: root.Id);
        var grandchild = await db.Store.CreateAsync("摘要筆記", parentId: child.Id);

        var act = () => db.Store.UpdateAsync(root.Id, l => l.ParentId = grandchild.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*循環*");
    }

    [Fact]
    public async Task 合法的父標籤搬移_成功()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("A");
        var b = await db.Store.CreateAsync("B");
        var child = await db.Store.CreateAsync("child", parentId: a.Id);

        await db.Store.UpdateAsync(child.Id, l => l.ParentId = b.Id);

        (await db.Store.FindAsync("child"))!.ParentId.Should().Be(b.Id);
    }
}
