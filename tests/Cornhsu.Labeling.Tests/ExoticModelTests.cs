using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>以影子外鍵參照標籤的實體。</summary>
public class ShadowRef
{
    public int Id { get; set; }
}

/// <summary>複合主鍵、以可為 null 的外鍵參照標籤的實體。</summary>
public class CompositeRef
{
    public string Code { get; set; } = "";
    public int Seq { get; set; }
    public Guid? LabelId { get; set; }
}

/// <summary>擁有一個 owned type,外鍵在 owned type 上。</summary>
public class Holder
{
    public Guid Id { get; set; }
    public HolderMeta Meta { get; set; } = new();
}

public class HolderMeta
{
    public Guid? LabelId { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>參照標籤的各種「不尋常」形狀:影子外鍵、複合主鍵、owned type;再加 string 與 int 主鍵的可標記型別。</summary>
public class ExoticDbContext : DbContext
{
    private readonly LabelRegistry _registry;

    public ExoticDbContext(DbContextOptions<ExoticDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyLabelModel(_registry);
        b.Entity<ShadowRef>(e =>
        {
            e.Property<Guid?>("LabelId");
            e.HasOne<Label>().WithMany().HasForeignKey("LabelId").OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<CompositeRef>(e =>
        {
            e.HasKey(x => new { x.Code, x.Seq });
            e.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Holder>().OwnsOne(h => h.Meta, m =>
            m.HasOne<Label>().WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.SetNull));
    }
}

/// <summary>
/// DeleteAsync 的還原與「已提交就不還原」碰到不尋常的模型形狀時也要成立
/// (第二輪審查的探針 P4 / P4b)。
/// </summary>
public class ExoticModelTests
{
    private sealed record Fixture(
        CustomDb<ExoticDbContext> Db, Label Label, ShadowRef Shadow, CompositeRef Composite, Holder Holder) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private static async Task<Fixture> CreateAsync(params IInterceptor[] interceptors)
    {
        var registry = new LabelRegistry().Labelable<TestTag>().Labelable<TestTodo>(t => t.Content);
        var db = new CustomDb<ExoticDbContext>(registry, (o, r) => new ExoticDbContext(o, r), interceptors);
        var label = await db.Store.CreateAsync("x");
        var shadow = new ShadowRef();
        var composite = new CompositeRef { Code = "k", Seq = 1, LabelId = label.Id };
        var holder = new Holder { Id = Guid.NewGuid(), Meta = new HolderMeta { LabelId = label.Id, Note = "n" } };
        var tag = new TestTag { Id = "tag-1" };
        var todo = new TestTodo { Content = "t" };
        db.Context.AddRange(shadow, composite, holder, tag, todo);
        db.Context.Entry(shadow).Property("LabelId").CurrentValue = label.Id;
        await db.Context.SaveChangesAsync();
        await db.Store.AttachAsync(tag, "x");
        await db.Store.AttachAsync(todo, "x");
        return new Fixture(db, label, shadow, composite, holder);
    }

    [Fact]
    public async Task DeleteAsync存檔前失敗_影子外鍵_owned_複合主鍵_string與int主鍵的連結_全部還原_重試成功()
    {
        using var x = await CreateAsync(new FailWhenDeletingLabel());

        var act = () => x.Db.Store.DeleteAsync(x.Label.Id);
        await act.Should().ThrowAsync<DbUpdateException>();

        x.Db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        x.Db.Context.Entry(x.Shadow).Property("LabelId").CurrentValue.Should().Be(x.Label.Id);
        x.Composite.LabelId.Should().Be(x.Label.Id);
        x.Holder.Meta.LabelId.Should().Be(x.Label.Id);
        await x.Db.Store.DeleteAsync(x.Label.Id);
        x.Composite.LabelId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync提交後才拋例外_影子外鍵_owned_複合主鍵_追蹤器與資料庫一致()
    {
        var after = new ThrowAfterSave();
        using var x = await CreateAsync(after);

        after.Armed = true;
        var act = () => x.Db.Store.DeleteAsync(x.Label.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();

        x.Db.Context.ChangeTracker.HasChanges().Should().BeFalse();
        x.Db.Context.Entry(x.Label).State.Should().Be(EntityState.Detached);
        x.Db.Context.Entry(x.Shadow).Property("LabelId").CurrentValue.Should().BeNull();
        x.Composite.LabelId.Should().BeNull();
        x.Holder.Meta.LabelId.Should().BeNull();
        await x.Db.Context.SaveChangesAsync();
    }

    private sealed class FailWhenDeletingLabel : SaveChangesInterceptor
    {
        private bool _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired && eventData.Context!.ChangeTracker.Entries<Label>().Any(e => e.State == EntityState.Deleted))
            {
                _fired = true;
                throw new DbUpdateException("模擬的寫入失敗");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowAfterSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new InvalidOperationException("模擬提交之後才發生的錯誤");
            }
            return ValueTask.FromResult(result);
        }
    }
}
