using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// 寫入語意。CLAUDE.md 地雷:寫入方法直接 SaveChangesAsync 消費端的 DbContext,
/// 同一個 context 裡其他未存的變更會一起送出(README Limitations 明文,是共用 context 的固有代價)。
/// </summary>
public class WriteSemanticsTests
{
    /// <summary>在 store 的 context 裡放一筆只追蹤、沒存檔的 Note,回傳它的 Id。</summary>
    private static Guid AddPendingNote(TestDb db)
    {
        var pending = new TestNote { Id = Guid.NewGuid(), Title = "未存" };
        db.Context.Notes.Add(pending);
        return pending.Id;
    }

    private static async Task<bool> PersistedAsync(TestDb db, Guid noteId)
    {
        using var other = db.CreateSecondContext();
        return await other.Notes.AsNoTracking().AnyAsync(n => n.Id == noteId);
    }

    [Fact]
    public async Task 每個寫入方法_都會把同一個context裡其他未存的變更一起送出()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var other = await db.AddNoteAsync();

        var p1 = AddPendingNote(db);
        var label = await db.Store.CreateAsync("論文");
        (await PersistedAsync(db, p1)).Should().BeTrue("CreateAsync");

        var p2 = AddPendingNote(db);
        await db.Store.RenameAsync(label.Id, "研究");
        (await PersistedAsync(db, p2)).Should().BeTrue("RenameAsync");

        var p3 = AddPendingNote(db);
        await db.Store.UpdateAsync(label.Id, l => l.SortOrder = 1);
        (await PersistedAsync(db, p3)).Should().BeTrue("UpdateAsync");

        var p4 = AddPendingNote(db);
        await db.Store.AttachAsync(note, "研究");
        (await PersistedAsync(db, p4)).Should().BeTrue("AttachAsync");

        var p5 = AddPendingNote(db);
        await db.Store.AttachManyAsync(new[] { other }, new[] { "研究" });
        (await PersistedAsync(db, p5)).Should().BeTrue("AttachManyAsync");

        var p6 = AddPendingNote(db);
        await db.Store.DetachAsync(note, "研究");
        (await PersistedAsync(db, p6)).Should().BeTrue("DetachAsync");

        var temp = await db.Store.CreateAsync("暫時");
        var p7 = AddPendingNote(db);
        await db.Store.DeleteAsync(temp.Id);
        (await PersistedAsync(db, p7)).Should().BeTrue("DeleteAsync");
    }

    [Fact]
    public async Task 自動建立標籤的那次存檔_也會一起送出未存的變更()
    {
        var failLinks = new FailLinkInsert();
        using var db = new TestDb(null, failLinks);   // AutoCreateLabels 預設 true
        var note = await db.AddNoteAsync();
        var pending = AddPendingNote(db);

        // 攔截器讓「寫連結」那次存檔失敗:pending 若已落地,只可能是前面「自動建立標籤」那次存檔帶出去的
        var act = () => db.Store.AttachAsync(note, "自動");
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failLinks.Thrown);

        (await PersistedAsync(db, pending)).Should().BeTrue("get-or-create 的存檔就是消費端 context 的 SaveChangesAsync");
    }

    [Fact]
    public async Task AttachAsync_名稱清單中有不合法的名稱_一條連結都不貼()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.CreateAsync("既有");

        var act = () => db.Store.AttachAsync(note, "既有", "新的", new string('長', Label.MaxNameLength + 1));
        await act.Should().ThrowAsync<ArgumentException>();

        using var other = db.CreateSecondContext();
        (await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AttachAsync_要自動建立的名稱有一個超長_拋例外前什麼都不建立_也不送出未存的變更()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        var pending = AddPendingNote(db);

        // 合法的名稱排在前面:驗證若晚於寫入,它會先被建立並存檔,連帶送出 pending
        var act = () => db.Store.AttachAsync(note, "合法", new string('長', Label.MaxNameLength + 1));
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"label name is {Label.MaxNameLength + 1} characters, over the {Label.MaxNameLength} limit: '{new string('長', 16)}…'");

        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().CountAsync()).Should().Be(0);
        (await PersistedAsync(db, pending)).Should().BeFalse("參數驗證失敗時不該有任何存檔");
    }

    [Fact]
    public async Task AttachManyAsync_要自動建立的名稱有一個超長_拋例外前什麼都不建立()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        var act = () => db.Store.AttachManyAsync(new[] { note }, new[] { "合法", "也合法", new string('長', Label.MaxNameLength + 1) });
        await act.Should().ThrowAsync<ArgumentException>();

        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AttachAsync_停用自動建立時_超長的名稱照舊列在缺漏清單裡_不改拋長度例外()
    {
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>();
            r.AutoCreateLabels = false;
        });
        var note = await db.AddNoteAsync();
        var tooLong = new string('長', Label.MaxNameLength + 1);

        var act = () => db.Store.AttachAsync(note, "沒有", tooLong);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"label(s) '沒有', '{tooLong}' do not exist*");
    }

    [Fact]
    public async Task AttachAsync_實體不在資料庫裡_外鍵擋下()
    {
        using var db = new TestDb();
        var ghost = new TestNote { Id = Guid.NewGuid(), Title = "沒存過" };   // 不在資料庫,也不在追蹤器裡

        var act = () => db.Store.AttachAsync(ghost, "論文");

        await act.Should().ThrowAsync<DbUpdateException>("連結的 EntityId 有真外鍵,透過 store API 也一樣擋");
        using var other = db.CreateSecondContext();
        (await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DetachAsync_名稱不存在或沒貼過或空白_安靜略過_不動既有連結()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");
        await db.Store.CreateAsync("沒貼過");

        await db.Store.DetachAsync(note, "不存在");
        await db.Store.DetachAsync(note, "沒貼過");
        await db.Store.DetachAsync(note, "  ", "");
        await db.Store.DetachAsync(note);

        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Name.Should().Be("論文");
        (await db.Store.FindAsync("沒貼過")).Should().NotBeNull("撕標不刪標籤本體");
    }

    [Fact]
    public async Task AttachAsync_名稱清單為空或全空白_不建立標籤也不貼()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note);
        await db.Store.AttachAsync(note, " ", "");

        (await db.Store.GetAllAsync()).Should().BeEmpty();
        (await db.Store.GetLabelsOfAsync(note)).Should().BeEmpty();
    }

    /// <summary>context 裡有待新增的 Note 連結時讓存檔失敗(模擬「寫連結」那一步出錯)。</summary>
    private sealed class FailLinkInsert : SaveChangesInterceptor
    {
        public DbUpdateException? Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LabelLink<TestNote, Guid>>().Any(e => e.State == EntityState.Added))
            {
                Thrown = new DbUpdateException("模擬的連結寫入失敗");
                throw Thrown;
            }
            return ValueTask.FromResult(result);
        }
    }
}
