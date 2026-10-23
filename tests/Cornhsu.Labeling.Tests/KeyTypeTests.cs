using System.ComponentModel.DataAnnotations;
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

/// <summary>主鍵不叫 Id:真正的主鍵是 Code,<see cref="ILabelable{TKey}.Id"/> 以明確介面實作接到它(EF 不對應這個成員)。</summary>
public class TestSku : ILabelable<string>
{
    [Key]
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    string ILabelable<string>.Id => Code;
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

    [Fact]
    public async Task All給超過八個名稱_非int主鍵與主鍵不叫Id的實體_只回傳每個名稱都有的()
    {
        // 名稱超過 MaxAllGroupsInSql 時,符合的實體在記憶體算好,再以「連結外鍵指向的主鍵」篩實體;
        // Guid(TestNote)與 int 由 MultiLabelQueryTests 涵蓋,這裡補 string、long 與主鍵不叫 Id 的實體
        using var db = new TestDb(r => { Register(r); r.Labelable<TestSku>(s => s.Name); });
        var names = Enumerable.Range(0, 9).Select(i => $"k{i}").ToList();
        var tagFull = new TestTag { Id = "tag-full", Name = "全" };
        var tagPartial = new TestTag { Id = "tag-partial", Name = "缺" };
        var eventFull = new TestEvent { Id = 5_000_000_000L, Title = "全" };
        var eventPartial = new TestEvent { Id = 5_000_000_001L, Title = "缺" };
        var skuFull = new TestSku { Code = "SKU-1", Name = "全" };
        var skuPartial = new TestSku { Code = "SKU-2", Name = "缺" };
        db.Context.AddRange(tagFull, tagPartial, eventFull, eventPartial, skuFull, skuPartial);
        await db.Context.SaveChangesAsync();
        await db.Store.AttachAsync(tagFull, names);
        await db.Store.AttachAsync(tagPartial, names.Skip(1));
        await db.Store.AttachAsync(eventFull, names);
        await db.Store.AttachAsync(eventPartial, names.Skip(1));
        await db.Store.AttachAsync(skuFull, names);
        await db.Store.AttachAsync(skuPartial, names.Skip(1));

        (await (await db.Store.QueryByLabelsAsync<TestTag>(names, LabelMatch.All)).Select(t => t.Id).ToListAsync())
            .Should().Equal("tag-full");
        (await (await db.Store.QueryByLabelsAsync<TestEvent>(names, LabelMatch.All)).Select(e => e.Id).ToListAsync())
            .Should().Equal(5_000_000_000L);
        (await (await db.Store.QueryByLabelsAsync<TestSku>(names, LabelMatch.All)).Select(s => s.Code).ToListAsync())
            .Should().Equal("SKU-1");

        var hits = await db.Store.FindByLabelsAsync(names, LabelMatch.All);
        hits.Select(h => (h.EntityClrType, h.EntityId, h.DisplayName)).Should().BeEquivalentTo(new (Type, object, string?)[]
        {
            (typeof(TestTag), "tag-full", "全"),
            (typeof(TestEvent), 5_000_000_000L, "全"),
            (typeof(TestSku), "SKU-1", "全"),
        });
        (await db.Store.FindByLabelsAsync(names.Skip(1), LabelMatch.All)).Should().HaveCount(6, "少一個名稱時缺的那些也符合");
    }
}
