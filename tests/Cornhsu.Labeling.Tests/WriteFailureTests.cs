using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 寫入方法的 SaveChanges 失敗之後,store 要把自己放進消費端 context 的變更撤掉。
/// 消費端的 context 常是長壽的(README「Without a DI container」教的就是這種用法):
/// 沒撤掉的話,被拒絕的新增或刪除會留在追蹤器裡,之後每一次存檔都撞同一個錯;
/// 並發衝突之後,同一個 store 重讀會拿到沒存進去的值,重試也永遠失敗。
/// </summary>
public class WriteFailureTests
{
    private static async Task<List<Label>> StoredLabelsAsync(TestDb db)
    {
        using var other = db.CreateSecondContext();
        return await other.Set<Label>().AsNoTracking().ToListAsync();
    }

    private static async Task<int> StoredLinkCountAsync(TestDb db)
    {
        using var other = db.CreateSecondContext();
        return await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync();
    }

    private static bool HasPendingLabelOrLink(DbContext context)
        => context.ChangeTracker.Entries()
            .Any(e => e.State != EntityState.Unchanged && (e.Entity is Label || e.Entity is LabelLink<TestNote, Guid>));

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync存檔失敗後_不留下新增中的標籤_之後的寫入照常()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Added && e.Entity.Name == "論文"));
        using var db = new TestDb(null, failure);
        var parent = await db.Store.CreateAsync("工作");

        var act = () => db.Store.CreateAsync("論文", parentId: parent.Id);
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure.Thrown);

        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        parent.Children.Should().BeEmpty("沒存成功的子標籤不能還掛在父標籤的導覽屬性上");
        await db.Store.CreateAsync("研究");
        (await StoredLabelsAsync(db)).Select(l => l.Name).Should().BeEquivalentTo(new[] { "工作", "研究" });
    }

    [Fact]
    public async Task CreateAsync輸掉同名競態_拋出例外後_context仍可用()
    {
        var rival = new RivalCreatesSameName("論文");
        using var db = new TestDb(null, rival);
        rival.CreateRivalContext = db.CreateSecondContext;
        var note = await db.AddNoteAsync();

        var act = () => db.Store.CreateAsync("論文");
        await act.Should().ThrowAsync<DbUpdateException>("CreateAsync 沒有 get-or-create 語意,撞唯一索引就照拋");

        rival.WinnerId.Should().NotBeNull();
        await db.Store.AttachAsync(note, "論文");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Id.Should().Be(rival.WinnerId!.Value);
    }

    // ---- RenameAsync 非並發的存檔失敗 ----

    [Fact]
    public async Task RenameAsync存檔失敗後_新名字不會在之後的存檔落地()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Modified));
        using var db = new TestDb(null, failure);
        var label = await db.Store.CreateAsync("原名");

        var act = () => db.Store.RenameAsync(label.Id, "新名");
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure.Thrown);

        label.Name.Should().Be("原名");
        await db.AddNoteAsync();   // 消費端無關的存檔
        (await StoredLabelsAsync(db)).Single().Name.Should().Be("原名");
    }

    // ---- UpdateAsync / RenameAsync / DeleteAsync 的並發衝突:接住、重讀、重試 ----

    [Fact]
    public async Task UpdateAsync並發衝突後_同一個store重讀拿到資料庫的值_重試成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("治療", "#CDDC39");
        using var other = db.CreateSecondContext();
        var otherStore = LabelStoreFactory.Create(other, db.Registry);
        await other.Set<Label>().FirstAsync(l => l.Id == label.Id);   // 先讀進來:拿到的是舊戳記
        await db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");

        var act = () => otherStore.UpdateAsync(label.Id, l => l.Color = "#00FF00");
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        other.ChangeTracker.HasChanges().Should().BeFalse();
        (await otherStore.FindAsync("治療"))!.Color.Should().Be("#FF0000", "重讀要拿到資料庫的值,不是剛才沒存進去的值");
        await otherStore.UpdateAsync(label.Id, l => l.Color = "#00FF00");
        (await StoredLabelsAsync(db)).Single().Color.Should().Be("#00FF00");
    }

    /// <summary>從另一個 context 改這個標籤並輪換戳記:db.Context 手上的就成了舊戳記。</summary>
    private static async Task ChangeElsewhereAsync(TestDb db, Guid labelId)
    {
        using var other = db.CreateSecondContext();
        var stored = await other.Set<Label>().SingleAsync(l => l.Id == labelId);
        stored.Icon = "別處改的";
        stored.ConcurrencyStamp = Guid.NewGuid();
        await other.SaveChangesAsync();
    }

    // 刻意的取捨(README Limitations 寫明):並發衝突時,這次要寫的那個標籤一律重讀成資料庫的值,
    // 連消費端自己對它還沒存的修改也一起丟掉——不重讀的話,舊戳記留在追蹤器裡,重試永遠撞同一個衝突。
    // 別的項目上消費端自己的未存修改則不碰(見下一條之後的那條)。

    [Fact]
    public async Task UpdateAsync並發衝突時_目標標籤上消費端自己的未存修改也被重讀掉_重試成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("目標");
        await ChangeElsewhereAsync(db, label.Id);
        label.Color = "#FFFFFF";   // 消費端自己對目標標籤的未存修改

        var act = () => db.Store.UpdateAsync(label.Id, l => l.SortOrder = 5);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        label.Color.Should().BeNull("目標標籤重讀成資料庫的值");
        label.Icon.Should().Be("別處改的");
        db.Context.Entry(label).State.Should().Be(EntityState.Unchanged);
        await db.Store.UpdateAsync(label.Id, l => l.SortOrder = 5);
        (await StoredLabelsAsync(db)).Single().SortOrder.Should().Be(5);
    }

    [Fact]
    public async Task RenameAsync並發衝突時_目標標籤上消費端自己的未存修改也被重讀掉_重試成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("原名");
        await ChangeElsewhereAsync(db, label.Id);
        label.Color = "#FFFFFF";

        var act = () => db.Store.RenameAsync(label.Id, "新名");
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        label.Color.Should().BeNull();
        label.Name.Should().Be("原名");
        await db.Store.RenameAsync(label.Id, "新名");
        (await StoredLabelsAsync(db)).Single().Name.Should().Be("新名");
    }

    [Fact]
    public async Task DeleteAsync並發衝突時_目標標籤上消費端自己的未存修改也被重讀掉_重試成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        await ChangeElsewhereAsync(db, label.Id);
        label.Color = "#FFFFFF";

        var act = () => db.Store.DeleteAsync(label.Id);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        label.Color.Should().BeNull();
        db.Context.Entry(label).State.Should().Be(EntityState.Unchanged);
        await db.Store.DeleteAsync(label.Id);
        (await StoredLabelsAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task 並發衝突出在消費端自己改過的別的標籤上_store不重讀它_消費端的修改原樣保留()
    {
        using var db = new TestDb();
        var mine = await db.Store.CreateAsync("我改的");
        var target = await db.Store.CreateAsync("要更新的");
        using (var other = db.CreateSecondContext())
        {
            var stale = await other.Set<Label>().SingleAsync(l => l.Id == mine.Id);
            stale.Color = "#000000";
            stale.ConcurrencyStamp = Guid.NewGuid();
            await other.SaveChangesAsync();   // 資料庫裡 mine 的戳記已輪換,db.Context 手上的是舊的
        }
        mine.Color = "#FFFFFF";   // 消費端自己對追蹤中的標籤做的修改,還沒存

        var act = () => db.Store.UpdateAsync(target.Id, l => l.SortOrder = 5);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        mine.Color.Should().Be("#FFFFFF", "衝突的是消費端自己的修改,store 不能替他丟掉");
        db.Context.Entry(mine).State.Should().Be(EntityState.Modified);
        target.SortOrder.Should().Be(0, "store 自己這次的修改照樣撤掉");
    }

    [Fact]
    public async Task RenameAsync並發衝突後_同一個store重讀拿到資料庫的值_重試成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("原名");
        using var other = db.CreateSecondContext();
        var otherStore = LabelStoreFactory.Create(other, db.Registry);
        await other.Set<Label>().FirstAsync(l => l.Id == label.Id);
        await db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");

        var act = () => otherStore.RenameAsync(label.Id, "新名");
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        other.ChangeTracker.HasChanges().Should().BeFalse();
        (await otherStore.FindAsync("原名"))!.Color.Should().Be("#FF0000");
        (await otherStore.FindAsync("新名")).Should().BeNull();
        await otherStore.RenameAsync(label.Id, "新名");
        (await StoredLabelsAsync(db)).Single().Name.Should().Be("新名");
    }

    [Fact]
    public async Task DeleteAsync並發衝突後_重試成功()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        using var other = db.CreateSecondContext();
        var otherStore = LabelStoreFactory.Create(other, db.Registry);
        await other.Set<Label>().FirstAsync(l => l.Id == label.Id);
        await db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");

        var act = () => otherStore.DeleteAsync(label.Id);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        other.ChangeTracker.HasChanges().Should().BeFalse();
        await otherStore.DeleteAsync(label.Id);
        (await StoredLabelsAsync(db)).Should().BeEmpty();
    }

    // ---- DeleteAsync 失敗:EF 已經 cascade 到追蹤中的連結與參照它的實體,都要放回去 ----

    [Fact]
    public async Task DeleteAsync存檔失敗後_標籤與它的連結都還原_之後的存檔不會刪掉它們()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Deleted));
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");   // 連結留在追蹤器裡:刪標籤時 EF 會立刻把它 cascade 成刪除
        var label = (await db.Store.FindAsync("論文"))!;

        var act = () => db.Store.DeleteAsync(label.Id);
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure.Thrown);

        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        await db.Context.SaveChangesAsync();
        (await StoredLabelsAsync(db)).Should().ContainSingle();
        (await StoredLinkCountAsync(db)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync存檔失敗後_參照它的SetNull外鍵也還原()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Deleted));
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var registry = new LabelRegistry().Labelable<TestNote>();
        var options = new DbContextOptionsBuilder<ClassifiedDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).AddInterceptors(failure).Options;
        using var context = new ClassifiedDbContext(options, registry);
        context.Database.EnsureCreated();
        var store = LabelStoreFactory.Create(context, registry);
        var label = await store.CreateAsync("分類");
        var item = new Classified { Id = Guid.NewGuid(), CategoryId = label.Id };
        context.Add(item);
        await context.SaveChangesAsync();

        var act = () => store.DeleteAsync(label.Id);
        await act.Should().ThrowAsync<DbUpdateException>();

        item.CategoryId.Should().Be(label.Id, "EF 刪除標籤時立刻把參照它的外鍵設成 null,失敗後要放回去");
        context.ChangeTracker.HasChanges().Should().BeFalse();
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        (await context.Set<Classified>().SingleAsync()).CategoryId.Should().Be(label.Id);
        (await context.Set<Label>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync存檔失敗後_參照它的實體原本就有未存修改_外鍵與修改都原樣保留()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Deleted));
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var registry = new LabelRegistry().Labelable<TestNote>();
        var options = new DbContextOptionsBuilder<ClassifiedDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).AddInterceptors(failure).Options;
        using var context = new ClassifiedDbContext(options, registry);
        context.Database.EnsureCreated();
        var store = LabelStoreFactory.Create(context, registry);
        var label = await store.CreateAsync("分類");
        var other = await store.CreateAsync("另一個");
        var item = new Classified { Id = Guid.NewGuid(), CategoryId = other.Id };
        context.Add(item);
        await context.SaveChangesAsync();
        item.CategoryId = label.Id;   // 消費端自己的未存修改:改掛到要刪的那個標籤,狀態是 Modified

        var act = () => store.DeleteAsync(label.Id);
        await act.Should().ThrowAsync<DbUpdateException>();

        item.CategoryId.Should().Be(label.Id, "EF 刪除時把它設成 null,失敗後要放回消費端自己改的值");
        context.Entry(item).State.Should().Be(EntityState.Modified);
    }

    [Fact]
    public async Task DeleteAsync存檔失敗後_多層cascade下去的項目也還原()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Deleted));
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var registry = new LabelRegistry().Labelable<TestNote>();
        var options = new DbContextOptionsBuilder<ClassifiedDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).AddInterceptors(failure).Options;
        using var context = new ClassifiedDbContext(options, registry);
        context.Database.EnsureCreated();
        var store = LabelStoreFactory.Create(context, registry);
        var label = await store.CreateAsync("分類");
        var pin = new Pin { Id = Guid.NewGuid(), LabelId = label.Id };
        var pinNote = new PinNote { Id = Guid.NewGuid(), PinId = pin.Id };
        context.AddRange(pin, pinNote);
        await context.SaveChangesAsync();

        var act = () => store.DeleteAsync(label.Id);
        await act.Should().ThrowAsync<DbUpdateException>();

        // pin 直接參照標籤;pinNote 參照的是 pin,只會被 EF 的第二層 cascade 標成刪除
        context.Entry(pinNote).State.Should().Be(EntityState.Unchanged);
        context.ChangeTracker.HasChanges().Should().BeFalse();
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        (await context.Set<PinNote>().CountAsync()).Should().Be(1);
        (await context.Set<Pin>().CountAsync()).Should().Be(1);
    }

    // ---- 貼標 / 撕標 ----

    [Fact]
    public async Task AttachAsync實體不在資料庫_外鍵擋下後_context仍可正常存檔()
    {
        using var db = new TestDb();
        var ghost = new TestNote { Id = Guid.NewGuid(), Title = "沒存過" };

        var act = () => db.Store.AttachAsync(ghost, "論文");
        await act.Should().ThrowAsync<DbUpdateException>();

        HasPendingLabelOrLink(db.Context).Should().BeFalse("違反外鍵的連結不能留在追蹤器裡");
        await db.Store.CreateAsync("研究");
        (await StoredLinkCountAsync(db)).Should().Be(0);
    }

    [Fact]
    public async Task AttachManyAsync存檔失敗後_不留下新增中的連結_重試成功()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<LabelLink<TestNote, Guid>>().Any(e => e.State == EntityState.Added));
        using var db = new TestDb(null, failure);
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");

        var act = () => db.Store.AttachManyAsync(new[] { a, b }, new[] { "急件" });
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure.Thrown);

        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "急件" });
        (await StoredLinkCountAsync(db)).Should().Be(2);
    }

    [Fact]
    public async Task DetachAsync存檔失敗後_連結還原_之後的存檔不會把它刪掉()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<LabelLink<TestNote, Guid>>().Any(e => e.State == EntityState.Deleted));
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        var act = () => db.Store.DetachAsync(note, "論文");
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure.Thrown);

        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        await db.Context.SaveChangesAsync();
        (await StoredLinkCountAsync(db)).Should().Be(1);
    }

    [Fact]
    public async Task DetachAsync連結已被別人撕掉_並發例外後重試成功()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");   // 連結留在追蹤器裡
        using (var other = db.CreateSecondContext())
        {
            other.Set<LabelLink<TestNote, Guid>>().RemoveRange(await other.Set<LabelLink<TestNote, Guid>>().ToListAsync());
            await other.SaveChangesAsync();
        }

        var act = () => db.Store.DetachAsync(note, "論文");
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        await db.Store.DetachAsync(note, "論文");
        (await db.Store.GetLabelsOfAsync(note)).Should().BeEmpty();
    }

    [Fact]
    public async Task 自動建立標籤時被取消_不留下新增中的標籤()
    {
        var failure = new FailOnce(
            c => c.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Added),
            () => new OperationCanceledException());
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();

        var act = () => db.Store.AttachAsync(note, "論文");
        await act.Should().ThrowAsync<OperationCanceledException>();

        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        await db.Store.AttachAsync(note, "論文");
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("論文");
    }

    // ---- 存檔已經提交、之後才拋例外(例如消費端的 SavedChanges 攔截器):寫進去的就是寫進去了,不能還原 ----

    [Fact]
    public async Task DeleteAsync提交後才拋例外_不把已刪掉的標籤放回追蹤器()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var label = (await db.Store.FindAsync("論文"))!;

        after.Armed = true;
        var act = () => db.Store.DeleteAsync(label.Id);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(after.Thrown);

        db.Context.Entry(label).State.Should().Be(EntityState.Detached, "刪除已經提交");
        HasPendingLabelOrLink(db.Context).Should().BeFalse();
        (await db.Store.FindAsync("論文")).Should().BeNull();
        await db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateAsync提交後才拋例外_已提交的值不會被之後的存檔蓋回去()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);
        var label = await db.Store.CreateAsync("論文");

        after.Armed = true;
        var act = () => db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");
        await act.Should().ThrowAsync<InvalidOperationException>();

        label.Color.Should().Be("#FF0000");
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        await db.AddNoteAsync();
        await db.Store.UpdateAsync(label.Id, l => l.SortOrder = 3);
        var stored = (await StoredLabelsAsync(db)).Single();
        stored.Color.Should().Be("#FF0000");
        stored.SortOrder.Should().Be(3);
    }

    [Fact]
    public async Task UpdateAsync提交後才拋例外_一起提交的消費端修改不會又被標成待存()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);
        var target = await db.Store.CreateAsync("目標");
        var mine = await db.Store.CreateAsync("我的");
        mine.SortOrder = 7;   // 消費端自己的未存修改:會跟著 UpdateAsync 的存檔一起提交

        after.Armed = true;
        var act = () => db.Store.UpdateAsync(target.Id, l => l.Color = "#FF0000");
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Context.Entry(mine).State.Should().Be(EntityState.Unchanged, "它已經跟著提交了,不能被還原成待存");
        db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        (await StoredLabelsAsync(db)).Single(l => l.Id == mine.Id).SortOrder.Should().Be(7);
    }

    [Fact]
    public async Task RenameAsync提交後才拋例外_已提交的名稱不會被之後的存檔蓋回去()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);
        var label = await db.Store.CreateAsync("原名");

        after.Armed = true;
        var act = () => db.Store.RenameAsync(label.Id, "新名");
        await act.Should().ThrowAsync<InvalidOperationException>();

        label.Name.Should().Be("新名");
        await db.AddNoteAsync();
        (await StoredLabelsAsync(db)).Single().Name.Should().Be("新名");
    }

    [Fact]
    public async Task CreateAsync提交後才拋例外_已建立的標籤留在追蹤器()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);

        after.Armed = true;
        var act = () => db.Store.CreateAsync("論文");
        await act.Should().ThrowAsync<InvalidOperationException>();

        var tracked = db.Context.ChangeTracker.Entries<Label>().Should().ContainSingle().Subject;
        tracked.State.Should().Be(EntityState.Unchanged);
        (await StoredLabelsAsync(db)).Should().ContainSingle();
    }

    [Fact]
    public async Task AttachAsync提交後才拋例外_已寫入的連結留在追蹤器_再貼一次照常()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);
        var note = await db.AddNoteAsync();
        await db.Store.CreateAsync("論文");

        after.Armed = true;
        var act = () => db.Store.AttachAsync(note, "論文");
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Context.ChangeTracker.Entries<LabelLink<TestNote, Guid>>().Should().ContainSingle()
            .Which.State.Should().Be(EntityState.Unchanged);
        await db.Store.AttachAsync(note, "論文");
        (await StoredLinkCountAsync(db)).Should().Be(1);
    }

    [Fact]
    public async Task DetachAsync提交後才拋例外_不把已刪掉的連結放回追蹤器_再撕一次照常()
    {
        var after = new ThrowAfterSave();
        using var db = new TestDb(null, after);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        after.Armed = true;
        var act = () => db.Store.DetachAsync(note, "論文");
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Context.ChangeTracker.Entries<LabelLink<TestNote, Guid>>().Should().BeEmpty("刪除已經提交");
        await db.Store.DetachAsync(note, "論文");   // 幽靈連結還在的話,這次會撞並發例外
        (await StoredLinkCountAsync(db)).Should().Be(0);
    }

    /// <summary>存檔提交之後才拋例外(只拋一次),模擬消費端的 SavedChanges 攔截器(稽核、outbox)出錯。</summary>
    private sealed class ThrowAfterSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public Exception? Thrown { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Thrown = new InvalidOperationException("模擬提交之後才發生的錯誤");
                throw Thrown;
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>條件第一次成立的那次存檔失敗(只失敗一次)。</summary>
    private sealed class FailOnce : SaveChangesInterceptor
    {
        private readonly Func<DbContext, bool> _when;
        private readonly Func<Exception> _exception;

        public FailOnce(Func<DbContext, bool> when, Func<Exception>? exception = null)
            => (_when, _exception) = (when, exception ?? (() => new DbUpdateException("模擬的寫入失敗")));

        public Exception? Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Thrown is null && _when(eventData.Context!))
            {
                Thrown = _exception();
                throw Thrown;
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>store 要新增名為 name 的標籤時,先用另一個 context 建好同名標籤並存檔(只做一次)。</summary>
    private sealed class RivalCreatesSameName : SaveChangesInterceptor
    {
        private readonly string _name;
        private bool _fired;

        public RivalCreatesSameName(string name) => _name = name;

        public Func<TestDbContext>? CreateRivalContext { get; set; }
        public Guid? WinnerId { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired && eventData.Context!.ChangeTracker.Entries<Label>()
                    .Any(e => e.State == EntityState.Added && e.Entity.Name == _name))
            {
                _fired = true;   // 先立旗:對手 context 共用同一份 options,它的 SaveChanges 也會經過這裡
                using var rival = CreateRivalContext!();
                var winner = new Label
                {
                    Id = Guid.NewGuid(),
                    Name = _name,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ConcurrencyStamp = Guid.NewGuid(),
                };
                rival.Set<Label>().Add(winner);
                await rival.SaveChangesAsync(cancellationToken);
                WinnerId = winner.Id;
            }
            return result;
        }
    }
}

/// <summary>消費端自己的實體,以可為 null 的外鍵參照標籤,刪標籤時設成 null(QuillNest 的單選分類就是這個形狀)。</summary>
public class Classified
{
    public Guid Id { get; set; }
    public Guid? CategoryId { get; set; }
    public Label? Category { get; set; }
}

/// <summary>以必填外鍵參照標籤、刪標籤時 cascade 刪除的實體。</summary>
public class Pin
{
    public Guid Id { get; set; }
    public Guid LabelId { get; set; }
    public Label? Label { get; set; }
}

/// <summary>參照 <see cref="Pin"/> 的實體:刪標籤時被第二層 cascade 刪除。</summary>
public class PinNote
{
    public Guid Id { get; set; }
    public Guid PinId { get; set; }
    public Pin? Pin { get; set; }
}

public class ClassifiedDbContext : DbContext
{
    private readonly LabelRegistry _registry;

    public ClassifiedDbContext(DbContextOptions<ClassifiedDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyLabelModel(_registry);
        b.Entity<Classified>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Category).WithMany()
             .HasForeignKey(x => x.CategoryId)
             .OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Pin>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Label).WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PinNote>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Pin).WithMany().HasForeignKey(x => x.PinId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
