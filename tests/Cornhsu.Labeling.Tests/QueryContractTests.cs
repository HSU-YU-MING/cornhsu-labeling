using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>查詢的邊界契約:名稱解析為空、未註冊型別、LabelHit 的內容、IQueryable 可續接。</summary>
public class QueryContractTests
{
    [Fact]
    public async Task FindByLabelAsync_名稱為null或空白或不存在_回傳空清單不拋例外()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        (await db.Store.FindByLabelAsync(null!)).Should().BeEmpty();
        (await db.Store.FindByLabelAsync("   ")).Should().BeEmpty();
        (await db.Store.FindByLabelAsync("不存在")).Should().BeEmpty();
    }

    [Fact]
    public async Task QueryByLabelAsync_名稱解析為空_回傳的查詢沒有任何實體()
    {
        using var db = new TestDb();
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文");

        // 這裡刻意用同步列舉:目前名稱解析為空時回傳的不是 EF 支撐的查詢,
        // 對它呼叫 ToListAsync / CountAsync 會拋例外(另案回報)。這條只釘「結果是空的」,
        // 不管將來改成哪一種空查詢都應該成立。
        (await db.Store.QueryByLabelAsync<TestNote>("不存在")).ToList().Should().BeEmpty();
        (await db.Store.QueryByLabelsAsync<TestNote>(new[] { "論文", "不存在" }, LabelMatch.All)).ToList().Should().BeEmpty();
        (await db.Store.QueryByLabelsAsync<TestNote>(new[] { "不存在", "也不存在" }, LabelMatch.Any)).ToList().Should().BeEmpty();
        (await db.Store.QueryByLabelsAsync<TestNote>(new[] { "  ", "" })).ToList().Should().BeEmpty();
    }

    [Fact]
    public async Task 未註冊型別_各個泛型方法都先拋清楚的例外_不會因名稱為空而安靜返回()
    {
        using var db = new TestDb();
        var orphan = new TestOrphan { Id = Guid.NewGuid() };
        const string Expected = "type TestOrphan is not registered. Call r.Labelable<TestOrphan>() inside AddLabeling.";

        var detach = () => db.Store.DetachAsync(orphan, "不存在");
        await detach.Should().ThrowAsync<InvalidOperationException>().WithMessage(Expected);

        var getLabels = () => db.Store.GetLabelsOfAsync(orphan);
        await getLabels.Should().ThrowAsync<InvalidOperationException>().WithMessage(Expected);

        var query = () => db.Store.QueryByLabelAsync<TestOrphan>("不存在");
        await query.Should().ThrowAsync<InvalidOperationException>().WithMessage(Expected);

        var queryMany = () => db.Store.QueryByLabelsAsync<TestOrphan>(Array.Empty<string>());
        await queryMany.Should().ThrowAsync<InvalidOperationException>().WithMessage(Expected);

        var attachMany = () => db.Store.AttachManyAsync(Array.Empty<TestOrphan>(), new[] { "論文" });
        await attachMany.Should().ThrowAsync<InvalidOperationException>().WithMessage(Expected);

        (await db.Store.GetAllAsync()).Should().BeEmpty("未註冊的型別不該觸發自動建立標籤");
    }

    [Fact]
    public async Task LabelHit_帶出實體的CLR型別與TypeKey_沒給投影時DisplayName為null()
    {
        using var db = new TestDb(r =>
        {
            r.Labelable<TestNote>(n => n.Title, typeKey: "Memo");
            r.Labelable<TestTodo>();   // 刻意不給 displayName 投影
        });
        var note = await db.AddNoteAsync("讀論文");
        var todo = await db.AddTodoAsync("整理文獻");
        await db.Store.AttachAsync(note, "論文");
        await db.Store.AttachAsync(todo, "論文");

        var hits = await db.Store.FindByLabelAsync("論文");

        var noteHit = hits.Single(h => h.EntityClrType == typeof(TestNote));
        noteHit.EntityTypeKey.Should().Be("Memo", "明確指定的 typeKey 就是 EntityTypeKey");
        noteHit.EntityId.Should().Be(note.Id);
        noteHit.DisplayName.Should().Be("讀論文");

        var todoHit = hits.Single(h => h.EntityClrType == typeof(TestTodo));
        todoHit.EntityTypeKey.Should().Be("TestTodo");
        todoHit.EntityIdAs<int>().Should().Be(todo.Id);
        todoHit.DisplayName.Should().BeNull("註冊時沒給投影");
    }

    [Fact]
    public async Task EntityIdAs_不做數值轉換_int主鍵取long拋InvalidCastException()
    {
        using var db = new TestDb();
        var todo = await db.AddTodoAsync();
        await db.Store.AttachAsync(todo, "論文");

        var hit = (await db.Store.FindByLabelAsync("論文")).Single();

        hit.EntityId.Should().BeOfType<int>();
        var act = () => hit.EntityIdAs<long>();
        act.Should().Throw<InvalidCastException>().WithMessage("the key of this hit (TestTodo) is Int32, not Int64.");
    }

    [Fact]
    public void LabelHit_是值相等的record_可解構()
    {
        var id = Guid.NewGuid();
        var a = new LabelHit(typeof(TestNote), "TestNote", id, "標題");
        var b = new LabelHit(typeof(TestNote), "TestNote", id, "標題");

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a with { DisplayName = "別的" }).Should().NotBe(a);

        var (clrType, typeKey, entityId, displayName) = a;
        clrType.Should().Be(typeof(TestNote));
        typeKey.Should().Be("TestNote");
        entityId.Should().Be(id);
        displayName.Should().Be("標題");
    }

    [Fact]
    public async Task QueryByLabelAsync_回傳的IQueryable可續接排序分頁與計數()
    {
        using var db = new TestDb();
        foreach (var title in new[] { "c", "a", "b" })
            await db.Store.AttachAsync(await db.AddNoteAsync(title), "論文");
        await db.Store.AttachAsync(await db.AddNoteAsync("沒貼論文"), "其他");

        var query = await db.Store.QueryByLabelAsync<TestNote>("論文");

        (await query.CountAsync()).Should().Be(3);
        (await query.OrderBy(n => n.Title).Skip(1).Take(1).Select(n => n.Title).ToListAsync())
            .Should().Equal("b");
    }
}
