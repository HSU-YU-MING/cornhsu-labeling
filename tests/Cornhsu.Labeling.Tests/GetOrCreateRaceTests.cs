using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// CLAUDE.md 地雷:get-or-create 的競態靠「重讀驗證」,不是無腦吞例外。
/// 撞到 DbUpdateException 時 detach、重讀同名標籤:讀得到 → 採用既有那筆;讀不到 → 原例外照拋。
/// 以 SaveChanges 攔截器在 store 存檔的前一刻插手,讓競態每次都準時發生。
/// </summary>
public class GetOrCreateRaceTests
{
    [Fact]
    public async Task 自動建立時另一端剛建好同名標籤_採用既有那筆_不重複也不拋例外()
    {
        var rival = new RivalCreatesSameName("論文");
        using var db = new TestDb(null, rival);
        rival.CreateRivalContext = db.CreateSecondContext;
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "論文");

        rival.WinnerId.Should().NotBeNull("攔截器必須真的搶先建立,否則這條測不到競態");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().ToListAsync())
            .Should().ContainSingle().Which.Id.Should().Be(rival.WinnerId!.Value);
        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Id.Should().Be(rival.WinnerId!.Value);
    }

    [Fact]
    public async Task 批次貼標自動建立時撞上競態_同樣採用既有那筆()
    {
        var rival = new RivalCreatesSameName("急件");
        using var db = new TestDb(null, rival);
        rival.CreateRivalContext = db.CreateSecondContext;
        var a = await db.AddNoteAsync("a");
        var b = await db.AddNoteAsync("b");

        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "急件" });

        rival.WinnerId.Should().NotBeNull();
        var map = await db.Store.GetLabelsOfManyAsync(new[] { a, b });
        map[a].Should().ContainSingle().Which.Id.Should().Be(rival.WinnerId!.Value);
        map[b].Should().ContainSingle().Which.Id.Should().Be(rival.WinnerId!.Value);
    }

    [Fact]
    public async Task 存檔失敗但重讀不到同名標籤_不是競態_原例外照拋()
    {
        var failure = new FailLabelInsert("論文");
        using var db = new TestDb(null, failure);
        var note = await db.AddNoteAsync();

        var act = () => db.Store.AttachAsync(note, "論文");

        (await act.Should().ThrowAsync<DbUpdateException>())
            .Which.Should().BeSameAs(failure.Thrown, "要原樣重拋,不能吞掉也不能換一個例外");
        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().CountAsync()).Should().Be(0);
        (await other.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync()).Should().Be(0);
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
            if (!_fired && IsAddingLabel(eventData.Context!, _name))
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

    /// <summary>store 要新增名為 name 的標籤時,模擬一個與同名競態無關的寫入失敗。</summary>
    private sealed class FailLabelInsert : SaveChangesInterceptor
    {
        private readonly string _name;

        public FailLabelInsert(string name) => _name = name;

        public DbUpdateException? Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsAddingLabel(eventData.Context!, _name))
            {
                Thrown = new DbUpdateException("模擬的寫入失敗(不是同名競態)");
                throw Thrown;
            }
            return ValueTask.FromResult(result);
        }
    }

    private static bool IsAddingLabel(DbContext context, string name)
        => context.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Added && e.Entity.Name == name);
}
