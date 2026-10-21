using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label name '研究' is already taken.");
    }

    [Fact]
    public async Task 把標籤設成自己的父標籤_拋例外()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.ParentId = label.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label '論文' cannot be its own parent.");
    }

    [Fact]
    public async Task 把標籤移到自己的子孫底下_拋例外防止循環()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("論文");
        var child = await db.Store.CreateAsync("文獻回顧", parentId: root.Id);
        var grandchild = await db.Store.CreateAsync("摘要筆記", parentId: child.Id);

        var act = () => db.Store.UpdateAsync(root.Id, l => l.ParentId = grandchild.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label '論文' cannot be moved under one of its own descendants (that would form a cycle).");
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

    [Fact]
    public async Task 移回頂層_ParentId設為null()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        var child = await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);

        await db.Store.UpdateAsync(child.Id, l => l.ParentId = null);

        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == child.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task 透過Update改名_去除前後空白_連結不受影響()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var label = (await db.Store.FindAsync("論文"))!;

        var updated = await db.Store.UpdateAsync(label.Id, l => l.Name = "  研究  ");

        updated.Name.Should().Be("研究");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == label.Id)).Name.Should().Be("研究");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("研究");
    }

    [Fact]
    public async Task 透過Update把名稱改成只差前後空白的自己_不算被占用()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        // 字串跟原名不同,所以會走改名檢查;去空白後就是自己的名字,占用檢查要排除標籤自己
        var updated = await db.Store.UpdateAsync(label.Id, l => l.Name = "  論文 ");

        updated.Name.Should().Be("論文");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == label.Id)).Name.Should().Be("論文");
    }

    [Fact]
    public async Task 回傳值是更新後的同一個標籤_戳記已輪換()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        var stampBefore = label.ConcurrencyStamp;

        var updated = await db.Store.UpdateAsync(label.Id, l => l.Color = "#FF5722");

        updated.Id.Should().Be(label.Id);
        updated.Color.Should().Be("#FF5722");
        updated.ConcurrencyStamp.Should().NotBe(stampBefore);
    }

    [Fact]
    public async Task update為null_拋出ArgumentNullException_ParamName為update()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, null!);

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("update");
    }

    [Fact]
    public async Task 標籤不存在_拋出帶Id的例外_且不執行update()
    {
        using var db = new TestDb();
        var missing = Guid.NewGuid();
        var called = false;

        var act = () => db.Store.UpdateAsync(missing, _ => called = true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"label {missing} not found.");
        called.Should().BeFalse("找不到標籤就沒有東西可以交給 update");
    }

    [Fact]
    public async Task 在update裡改Id_拋例外()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Id = Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("a label's Id must not be changed inside UpdateAsync.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 透過Update改成空白名稱_拋出ArgumentException_ParamName為update(string? name)
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Name = name!);

        (await act.Should().ThrowAsync<ArgumentException>().WithMessage("label name must not be blank.*"))
            .Which.ParamName.Should().Be("update");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == label.Id)).Name.Should().Be("論文");
    }

    [Fact]
    public async Task 透過Update改成超過上限的名稱_拋出ArgumentException()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Name = new string('長', Label.MaxNameLength + 1));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"label name is {Label.MaxNameLength + 1} characters, over the {Label.MaxNameLength} limit: '{new string('長', 16)}…'");
    }

    [Fact]
    public async Task 透過Update把父標籤指到不存在的Id_拋例外()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        var missing = Guid.NewGuid();

        var act = () => db.Store.UpdateAsync(label.Id, l => l.ParentId = missing);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"parent label {missing} does not exist.");
    }
}
