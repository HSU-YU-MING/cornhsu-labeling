using System.ComponentModel.DataAnnotations.Schema;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>string 主鍵的可標記型別。沒有 DbSet:EF 沿連結表的導覽屬性把它拉進 model。</summary>
public class TestTag : ILabelable<string>
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>long 主鍵的可標記型別。主鍵由程式指定(不靠資料庫發號),才能放超過 int 範圍的值。</summary>
public class TestEvent : ILabelable<long>
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long Id { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>README Limitations 承諾主鍵可以是 int/long/Guid/string 並混用;Guid 與 int 由其他測試涵蓋,這裡補 string 與 long。</summary>
public class KeyTypeTests
{
    private static void Register(LabelRegistry r)
    {
        r.Labelable<TestNote>(n => n.Title);
        r.Labelable<TestTag>(t => t.Name);
        r.Labelable<TestEvent>(e => e.Title);
    }

    [Fact]
    public async Task string主鍵_貼標撕標反查與查詢都可用()
    {
        using var db = new TestDb(Register);
        var a = new TestTag { Id = "tag-a", Name = "甲" };
        var b = new TestTag { Id = "tag-b", Name = "乙" };
        db.Context.Set<TestTag>().AddRange(a, b);
        await db.Context.SaveChangesAsync();
        var note = await db.AddNoteAsync("筆記");

        await db.Store.AttachAsync(a, "論文", "急件");
        await db.Store.AttachManyAsync(new[] { a, b }, new[] { "論文" });
        await db.Store.AttachAsync(note, "論文");
        await db.Store.DetachAsync(a, "急件");

        (await db.Store.GetLabelsOfAsync(a)).Select(l => l.Name).Should().Equal("論文");
        var map = await db.Store.GetLabelsOfManyAsync(new[] { a, b });
        map[a].Select(l => l.Name).Should().Equal("論文");
        map[b].Select(l => l.Name).Should().Equal("論文");

        var hits = await db.Store.FindByLabelAsync("論文");
        var tagHits = hits.Where(h => h.EntityClrType == typeof(TestTag)).ToList();
        tagHits.Select(h => h.EntityIdAs<string>()).Should().BeEquivalentTo(new[] { "tag-a", "tag-b" });
        tagHits.Single(h => h.EntityIdAs<string>() == "tag-a").DisplayName.Should().Be("甲");
        hits.Single(h => h.EntityClrType == typeof(TestNote)).EntityIdAs<Guid>().Should().Be(note.Id, "string 與 Guid 主鍵混在同一次查詢裡");

        var query = await db.Store.QueryByLabelAsync<TestTag>("論文");
        (await query.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { "tag-a", "tag-b" });
        db.Context.Model.FindEntityType(typeof(LabelLink<TestTag, string>))!.GetTableName().Should().Be("LabelLink_TestTag");
    }

    [Fact]
    public async Task string主鍵_刪除實體後連結由資料庫cascade消失()
    {
        using var db = new TestDb(Register);
        var tag = new TestTag { Id = "tag-a", Name = "甲" };
        db.Context.Set<TestTag>().Add(tag);
        await db.Context.SaveChangesAsync();
        await db.Store.AttachAsync(tag, "論文");

        await db.Context.Set<TestTag>().Where(t => t.Id == "tag-a").ExecuteDeleteAsync();

        (await db.Context.Set<LabelLink<TestTag, string>>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task long主鍵_超過int範圍的值不會被截斷()
    {
        using var db = new TestDb(Register);
        var big = new TestEvent { Id = 5_000_000_000L, Title = "大" };
        var small = new TestEvent { Id = 1L, Title = "小" };
        db.Context.Set<TestEvent>().AddRange(big, small);
        await db.Context.SaveChangesAsync();

        await db.Store.AttachAsync(big, "論文");
        await db.Store.AttachAsync(small, "急件");

        var hit = (await db.Store.FindByLabelAsync("論文")).Should().ContainSingle().Subject;
        hit.EntityClrType.Should().Be(typeof(TestEvent));
        hit.EntityId.Should().BeOfType<long>().Which.Should().Be(5_000_000_000L);
        hit.EntityIdAs<long>().Should().Be(5_000_000_000L);

        var query = await db.Store.QueryByLabelsAsync<TestEvent>(new[] { "論文", "急件" }, LabelMatch.Any);
        (await query.Select(e => e.Id).ToListAsync()).Should().BeEquivalentTo(new[] { 5_000_000_000L, 1L });
        (await db.Store.GetLabelsOfManyAsync(new[] { big, small }))[big].Should().ContainSingle().Which.Name.Should().Be("論文");

        await db.Store.DetachAsync(big, "論文");
        (await db.Store.GetLabelsOfAsync(big)).Should().BeEmpty();
        (await db.Store.GetLabelsOfAsync(small)).Should().ContainSingle().Which.Name.Should().Be("急件");
    }
}
