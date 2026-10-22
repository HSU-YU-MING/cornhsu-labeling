using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// UpdateAsync 被擋下時不能留下痕跡。lambda 改的是 DbContext 追蹤中的實體;驗證失敗若只拋例外,
/// 那份修改會留在追蹤器裡,在下一次 SaveChanges(store 的任何寫入,或消費端自己存檔)落地。
/// 所以每條都在失敗之後再做一次無關的寫入,然後從另一個 context 讀資料庫。
/// 另外涵蓋循環檢查看不到的兩條路:經導覽屬性換父標籤,以及同一個 context 裡其他標籤未存的 ParentId;
/// 以及 2.0 起 lambda 只能改目標標籤本身(別的標籤只准經 Parent / Children 搬移)。
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
        db.Context.Entry(a).Property(l => l.SortOrder).IsModified.Should().BeTrue("消費端自己的修改仍標成待存");
        db.Context.Entry(a).Property(l => l.ParentId).IsModified.Should().BeFalse("lambda 改的欄位連「已修改」旗標也還原");
        await db.Context.SaveChangesAsync();
        var stored = await StoredAsync(db, a.Id);
        stored.SortOrder.Should().Be(7);
        stored.ParentId.Should().BeNull();
    }

    // ---- lambda 只能改目標標籤本身;別的標籤只能經 Parent / Children 搬移(2.0,決策 D2 選項 B) ----

    [Theory]
    [InlineData("Name")]
    [InlineData("Color")]
    [InlineData("Icon")]
    [InlineData("SortOrder")]
    public async Task lambda經導覽屬性改了別的標籤的欄位_拋InvalidOperationException_什麼都不存(string field)
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c", parentId: parent.Id);
        var stamp = parent.ConcurrencyStamp;

        var act = () => db.Store.UpdateAsync(child.Id, l =>
        {
            l.Color = "#111111";   // 目標自己的修改也一起撤掉
            switch (field)
            {
                case "Name": l.Parent!.Name = "q"; break;
                case "Color": l.Parent!.Color = "#000000"; break;
                case "Icon": l.Parent!.Icon = "star"; break;
                default: l.Parent!.SortOrder = 5; break;
            }
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"update may only change the label being updated*but it changed {field} on label 'p' (Id: {parent.Id})*");
        child.Color.Should().BeNull();
        parent.Name.Should().Be("p");
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        await db.Store.CreateAsync("x");
        var stored = await StoredAsync(db, parent.Id);
        (stored.Name, stored.Color, stored.Icon, stored.SortOrder).Should().Be(("p", (string?)null, (string?)null, 0));
        stored.ConcurrencyStamp.Should().Be(stamp);
        (await StoredAsync(db, child.Id)).Color.Should().BeNull();
    }

    [Fact]
    public async Task lambda裡讓別的標籤接手或互換名稱_一律拒絕_改用各自的RenameAsync()
    {
        // 1.x 允許「目標讓出名字、父標籤接手」(第三輪審查 P7a)。2.0 起 lambda 不能改別的標籤
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("a", parentId: parent.Id);

        var attempts = new Func<Task>[]
        {
            () => db.Store.UpdateAsync(child.Id, l => { l.Name = "tmp"; l.Parent!.Name = "a"; }),   // 接手
            () => db.Store.UpdateAsync(child.Id, l => { l.Name = "p"; l.Parent!.Name = "a"; }),     // 互換
            () => db.Store.UpdateAsync(child.Id, l => { l.Name = "新"; l.Parent!.Name = "新"; }),   // 搶同一個新名字
        };
        foreach (var act in attempts)
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*but it changed Name on label 'p'*");
        (await StoredAsync(db, parent.Id)).Name.Should().Be("p");
        (await StoredAsync(db, child.Id)).Name.Should().Be("a");

        // 遷移寫法:各自呼叫
        await db.Store.RenameAsync(child.Id, "tmp");
        await db.Store.RenameAsync(parent.Id, "a");
        (await StoredAsync(db, parent.Id)).Name.Should().Be("a");
        (await StoredAsync(db, child.Id)).Name.Should().Be("tmp");
    }

    [Fact]
    public async Task lambda經導覽屬性帶進新標籤_拋InvalidOperationException_新標籤不會插入()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c");
        Label Fresh(string name) => new() { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };
        var viaChildren = Fresh("stray");
        var viaParent = Fresh("new parent");

        var addChild = () => db.Store.UpdateAsync(parent.Id, l => l.Children.Add(viaChildren));
        var newParent = () => db.Store.UpdateAsync(child.Id, l => l.Parent = viaParent);

        await addChild.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("update may only change the label being updated*but it added a new label 'stray'*CreateAsync*");
        await newParent.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*but it added a new label 'new parent'*");
        db.Context.Entry(viaChildren).State.Should().Be(EntityState.Detached);
        db.Context.Entry(viaParent).State.Should().Be(EntityState.Detached);
        parent.Children.Should().BeEmpty();
        child.ParentId.Should().BeNull();
        await db.Store.CreateAsync("x");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().Select(l => l.Name).ToListAsync())
            .Should().BeEquivalentTo(new[] { "p", "c", "x" });
    }

    [Fact]
    public async Task lambda自己載入別的標籤來改_或刪掉別的標籤_一樣拒絕()
    {
        using var db = new TestDb();
        var target = await db.Store.CreateAsync("t");
        var other = await db.Store.CreateAsync("o");
        db.Context.ChangeTracker.Clear();   // 呼叫前 other 不在追蹤器裡

        var edit = () => db.Store.UpdateAsync(target.Id, l =>
            db.Context.Set<Label>().Single(x => x.Name == "o").Color = "#000000");
        await edit.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*but it changed Color on label 'o' (Id: {other.Id})*");

        var loaded = await db.Context.Set<Label>().SingleAsync(x => x.Name == "o");
        var delete = () => db.Store.UpdateAsync(target.Id, l => db.Context.Remove(loaded));
        await delete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"update may only change the label being updated*but it deleted label 'o' (Id: {other.Id})*DeleteAsync*");
        db.Context.Entry(loaded).State.Should().Be(EntityState.Unchanged);

        await db.Store.CreateAsync("x");
        var stored = await StoredAsync(db, other.Id);
        stored.Color.Should().BeNull();
    }

    [Fact]
    public async Task lambda刪掉有連結的別的標籤被拒絕後_EF串聯刪掉的連結也不會在下一次存檔刪除()
    {
        // EF 預設在 Remove 的當下就把追蹤中的連結串聯標成刪除;只還原標籤的話,連結會在下一次存檔被刪掉
        using var db = new TestDb();
        var target = await db.Store.CreateAsync("t");
        var other = await db.Store.CreateAsync("o");
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "o");   // 連結在追蹤器裡

        var act = () => db.Store.UpdateAsync(target.Id, l => db.Context.Remove(other));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*but it deleted label 'o'*");
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        db.Context.ChangeTracker.CascadeDeleteTiming.Should().Be(Microsoft.EntityFrameworkCore.ChangeTracking.CascadeTiming.Immediate);
        await db.Context.SaveChangesAsync();
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("o");
        using var check = db.CreateSecondContext();
        (await check.Set<Cornhsu.Labeling.EntityFrameworkCore.LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task 消費端呼叫前自己刪掉的標籤_UpdateAsync存檔時連結照常串聯刪除_串聯時機設定原樣保留()
    {
        // lambda 執行期間暫時改了串聯時機;消費端自己在呼叫前做的刪除不受影響,設定也要放回去
        using var db = new TestDb();
        var target = await db.Store.CreateAsync("t");
        var doomed = await db.Store.CreateAsync("d");
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "d");
        db.Context.ChangeTracker.CascadeDeleteTiming = Microsoft.EntityFrameworkCore.ChangeTracking.CascadeTiming.OnSaveChanges;
        db.Context.Remove(doomed);

        await db.Store.UpdateAsync(target.Id, l => l.Color = "#111111");

        db.Context.ChangeTracker.CascadeDeleteTiming.Should().Be(Microsoft.EntityFrameworkCore.ChangeTracking.CascadeTiming.OnSaveChanges);
        using var check = db.CreateSecondContext();
        (await check.Set<Label>().AsNoTracking().Select(l => l.Name).ToListAsync()).Should().Equal("t");
        (await check.Set<Cornhsu.Labeling.EntityFrameworkCore.LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync())
            .Should().Be(0);
    }

    [Fact]
    public async Task 呼叫前消費端自己改了別的標籤還沒存_不算lambda的修改_照常一起存檔()
    {
        // 判斷的是 lambda 前後的差異:消費端自己在呼叫前做的修改不受限制(照舊隨這次存檔送出)
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b");
        a.Color = "#123456";
        a.Name = "a2";

        await db.Store.UpdateAsync(b.Id, l => l.SortOrder = 1);

        var stored = await StoredAsync(db, a.Id);
        (stored.Name, stored.Color).Should().Be(("a2", "#123456"));
    }

    [Fact]
    public async Task 呼叫前就在追蹤器裡的標籤_lambda沒改它的名稱_舊資料不驗也不改寫()
    {
        using var db = new TestDb();
        var legacy = await db.Store.CreateAsync("舊");
        var target = await db.Store.CreateAsync("目標");
        await WriteLegacyNameAsync(db, legacy.Id, "legacy ");
        await db.Context.Set<Label>().SingleAsync(l => l.Id == legacy.Id);   // 呼叫前就載進追蹤器(例如清單畫面)

        await db.Store.UpdateAsync(target.Id, l => l.Color = "#111111");

        (await StoredAsync(db, legacy.Id)).Name.Should().Be("legacy ", "只驗 lambda 改了名稱的標籤");
    }

    // ---- 還原前的偵測遇到身分衝突、自動建立標籤提交後才拋例外 ----

    // 同 Id 的另一個實例:EF 10 偵測時撞身分衝突,衝突的那一筆會以 Detached 留在追蹤器,之後每次存檔都壞。
    // store 要在自己偵測之前就認出來、擋下,剪掉之後再還原,context 才能繼續用

    [Fact]
    public async Task lambda把Parent設成同Id的另一個實例_沒有拋例外_store擋下_context仍可用()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c");
        var clone = new Label { Id = parent.Id, Name = "p", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(child.Id, l =>
        {
            l.Parent = clone;
            l.Color = "#123456";
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{parent.Id}*");
        child.Color.Should().BeNull();
        child.Parent.Should().NotBeSameAs(clone);
        await db.Store.CreateAsync("x");
        await db.Context.SaveChangesAsync();
        (await StoredAsync(db, child.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task lambda把同Id的另一個實例加進Children_沒有拋例外_store擋下_context仍可用()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c");
        var clone = new Label { Id = child.Id, Name = "c", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            l.Children.Add(clone);
            l.Color = "#123456";
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{child.Id}*");
        parent.Color.Should().BeNull();
        parent.Children.Should().NotContain(clone);
        await db.Store.CreateAsync("x");
        await db.Context.SaveChangesAsync();
        (await StoredAsync(db, child.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task lambda把Parent設成同Id的另一個實例又自己拋例外_原例外照拋_context仍可用()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c");
        var clone = new Label { Id = parent.Id, Name = "p", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(child.Id, l =>
        {
            l.Parent = clone;
            throw new FormatException("消費端自己的錯");
        });

        await act.Should().ThrowAsync<FormatException>();
        child.Parent.Should().NotBeSameAs(clone);
        await db.Store.CreateAsync("x");
        await db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task lambda經Children放進同Id的另一個實例又自己拋例外_原例外照拋_context仍可用()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var other = await db.Store.CreateAsync("o");
        var clone = new Label { Id = other.Id, Name = "o", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            l.Children.Add(clone);   // 偵測變更時會撞身分衝突(同 Id 已追蹤另一個實例)
            throw new FormatException("消費端自己的錯");
        });

        await act.Should().ThrowAsync<FormatException>("還原前的偵測失敗不能蓋掉原本的例外");
        await db.Store.CreateAsync("x");
    }

    [Fact]
    public async Task lambda改了父標籤的Id_被擋下後父標籤的主鍵放回_context可用()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c", parentId: parent.Id);
        var originalId = parent.Id;

        var act = () => db.Store.UpdateAsync(child.Id, l =>
        {
            l.Parent!.Id = Guid.NewGuid();
            l.Color = "#000000";
        });
        await act.Should().ThrowAsync<InvalidOperationException>();   // EF 偵測到主鍵被改

        parent.Id.Should().Be(originalId);
        child.Color.Should().BeNull();
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        await db.Store.CreateAsync("x");
    }

    /// <summary>拒絕移除的集合:Remove 在動到內容之前就拋例外(IsReadOnly 仍是 false)。</summary>
    private sealed class NoRemoveCollection : System.Collections.ObjectModel.Collection<Label>
    {
        public bool Armed { get; set; }

        protected override void RemoveItem(int index)
        {
            if (Armed)
                throw new NotSupportedException("這個集合不准移除");
            base.RemoveItem(index);
        }
    }

    [Fact]
    public async Task 同Id的另一個實例剪不掉時_還原前的偵測撞身分衝突_原例外照拋_其他修改照常還原()
    {
        // 集合拒絕 Remove,剪不掉那個實例:還原前的偵測一定會撞身分衝突,那個失敗只能吞掉,不能蓋掉原本的例外
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var other = await db.Store.CreateAsync("o");
        var children = new NoRemoveCollection();
        parent.Children = children;
        var clone = new Label { Id = other.Id, Name = "o", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            l.Children.Add(clone);
            children.Armed = true;
            l.Color = "#000000";
            throw new FormatException("消費端自己的錯");
        });

        await act.Should().ThrowAsync<FormatException>("還原前的偵測失敗不能蓋掉原本的例外");
        parent.Color.Should().BeNull("偵測失敗也要照常還原其他修改");
    }

    [Fact]
    public async Task 消費端已把某個標籤標成刪除還沒存_lambda改別的標籤被拒絕後_消費端的刪除原樣保留()
    {
        // 1.x 允許 lambda 讓別的標籤接手被刪標籤的名字(第四輪審查 S09);2.0 起拒絕,還原時不能動到消費端自己的刪除
        using var db = new TestDb();
        var target = await db.Store.CreateAsync("t");
        var holder = await db.Store.CreateAsync("q");
        var other = await db.Store.CreateAsync("x", parentId: target.Id);
        db.Context.Remove(holder);   // 消費端自己的未存刪除

        var act = () => db.Store.UpdateAsync(target.Id, l => l.Children.Single().Name = "q");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*but it changed Name on label 'x'*");
        other.Name.Should().Be("x");
        db.Context.Entry(holder).State.Should().Be(EntityState.Deleted);
        await db.Context.SaveChangesAsync();
        using var check = db.CreateSecondContext();
        (await check.Set<Label>().AsNoTracking().Select(l => l.Name).ToListAsync())
            .Should().BeEquivalentTo(new[] { "t", "x" });
    }

    [Fact]
    public async Task 目標自己的改名只看資料庫_名字被占用就擋下()
    {
        // 占用者在同一個 context 裡改了名字、還沒存也一樣:目標的改名與 RenameAsync 同一套規則
        using var db = new TestDb();
        var holder = await db.Store.CreateAsync("p");
        var target = await db.Store.CreateAsync("c");
        holder.Name = "p2";   // 消費端自己的未存改名

        var act = () => db.Store.UpdateAsync(target.Id, l => l.Name = "p");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("label name 'p' is already taken.");
        (await StoredAsync(db, target.Id)).Name.Should().Be("c");
        holder.Name.Should().Be("p2");
    }

    /// <summary>繞過 store 直接改資料庫裡的名稱(舊資料、別的工具寫入的),然後清空 db.Context 的追蹤器。</summary>
    private static async Task WriteLegacyNameAsync(TestDb db, Guid labelId, string name)
    {
        using (var other = db.CreateSecondContext())
        {
            var stored = await other.Set<Label>().SingleAsync(l => l.Id == labelId);
            stored.Name = name;
            await other.SaveChangesAsync();
        }
        db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task lambda只是載入父標籤來讀_名稱帶前後空白的舊資料不會被改寫()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c", parentId: parent.Id);
        await WriteLegacyNameAsync(db, parent.Id, "legacy ");

        await db.Store.UpdateAsync(child.Id, l =>
        {
            db.Context.Entry(l).Reference(x => x.Parent).Load();   // 只為了讀父標籤
            l.Color = l.Parent!.Color ?? "#111111";
        });

        (await StoredAsync(db, parent.Id)).Name.Should().Be("legacy ", "lambda 從沒寫過父標籤的名稱");
        (await StoredAsync(db, child.Id)).Color.Should().Be("#111111");
    }

    [Fact]
    public async Task lambda只是載入父標籤來讀_名稱超過上限的舊資料不會讓UpdateAsync失敗()
    {
        // SQL Server / PostgreSQL 的欄寬就是 64,寫不進這種舊資料;只有 SQLite 不強制長度
        var provider = Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() ?? "sqlite";
        if (provider != "sqlite") return;

        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c", parentId: parent.Id);
        await WriteLegacyNameAsync(db, parent.Id, new string('長', Label.MaxNameLength + 6));

        await db.Store.UpdateAsync(child.Id, l =>
        {
            db.Context.Entry(l).Reference(x => x.Parent).Load();
            l.Color = "#111111";
        });

        (await StoredAsync(db, child.Id)).Color.Should().Be("#111111");
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 消費端的自動偵測設定_成功與各種失敗路徑之後都維持原值(bool initial)
    {
        // store 內部列舉、還原時會暫時關掉自動偵測;每條路徑走完都要放回消費端原本的設定
        var failure = new FailNextSave();
        using var db = new TestDb(null, failure);
        var parent = await db.Store.CreateAsync("p");
        var child = await db.Store.CreateAsync("c", parentId: parent.Id);
        var tracker = db.Context.ChangeTracker;
        tracker.AutoDetectChangesEnabled = initial;
        var clone = new Label { Id = parent.Id, Name = "p", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var paths = new (string Name, Func<Task> Run)[]
        {
            ("成功", () => db.Store.UpdateAsync(child.Id, l => l.Color = "#111111")),
            ("lambda 自己拋例外", () => db.Store.UpdateAsync(child.Id, l => { l.Color = "#222222"; throw new FormatException(); })),
            ("目標名稱驗證失敗", () => db.Store.UpdateAsync(child.Id, l => l.Name = "   ")),
            ("改了別的標籤", () => db.Store.UpdateAsync(child.Id, l => l.Parent!.Name = "   ")),
            ("同 Id 的另一個實例", () => db.Store.UpdateAsync(child.Id, l => l.Parent = clone)),
            ("存檔失敗", () => { failure.Armed = true; return db.Store.UpdateAsync(child.Id, l => l.SortOrder = 3); }),
            ("刪除存檔失敗", () => { failure.Armed = true; return db.Store.DeleteAsync(child.Id); }),
        };
        foreach (var (name, run) in paths)
        {
            try { await run(); } catch (Exception) { /* 這裡只看設定有沒有放回去 */ }
            tracker.AutoDetectChangesEnabled.Should().Be(initial, $"「{name}」之後");
        }
    }

    /// <summary>Armed 時讓下一次存檔失敗(只失敗一次)。</summary>
    private sealed class FailNextSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new DbUpdateException("模擬的寫入失敗");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task 自動偵測變更關掉時_經Parent導覽屬性成環_一樣擋下()
    {
        // 自動偵測開著時,列舉追蹤器就會順便偵測;關掉之後只剩 lambda 後那次 DetectLabelChanges 讓循環檢查看得到
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("root");
        var child = await db.Store.CreateAsync("child", parentId: root.Id);
        db.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        var act = () => db.Store.UpdateAsync(root.Id, l => l.Parent = child);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*would form a cycle*");
        (await StoredAsync(db, root.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task 自動偵測變更關掉時_消費端還沒偵測的導覽屬性修改_UpdateAsync被擋下後原樣保留()
    {
        // 拍快照前先偵測:不然快照記的是舊外鍵,還原時會把消費端自己的搬移一起撤掉
        using var db = new TestDb();
        var a = await db.Store.CreateAsync("a");
        var b = await db.Store.CreateAsync("b");
        var target = await db.Store.CreateAsync("目標");
        db.Context.ChangeTracker.AutoDetectChangesEnabled = false;
        a.Parent = b;   // 消費端自己的搬移,還沒偵測、還沒存

        var act = () => db.Store.UpdateAsync(target.Id, l => l.Name = "   ");
        await act.Should().ThrowAsync<ArgumentException>();

        a.Parent.Should().BeSameAs(b);
        db.Context.ChangeTracker.DetectChanges();
        await db.Context.SaveChangesAsync();
        (await StoredAsync(db, a.Id)).ParentId.Should().Be(b.Id);
    }

    [Fact]
    public async Task lambda經Children帶進新標籤又自己拋例外_那個新標籤不會在下一次存檔插入()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var fresh = new Label { Id = Guid.NewGuid(), Name = "stray", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            l.Children.Add(fresh);
            throw new FormatException("消費端自己的錯");
        });
        await act.Should().ThrowAsync<FormatException>();

        db.Context.Entry(fresh).State.Should().Be(EntityState.Detached);
        parent.Children.Should().BeEmpty();
        await db.Store.CreateAsync("x");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().AnyAsync(l => l.Id == fresh.Id)).Should().BeFalse();
    }

    /// <summary>綁在畫面上的集合:Remove 時會拋例外(例如要在 UI 執行緒上改),但 IsReadOnly 是 false。</summary>
    private sealed class RefusingCollection : System.Collections.ObjectModel.ObservableCollection<Label>
    {
        public bool Armed { get; set; }

        protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (Armed && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                throw new NotSupportedException("綁定的集合只能在 UI 執行緒上修改");
            base.OnCollectionChanged(e);
        }
    }

    [Fact]
    public async Task Children是Remove會拋例外的集合_原本的例外照拋_其餘還原照做()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var children = new RefusingCollection();
        parent.Children = children;
        var fresh = new Label { Id = Guid.NewGuid(), Name = "stray", CreatedAt = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid() };

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            l.Children.Add(fresh);
            children.Armed = true;
            l.Name = "  ";
        });

        await act.Should().ThrowAsync<InvalidOperationException>("呼叫端要拿到的是驗證失敗,不是清理集合時的錯")
            .WithMessage("*but it added a new label 'stray'*");
        parent.Name.Should().Be("p");
        db.Context.Entry(fresh).State.Should().Be(EntityState.Detached);
        children.Armed = false;
        await db.Store.CreateAsync("x");
        (await StoredAsync(db, parent.Id)).Name.Should().Be("p");
    }

    [Fact]
    public async Task 還原外鍵時EF的導覽修正碰到不准改的集合_原本的例外照拋_其他項目照樣還原()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("p");
        var other = await db.Store.CreateAsync("o");
        var children = new RefusingCollection();
        parent.Children = children;

        var act = () => db.Store.UpdateAsync(parent.Id, l =>
        {
            l.Children.Add(other);   // 還原 other 的 ParentId 時,EF 會把它從這個集合拿掉 → 拋例外
            children.Armed = true;
            l.Name = "  ";
        });

        await act.Should().ThrowAsync<ArgumentException>();
        parent.Name.Should().Be("p");
        other.ParentId.Should().BeNull();
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
