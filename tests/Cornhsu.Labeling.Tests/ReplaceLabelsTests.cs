using System.Reflection;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// ReplaceLabelsAsync:把實體的標籤整組換成指定的這幾個。連結的增減在一次存檔裡,保留的連結不重建,
/// 集合沒有變化就不存檔;要自動建立的標籤跟 AttachAsync 一樣先各自存檔。
/// </summary>
public class ReplaceLabelsTests
{
    private static async Task<string[]> NamesAsync(TestDb db, TestNote note)
        => (await db.Store.GetLabelsOfAsync(note)).Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static bool HasPendingLabelOrLink(DbContext context)
        => context.ChangeTracker.Entries()
            .Any(e => e.State != EntityState.Unchanged && (e.Entity is Label || e.Entity is LabelLink<TestNote, Guid>));

    // ---- 基本語意 ----

    [Fact]
    public async Task 多的撕掉_少的補上_留下的不動()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "留下", "撕掉");

        await db.Store.ReplaceLabelsAsync(note, new[] { "留下", "新的" });

        (await NamesAsync(db, note)).Should().Equal("新的", "留下");
    }

    [Fact]
    public async Task 保留的連結_AttachedAt不會被洗掉()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "留下", "撕掉");
        var long_ago = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var kept = await db.Context.Set<LabelLink<TestNote, Guid>>().Include(l => l.Label).SingleAsync(l => l.Label.Name == "留下");
        kept.AttachedAt = long_ago;
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        await db.Store.ReplaceLabelsAsync(note, new[] { "留下", "新的" });

        db.Context.ChangeTracker.Clear();
        var after = await db.Context.Set<LabelLink<TestNote, Guid>>().Include(l => l.Label).SingleAsync(l => l.Label.Name == "留下");
        after.AttachedAt.Should().Be(long_ago, "留下來的標籤不是這次才貼上的");
    }

    [Fact]
    public async Task 空集合_撕掉全部_但不刪標籤本身()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文", "急件");

        await db.Store.ReplaceLabelsAsync(note, Array.Empty<string>());

        (await NamesAsync(db, note)).Should().BeEmpty();
        (await db.Store.FindAsync("論文")).Should().NotBeNull("撕標不是刪標籤");
    }

    [Fact]
    public async Task 只有空白名稱_等於空集合()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        await db.Store.ReplaceLabelsAsync(note, new[] { "", "   " });

        (await NamesAsync(db, note)).Should().BeEmpty();
    }

    [Fact]
    public async Task 名稱會去前後空白_重複的只算一個()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        await db.Store.ReplaceLabelsAsync(note, new[] { " 論文 ", "論文", "論文  " });

        (await NamesAsync(db, note)).Should().Equal("論文");
    }

    [Fact]
    public async Task 不影響別的實體與別的型別()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync("這筆");
        var other = await db.AddNoteAsync("別筆");
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(note, "撕掉");
        await db.Store.AttachAsync(other, "撕掉");
        await db.Store.AttachAsync(todo, "撕掉");

        await db.Store.ReplaceLabelsAsync(note, Array.Empty<string>());

        (await NamesAsync(db, other)).Should().Equal("撕掉");
        (await db.Store.GetLabelsOfAsync(todo)).Should().ContainSingle().Which.Name.Should().Be("撕掉");
    }

    // ---- 主鍵型別 ----

    [Fact]
    public async Task int主鍵也能取代()
    {
        using var db = new TestDb();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(todo, "舊");

        await db.Store.ReplaceLabelsAsync(todo, new[] { "新" });

        (await db.Store.GetLabelsOfAsync(todo)).Should().ContainSingle().Which.Name.Should().Be("新");
    }

    [Fact]
    public async Task string主鍵也能取代()
    {
        using var db = new TestDb(r => r.Labelable<TestTag>(t => t.Name));
        var tag = new TestTag { Id = "abc", Name = "x" };
        db.Context.Set<TestTag>().Add(tag);
        await db.Context.SaveChangesAsync();
        await db.Store.AttachAsync(tag, "舊");

        await db.Store.ReplaceLabelsAsync(tag, new[] { "新" });

        (await db.Store.GetLabelsOfAsync(tag)).Should().ContainSingle().Which.Name.Should().Be("新");
    }

    // ---- 存檔次數 ----

    [Fact]
    public async Task 連結的增減只存一次_要自動建立的標籤各自先存()
    {
        var saves = new SaveCounter();
        using var db = new TestDb(null, saves);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "撕掉", "留下");
        await db.Store.CreateAsync("既有");

        saves.Count = 0;
        await db.Store.ReplaceLabelsAsync(note, new[] { "留下", "既有" });
        saves.Count.Should().Be(1, "同時有撕掉與補上,也只有一次存檔");

        saves.Count = 0;
        await db.Store.ReplaceLabelsAsync(note, new[] { "留下", "新一", "新二" });
        saves.Count.Should().Be(3, "兩個自動建立的標籤各存一次,連結再一起存一次");
    }

    [Fact]
    public async Task 集合沒有變化_不存檔_消費端的未存變更不會被帶出去()
    {
        var saves = new SaveCounter();
        using var db = new TestDb(null, saves);
        var note = await db.AddNoteAsync("原標題");
        await db.Store.AttachAsync(note, "論文", "急件");
        note.Title = "還沒存的標題";

        saves.Count = 0;
        await db.Store.ReplaceLabelsAsync(note, new[] { "急件", "論文" });

        saves.Count.Should().Be(0);
        db.Context.Entry(note).State.Should().Be(EntityState.Modified, "消費端的修改留給他自己存");
        using var other = db.CreateSecondContext();
        (await other.Notes.AsNoTracking().SingleAsync(n => n.Id == note.Id)).Title.Should().Be("原標題");
    }

    // ---- 失敗不留痕跡 ----

    [Fact]
    public async Task 關掉自動建立時有缺的標籤_拋例外且連結不動()
    {
        var saves = new SaveCounter();
        using var db = new TestDb(r => { r.Labelable<TestNote>(n => n.Title); r.AutoCreateLabels = false; }, saves);
        var note = await db.AddNoteAsync();
        await db.Store.CreateAsync("既有");
        await db.Store.AttachAsync(note, "既有");

        saves.Count = 0;
        var act = () => db.Store.ReplaceLabelsAsync(note, new[] { "沒有一", "沒有二" });

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("沒有一").And.Contain("沒有二");
        saves.Count.Should().Be(0);
        (await NamesAsync(db, note)).Should().Equal("既有");
    }

    [Fact]
    public async Task 名稱太長_拋例外且什麼都沒寫()
    {
        var saves = new SaveCounter();
        using var db = new TestDb(null, saves);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "既有");

        saves.Count = 0;
        var act = () => db.Store.ReplaceLabelsAsync(note, new[] { "新的", new string('x', Label.MaxNameLength + 1) });

        await act.Should().ThrowAsync<ArgumentException>();
        saves.Count.Should().Be(0);
        (await db.Store.FindAsync("新的")).Should().BeNull("驗證要在建立任何標籤之前");
        (await NamesAsync(db, note)).Should().Equal("既有");
    }

    [Fact]
    public async Task 存檔失敗_追蹤器還原_連結不動_之後可以重試()
    {
        var failure = new FailOnce(c => c.ChangeTracker.Entries<LabelLink<TestNote, Guid>>().Any(e => e.State == EntityState.Deleted));
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "撕掉");
        await db.Store.CreateAsync("補上");

        var act = () => db.Store.ReplaceLabelsAsync(note, new[] { "補上" });
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure.Thrown);

        HasPendingLabelOrLink(db.Context).Should().BeFalse("失敗時要撤掉 store 自己放進追蹤器的變更");
        (await NamesAsync(db, note)).Should().Equal("撕掉");

        await db.Store.ReplaceLabelsAsync(note, new[] { "補上" });
        (await NamesAsync(db, note)).Should().Equal("補上");
    }

    [Fact]
    public async Task 資料庫拒絕其中一筆_撕掉的也要回滾()
    {
        // 上一個測試的失敗發生在送進資料庫之前;這裡用 trigger 讓資料庫真的拒絕,驗證增減在同一個交易裡
        using var db = new CaseInsensitiveDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "撕掉");
        await db.Store.CreateAsync("補上");
        await db.Context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER block_link BEFORE INSERT ON LabelLink_TestNote BEGIN SELECT RAISE(ABORT, 'blocked'); END;");

        var act = () => db.Store.ReplaceLabelsAsync(note, new[] { "補上" });
        await act.Should().ThrowAsync<DbUpdateException>();

        db.Context.ChangeTracker.Entries<LabelLink<TestNote, Guid>>()
            .Should().OnlyContain(e => e.State == EntityState.Unchanged, "追蹤器也要還原");
        db.Context.ChangeTracker.Clear();
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("撕掉");
    }

    [Fact]
    public async Task 實體不存在_撞外鍵_追蹤器還原()
    {
        using var db = new TestDb();
        var ghost = new TestNote { Id = Guid.NewGuid(), Title = "沒存過" };
        await db.Store.CreateAsync("論文");

        var act = () => db.Store.ReplaceLabelsAsync(ghost, new[] { "論文" });

        await act.Should().ThrowAsync<DbUpdateException>();
        HasPendingLabelOrLink(db.Context).Should().BeFalse();
    }

    [Fact]
    public async Task 已取消的token_拋例外且連結不動()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "既有");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => db.Store.ReplaceLabelsAsync(note, new[] { "新的" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await NamesAsync(db, note)).Should().Equal("既有");
    }

    // ---- 參數 ----

    [Fact]
    public async Task 參數是null_或型別沒註冊_拋清楚的例外()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        var nullEntity = () => db.Store.ReplaceLabelsAsync<TestNote>(null!, new[] { "x" });
        (await nullEntity.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("entity");

        var nullNames = () => db.Store.ReplaceLabelsAsync(note, null!);
        (await nullNames.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("labelNames");

        var orphan = () => db.Store.ReplaceLabelsAsync(new TestOrphan(), new[] { "x" });
        await orphan.Should().ThrowAsync<InvalidOperationException>();

        var nullStore = () => ((ILabelStore)null!).ReplaceLabelsAsync(note, new[] { "x" });
        (await nullStore.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("store");
    }

    // ---- 追蹤器裡還沒存的連結 ----

    [Fact]
    public async Task 消費端還沒存的新連結_不在清單裡就撤掉()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var pending = await db.Store.CreateAsync("還沒存");
        await db.Store.CreateAsync("論文");
        db.Context.Set<LabelLink<TestNote, Guid>>().Add(new LabelLink<TestNote, Guid>
        {
            LabelId = pending.Id,
            EntityId = note.Id,
            AttachedAt = DateTimeOffset.UtcNow,
        });

        await db.Store.ReplaceLabelsAsync(note, new[] { "論文" });

        (await NamesAsync(db, note)).Should().Equal("論文");
    }

    [Fact]
    public async Task 消費端還沒存的刪除_在清單裡就留下()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        var links = db.Context.Set<LabelLink<TestNote, Guid>>();
        links.Remove(await links.SingleAsync());

        await db.Store.ReplaceLabelsAsync(note, new[] { "論文" });

        (await NamesAsync(db, note)).Should().Equal(new[] { "論文" }, "呼叫之後實體就該剛好貼著這幾個");
    }

    [Fact]
    public async Task 消費端關掉自動偵測變更_照常運作()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "撕掉");
        db.Context.ChangeTracker.AutoDetectChangesEnabled = false;

        await db.Store.ReplaceLabelsAsync(note, new[] { "補上" });

        (await NamesAsync(db, note)).Should().Equal("補上");
    }

    // ---- 名稱比對跟著資料庫 ----

    [Fact]
    public async Task 不分大小寫的資料庫_只差大小寫的是同一個標籤_連結不重建()
    {
        using var db = new CaseInsensitiveDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "Paper");
        var before = await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().SingleAsync();

        await db.Store.ReplaceLabelsAsync(note, new[] { "PAPER", "paper" });

        var after = await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().SingleAsync();
        after.LabelId.Should().Be(before.LabelId);
        after.AttachedAt.Should().Be(before.AttachedAt);
        (await db.Store.GetAllAsync()).Should().ContainSingle().Which.Name.Should().Be("Paper");
    }

    // ---- 凍結的介面 ----

    [Fact]
    public async Task 其他ILabelStore實作_呼叫時拋NotSupported_不會默默做一半()
    {
        var other = DispatchProxy.Create<ILabelStore, UnimplementedStore>();

        var act = () => other.ReplaceLabelsAsync(new TestNote(), new[] { "x" });

        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage($"*{nameof(ILabelSetReplacer)}*");
    }

    [Fact]
    public void ILabelStore本身沒有這個成員_才不會弄壞其他實作()
    {
        typeof(ILabelStore).GetMethods().Should().NotContain(m => m.Name == nameof(ILabelSetReplacer.ReplaceLabelsAsync));
    }

    public class UnimplementedStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotImplementedException();
    }

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

    private sealed class FailOnce : SaveChangesInterceptor
    {
        private readonly Func<DbContext, bool> _when;

        public FailOnce(Func<DbContext, bool> when) => _when = when;

        public Exception? Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Thrown is null && _when(eventData.Context!))
            {
                Thrown = new DbUpdateException("模擬的寫入失敗");
                throw Thrown;
            }
            return ValueTask.FromResult(result);
        }
    }
}
