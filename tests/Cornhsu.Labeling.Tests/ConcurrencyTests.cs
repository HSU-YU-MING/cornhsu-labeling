using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>ConcurrencyStamp:同一個標籤被兩邊同時修改時,後存檔的一方要得到例外,不能默默後蓋前。</summary>
public class ConcurrencyTests
{
    [Fact]
    public async Task 兩邊同時改同一個標籤_後存檔的一方拋出並發例外()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("治療", "#CDDC39");

        // 模擬第二個使用端:同一個資料庫、獨立的 context/store,
        // 並且「先讀進來」(此時拿到的是舊戳記)——這就是真實世界裡開著編輯視窗的狀態
        using var other = db.CreateSecondContext();
        var otherStore = Cornhsu.Labeling.EntityFrameworkCore.LabelStoreFactory.Create(other, db.Registry);
        var stale = await other.Set<Label>().FirstAsync(l => l.Id == label.Id);

        // 一號先改成功 → 資料庫裡的戳記已輪換
        await db.Store.UpdateAsync(label.Id, l => l.Color = "#FF0000");

        // 二號基於舊戳記存檔必須失敗,不能默默蓋掉一號的變更
        var act = () => otherStore.UpdateAsync(label.Id, l => l.Color = "#00FF00");
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        // 一號的變更倖存
        (await db.Store.FindAsync("治療"))!.Color.Should().Be("#FF0000");
    }

    [Fact]
    public async Task 每次修改都輪換戳記()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("原名");
        var stamp0 = label.ConcurrencyStamp;

        await db.Store.RenameAsync(label.Id, "新名");
        var stamp1 = (await db.Store.FindAsync("新名"))!.ConcurrencyStamp;

        await db.Store.UpdateAsync(label.Id, l => l.SortOrder = 5);
        var stamp2 = (await db.Store.FindAsync("新名"))!.ConcurrencyStamp;

        stamp0.Should().NotBeEmpty();
        stamp1.Should().NotBe(stamp0);
        stamp2.Should().NotBe(stamp1);
    }

    [Fact]
    public async Task 貼標與撕標不動標籤本體_不輪換戳記()
    {
        using var db = new TestDb();
        var label = await db.Store.CreateAsync("論文");
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "論文");
        await db.Store.DetachAsync(note, "論文");

        (await db.Store.FindAsync("論文"))!.ConcurrencyStamp.Should().Be(label.ConcurrencyStamp);
    }

    [Fact]
    public async Task UpdateAsync的lambda經導覽屬性搬移別的標籤_那個標籤的戳記也輪換_拿舊戳記的一方撞並發例外()
    {
        // 搬移也是透過 store 的修改:不輪換的話,另一邊拿舊戳記搬同一個標籤會默默蓋掉這次的搬移
        using var db = new TestDb();
        var p = await db.Store.CreateAsync("p");
        var q = await db.Store.CreateAsync("q");
        var x = await db.Store.CreateAsync("x");
        var stamp = x.ConcurrencyStamp;

        using var other = db.CreateSecondContext();
        var otherStore = Cornhsu.Labeling.EntityFrameworkCore.LabelStoreFactory.Create(other, db.Registry);
        await other.Set<Label>().FirstAsync(l => l.Id == x.Id);   // 先讀進來:舊戳記

        await db.Store.UpdateAsync(p.Id, l => l.Children.Add(x));   // 一號經 p 的 Children 把 x 搬到 p 底下
        x.ConcurrencyStamp.Should().NotBe(stamp);

        var act = () => otherStore.UpdateAsync(x.Id, l => l.ParentId = q.Id);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        using var check = db.CreateSecondContext();
        (await check.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == x.Id)).ParentId.Should().Be(p.Id);
    }

    [Fact]
    public async Task UpdateAsync的lambda搬移別的標籤時撞並發例外_那個標籤也重讀_重試就成功()
    {
        // 被搬的標籤呼叫前沒有未存修改,store 可以替消費端重讀;不重讀的話追蹤器留著舊戳記,重試永遠撞同一個衝突
        using var db = new TestDb();
        var p = await db.Store.CreateAsync("p");
        var x = await db.Store.CreateAsync("x");

        using (var other = db.CreateSecondContext())
        {
            var otherStore = Cornhsu.Labeling.EntityFrameworkCore.LabelStoreFactory.Create(other, db.Registry);
            await otherStore.UpdateAsync(x.Id, l => l.Color = "#000000");   // 別人先改了 x
        }

        var act = () => db.Store.UpdateAsync(p.Id, l => l.Children.Add(x));
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        x.Color.Should().Be("#000000", "衝突的標籤重讀成資料庫的值");

        await db.Store.UpdateAsync(p.Id, l => l.Children.Add(x));
        using var check = db.CreateSecondContext();
        var stored = await check.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == x.Id);
        (stored.ParentId, stored.Color).Should().Be((p.Id, "#000000"));
    }
}
