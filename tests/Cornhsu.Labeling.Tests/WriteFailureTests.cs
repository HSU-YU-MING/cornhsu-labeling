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
    }
}
