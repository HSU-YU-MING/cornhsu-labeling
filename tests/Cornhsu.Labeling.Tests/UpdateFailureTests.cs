using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// UpdateAsync 被擋下時不能留下痕跡。lambda 改的是 DbContext 追蹤中的實體;驗證失敗若只拋例外,
/// 那份修改會留在追蹤器裡,在下一次 SaveChanges(store 的任何寫入,或消費端自己存檔)落地。
/// 所以每條都在失敗之後再做一次無關的寫入,然後從另一個 context 讀資料庫。
/// 另外涵蓋循環檢查看不到的兩條路:經導覽屬性換父標籤,以及同一個 context 裡其他標籤未存的 ParentId。
/// </summary>
public class UpdateFailureTests
{
    private static async Task<Label> StoredAsync(TestDb db, Guid id)
    {
        using var other = db.CreateSecondContext();
        return await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == id);
    }

    // ---- 驗證失敗後,被擋下的值不會在下一次存檔落地 ----

    [Fact]
    public async Task 循環被擋下後_下一次寫入不會把循環存進去()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b", parentId: a.Id);

        var act = () => db.Store.UpdateAsync(a.Id, l => l.ParentId = b.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("label 'a' cannot be moved under one of its own descendants (that would form a cycle).");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, a.Id)).ParentId.Should().BeNull();
        (await StoredAsync(db, b.Id)).ParentId.Should().Be(a.Id);
    }

    [Fact]
    public async Task 空白名稱被擋下後_下一次寫入不會把空白名稱存進去()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Name = "   ");
        await act.Should().ThrowAsync<ArgumentException>();
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, label.Id)).Name.Should().Be("論文");
    }

    [Fact]
    public async Task 消費端自己把名稱改成空白還沒存_UpdateAsync只改顏色也要擋下_名稱檢查比的是資料庫裡的值()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        label.Name = "   ";   // 消費端自己對追蹤中的標籤做的修改,不是這次 lambda 改的;UpdateAsync 存檔時會一起寫出去

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("label name must not be blank.*");
        var stored = await StoredAsync(db, label.Id);
        stored.Name.Should().Be("論文");
        stored.Color.Should().BeNull();
        label.Name.Should().Be("   ", "還原的是這次呼叫的修改,消費端自己的未存值原樣留著");
    }

    [Fact]
    public async Task 超長名稱被擋下後_下一次寫入不會把超長名稱存進去()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Name = new string('長', Label.MaxNameLength + 1));
        await act.Should().ThrowAsync<ArgumentException>();
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, label.Id)).Name.Should().Be("論文");
    }

    [Fact]
    public async Task 名稱已被占用而擋下後_下一次寫入不會撞唯一索引()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("研究");
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Name = "研究");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label name '研究' is already taken.");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, label.Id)).Name.Should().Be("論文");
    }

    [Fact]
    public async Task 改Id被擋下後_context仍可正常存檔()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l =>
        {
            l.Id = Guid.NewGuid();
            l.Color = "#FF0000";
        });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("a label's Id must not be changed inside UpdateAsync.");

        await db.Store.CreateAsync("x");   // 沒還原的話,每次存檔都拋「key 不可修改」
        label.Id.Should().NotBeEmpty();
        var stored = await StoredAsync(db, label.Id);
        stored.Color.Should().BeNull("同一個 lambda 裡的其他修改也一起撤掉");
    }

    [Fact]
    public async Task 被擋下後_追蹤中的實體回到呼叫前的值_context沒有待存的變更()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b", parentId: a.Id);
        var stamp = a.ConcurrencyStamp;

        var act = () => db.Store.UpdateAsync(a.Id, l =>
        {
            l.Color = "#FF0000";
            l.SortOrder = 9;
            l.ParentId = b.Id;
        });
        await act.Should().ThrowAsync<InvalidOperationException>();

        a.Color.Should().BeNull();
        a.SortOrder.Should().Be(0);
        a.ParentId.Should().BeNull();
        a.ConcurrencyStamp.Should().Be(stamp);
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        (await db.Store.FindAsync("a"))!.Color.Should().BeNull("同一個 store 重讀拿到的是追蹤中的實例");
    }

    [Fact]
    public async Task 呼叫前就有的未存變更_被擋下後原樣保留()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b", parentId: a.Id);
        a.SortOrder = 7;   // 消費端自己對追蹤中的標籤做的修改,還沒存

        var act = () => db.Store.UpdateAsync(a.Id, l => l.ParentId = b.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();

        a.SortOrder.Should().Be(7, "還原的是這次呼叫做的修改,不是消費端自己的");
        a.ParentId.Should().BeNull();
        await db.Context.SaveChangesAsync();
        var stored = await StoredAsync(db, a.Id);
        stored.SortOrder.Should().Be(7);
        stored.ParentId.Should().BeNull();
    }

    // ---- 在偵測變更之前就失敗:經導覽屬性的修改那時還沒寫回外鍵,還原也要撤得掉 ----

    [Fact]
    public async Task lambda經Parent導覽屬性改了之後自己拋例外_下一次寫入不會成環()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("root");
        var child = await db.Store.CreateAsync("child", parentId: root.Id);

        var act = () => db.Store.UpdateAsync(root.Id, l =>
        {
            l.Parent = child;
            throw new FormatException("消費端自己的錯");
        });
        await act.Should().ThrowAsync<FormatException>();
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
        root.Parent.Should().BeNull();
    }

    [Fact]
    public async Task lambda經Children改了之後自己拋例外_下一次寫入不會成環()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("root");
        var child = await db.Store.CreateAsync("child", parentId: root.Id);

        var act = () => db.Store.UpdateAsync(child.Id, l =>
        {
            l.Children.Add(root);
            throw new FormatException("消費端自己的錯");
        });
        await act.Should().ThrowAsync<FormatException>();
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
        child.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task lambda經Parent導覽屬性改了又改Id_被擋下後下一次寫入不會成環()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("root");
        var child = await db.Store.CreateAsync("child", parentId: root.Id);

        var act = () => db.Store.UpdateAsync(root.Id, l =>
        {
            l.Parent = child;
            l.Id = Guid.NewGuid();
        });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("a label's Id must not be changed inside UpdateAsync.");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task lambda改了Id又自己拋例外_原例外照拋_不被還原時的錯蓋掉_context仍可用()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("root");
        var child = await db.Store.CreateAsync("child", parentId: root.Id);

        var act = () => db.Store.UpdateAsync(root.Id, l =>
        {
            l.Parent = child;
            l.Id = Guid.NewGuid();
            throw new FormatException("消費端自己的錯");
        });
        await act.Should().ThrowAsync<FormatException>();

        root.Id.Should().NotBeEmpty();
        await db.Store.CreateAsync("x");   // 主鍵沒放回去的話,這次存檔會拋「key 不可修改」
        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
    }

    // ---- 消費端關掉自動偵測變更:store 改的欄位仍要存進去,戳記也要跟著輪換 ----

    [Fact]
    public async Task 自動偵測變更關掉時_RenameAsync單獨呼叫就存進資料庫_戳記照常輪換()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("原名");
        db.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        await db.Store.RenameAsync(label.Id, "新名");

        // 這裡就斷言,不接著呼叫其他寫入方法:那會把沒存進去的改名一起帶出去,測不到 RenameAsync 自己
        var stored = await StoredAsync(db, label.Id);
        stored.Name.Should().Be("新名");
        stored.ConcurrencyStamp.Should().Be(label.ConcurrencyStamp);
    }

    [Fact]
    public async Task 自動偵測變更關掉時_UpdateAsync偵測每個追蹤中的標籤_但不替消費端偵測其他型別的實體()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b");
        var note = await db.AddNoteAsync("原標題");
        db.Context.ChangeTracker.AutoDetectChangesEnabled = false;
        a.Color = "#123456";          // 消費端對另一個標籤的修改,沒有偵測
        note.Title = "改過的標題";    // 消費端對別的型別的修改,沒有偵測

        await db.Store.UpdateAsync(b.Id, l => l.SortOrder = 1);

        // lambda 可能經導覽屬性動到別的標籤,循環檢查要看得到,所以每個追蹤中的標籤都會偵測、跟著送出
        (await StoredAsync(db, a.Id)).Color.Should().Be("#123456");
        using var other = db.CreateSecondContext();
        (await other.Notes.AsNoTracking().SingleAsync(n => n.Id == note.Id)).Title.Should().Be("原標題");
    }

    [Fact]
    public async Task 自動偵測變更關掉時_RenameAsync與UpdateAsync仍會存檔_戳記照常輪換()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("原名");
        db.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        await db.Store.RenameAsync(label.Id, "新名");
        await db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");
        await db.Store.UpdateAsync(label.Id, l => l.Name = "  再改  ");   // 戳記沒存進去的話,這次會撞並發例外

        var stored = await StoredAsync(db, label.Id);
        stored.Name.Should().Be("再改");
        stored.Color.Should().Be("#FF0000");
        stored.ConcurrencyStamp.Should().Be(label.ConcurrencyStamp);
    }

    // ---- 循環檢查要看得到導覽屬性,以及同一個 context 裡其他標籤未存的 ParentId ----

    [Fact]
    public async Task 經Parent導覽屬性移到自己的子孫底下_拋循環例外_資料庫不變()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("論文");
        var child = await db.Store.CreateAsync("文獻回顧", parentId: root.Id);

        var act = () => db.Store.UpdateAsync(root.Id, l => l.Parent = child);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("label '論文' cannot be moved under one of its own descendants (that would form a cycle).");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
        (await StoredAsync(db, child.Id)).ParentId.Should().Be(root.Id);
        root.Parent.Should().BeNull();
        child.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task 經Children把祖先加到自己底下_拋循環例外_資料庫不變()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("論文");
        var child = await db.Store.CreateAsync("文獻回顧", parentId: root.Id);

        var act = () => db.Store.UpdateAsync(child.Id, l => l.Children.Add(root));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*would form a cycle*");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
        (await StoredAsync(db, child.Id)).ParentId.Should().Be(root.Id);
        root.ParentId.Should().BeNull();
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
    }

    [Fact]
    public async Task 經Parent導覽屬性指向自己_拋自我父標籤例外()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");

        var act = () => db.Store.UpdateAsync(label.Id, l => l.Parent = l);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label '論文' cannot be its own parent.");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, label.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task 經導覽屬性的合法搬移_照常存檔()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("A");
        var b = await db.Store.CreateAsync("B");
        var child = await db.Store.CreateAsync("child", parentId: a.Id);
        var other = await db.Store.CreateAsync("other");

        await db.Store.UpdateAsync(child.Id, l => l.Parent = b);
        await db.Store.UpdateAsync(b.Id, l => l.Children.Add(other));

        (await StoredAsync(db, child.Id)).ParentId.Should().Be(b.Id);
        (await StoredAsync(db, other.Id)).ParentId.Should().Be(b.Id);
    }

    [Fact]
    public async Task lambda經導覽屬性讓另外兩個標籤互為父子_也擋下()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("工作");
        var c1 = await db.Store.CreateAsync("c1", parentId: parent.Id);
        var c2 = await db.Store.CreateAsync("c2", parentId: parent.Id);

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            var kids = l.Children.OrderBy(k => k.Name).ToList();
            kids[0].Parent = kids[1];
            kids[1].Parent = kids[0];
        });
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*would form a cycle*");
        await db.Store.CreateAsync("x");

        (await StoredAsync(db, c1.Id)).ParentId.Should().Be(parent.Id);
        (await StoredAsync(db, c2.Id)).ParentId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task 同一個context裡別的標籤有未存的ParentId變更_循環檢查看得到()
    {
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b");
        a.ParentId = b.Id;   // 還沒存:單看這一筆不成環,跟下面那次搬移一起存才會成環

        var act = () => db.Store.UpdateAsync(b.Id, l => l.ParentId = a.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("label 'b' cannot be moved under one of its own descendants (that would form a cycle).");

        (await StoredAsync(db, a.Id)).ParentId.Should().BeNull();
        (await StoredAsync(db, b.Id)).ParentId.Should().BeNull();
    }
}
