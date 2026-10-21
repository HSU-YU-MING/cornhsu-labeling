using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>標籤 CRUD 的契約:建立時寫入的欄位、重名、空白名稱、找不到,以及改名與刪除的失敗路徑。</summary>
public class CrudContractTests
{
    [Fact]
    public async Task CreateAsync_寫入的欄位_Id與戳記非空_CreatedAt為UTC_排序預設為0()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("父標籤");

        var before = DateTimeOffset.UtcNow;
        var label = await db.Store.CreateAsync("子標籤", color: "#2196F3", icon: "📄", parentId: parent.Id);
        var after = DateTimeOffset.UtcNow;

        // 從另一個 context 讀:驗的是資料庫裡的值,不是 store 追蹤中的那個實例
        using var other = db.CreateSecondContext();
        var stored = await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == label.Id);

        stored.Id.Should().NotBeEmpty();
        stored.Name.Should().Be("子標籤");
        stored.Color.Should().Be("#2196F3");
        stored.Icon.Should().Be("📄");
        stored.ParentId.Should().Be(parent.Id);
        stored.SortOrder.Should().Be(0);
        stored.ConcurrencyStamp.Should().NotBeEmpty("戳記從建立那一刻就要有值,並發檢查才有基準");
        stored.ConcurrencyStamp.Should().NotBe(parent.ConcurrencyStamp, "每個標籤各自一個戳記");
        stored.CreatedAt.Offset.Should().Be(TimeSpan.Zero, "建立時間以 UTC 寫入");
        // PostgreSQL 只存到微秒,上下各留 1 ms 的餘裕
        stored.CreatedAt.Should().BeOnOrAfter(before.AddMilliseconds(-1)).And.BeOnOrBefore(after.AddMilliseconds(1));
    }

    [Fact]
    public async Task CreateAsync_同名已存在_拋出帶既有Id的例外且不新增()
    {
        using var db = new TestDb();
        var first = await db.Store.CreateAsync("論文");

        var act = () => db.Store.CreateAsync("  論文 ");   // 正規化後同名

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"label '論文' already exists (Id: {first.Id}).");
        (await db.Store.GetAllAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_名稱為null或空白_拋出ArgumentException_ParamName為name(string? name)
    {
        using var db = new TestDb();

        var act = () => db.Store.CreateAsync(name!);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("name");
        (await db.Store.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task FindAsync_名稱不存在或為null或空白_回傳null不拋例外()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("論文");

        (await db.Store.FindAsync("不存在")).Should().BeNull();
        (await db.Store.FindAsync(null!)).Should().BeNull();
        (await db.Store.FindAsync("")).Should().BeNull();
        (await db.Store.FindAsync("   ")).Should().BeNull();
    }

    [Fact]
    public async Task RenameAsync_標籤不存在_拋出帶Id的例外()
    {
        using var db = new TestDb();
        var missing = Guid.NewGuid();

        var act = () => db.Store.RenameAsync(missing, "新名");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"label {missing} not found.");
    }

    [Fact]
    public async Task RenameAsync_新名稱已被其他標籤占用_拋例外且原名不變()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("研究");
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.RenameAsync(label.Id, " 研究 ");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label name '研究' is already taken.");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == label.Id)).Name.Should().Be("論文");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameAsync_新名稱為null或空白_拋出ArgumentException_ParamName為newName(string? newName)
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.RenameAsync(label.Id, newName!);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("newName");
        (await db.Store.FindAsync("論文")).Should().NotBeNull();
    }

    [Fact]
    public async Task RenameAsync_去除前後空白後才存檔()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        await db.Store.RenameAsync(label.Id, "  研究  ");

        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == label.Id)).Name.Should().Be("研究");
    }

    [Fact]
    public async Task RenameAsync_改成自己目前的名稱_不算被占用()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.RenameAsync(label.Id, "論文");

        await act.Should().NotThrowAsync("占用檢查要排除標籤自己");
    }

    [Fact]
    public async Task DeleteAsync_標籤不存在_拋出帶Id的例外()
    {
        using var db = new TestDb();
        var missing = Guid.NewGuid();

        var act = () => db.Store.DeleteAsync(missing);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"label {missing} not found.");
    }

    [Fact]
    public async Task DeleteAsync_沒有連結的葉節點_直接刪除_父標籤之後也能刪()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        var leaf = await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);

        await db.Store.DeleteAsync(leaf.Id);

        (await db.Store.FindAsync("文獻回顧")).Should().BeNull();
        (await db.Store.FindAsync("論文")).Should().NotBeNull("刪子標籤不影響父標籤");

        await db.Store.DeleteAsync(parent.Id);   // 子標籤刪光之後,父標籤不再被擋
        (await db.Store.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_刪除標籤只清連結_實體本身還在()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync("留著");
        await db.Store.AttachAsync(note, "論文", "急件");

        await db.Store.DeleteAsync((await db.Store.FindAsync("論文"))!.Id);

        using var other = db.CreateSecondContext();
        (await other.Notes.AsNoTracking().CountAsync(n => n.Id == note.Id)).Should().Be(1);
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("急件");
    }
}
