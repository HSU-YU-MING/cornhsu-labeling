using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>標籤階層:導覽屬性、多層子孫展開、名稱跨階層全域唯一。</summary>
public class HierarchyTests
{
    [Fact]
    public async Task Parent與Children導覽屬性對應ParentId()
    {
        using var db = new TestDb();
        var root = await db.Store.CreateAsync("論文");
        var child = await db.Store.CreateAsync("文獻回顧", parentId: root.Id);
        var grandchild = await db.Store.CreateAsync("摘要筆記", parentId: child.Id);
        await db.Store.CreateAsync("無關");

        using var other = db.CreateSecondContext();
        var loadedRoot = await other.Set<Label>()
            .Include(l => l.Children).ThenInclude(c => c.Children)
            .SingleAsync(l => l.Id == root.Id);
        var loadedGrandchild = await other.Set<Label>()
            .Include(l => l.Parent)
            .SingleAsync(l => l.Id == grandchild.Id);

        loadedRoot.Parent.Should().BeNull();
        loadedRoot.Children.Should().ContainSingle().Which.Id.Should().Be(child.Id);
        loadedRoot.Children.Single().Children.Should().ContainSingle().Which.Id.Should().Be(grandchild.Id);
        loadedGrandchild.Parent!.Id.Should().Be(child.Id);
    }

    [Fact]
    public async Task 包含子孫時展開到孫層以下_且不含兄弟分支()
    {
        using var db = new TestDb();
        // 論文 ─┬─ 文獻回顧 ── 摘要筆記 ── 引用卡片
        //       └─ 方法
        // 研究(另一棵樹)
        var paper = await db.Store.CreateAsync("論文");
        var review = await db.Store.CreateAsync("文獻回顧", parentId: paper.Id);
        var summary = await db.Store.CreateAsync("摘要筆記", parentId: review.Id);
        await db.Store.CreateAsync("引用卡片", parentId: summary.Id);
        await db.Store.CreateAsync("方法", parentId: paper.Id);

        var deep = await db.AddNoteAsync("貼在第四層");
        var sibling = await db.AddNoteAsync("貼在兄弟分支");
        var unrelated = await db.AddNoteAsync("另一棵樹");
        await db.Store.AttachAsync(deep, "引用卡片");
        await db.Store.AttachAsync(sibling, "方法");
        await db.Store.AttachAsync(unrelated, "研究");

        (await db.Store.FindByLabelAsync("論文")).Select(h => h.DisplayName)
            .Should().BeEquivalentTo(new[] { "貼在第四層", "貼在兄弟分支" });
        (await db.Store.FindByLabelAsync("文獻回顧")).Select(h => h.DisplayName)
            .Should().BeEquivalentTo(new[] { "貼在第四層" }, "「方法」是文獻回顧的兄弟,不是子孫");

        var query = await db.Store.QueryByLabelAsync<TestNote>("文獻回顧");
        (await query.ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(deep.Id);
    }

    [Fact]
    public async Task 同一實體同時貼父標籤與子標籤_包含子孫的查詢只出現一次()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);
        var note = await db.AddNoteAsync();
        await db.Store.AttachAsync(note, "論文", "文獻回顧");

        (await db.Store.FindByLabelAsync("論文")).Should().ContainSingle();
        (await db.Store.FindByLabelsAsync(new[] { "論文", "文獻回顧" }, LabelMatch.Any)).Should().ContainSingle();
        (await db.Store.FindByLabelsAsync(new[] { "論文", "文獻回顧" }, LabelMatch.All)).Should().ContainSingle();
        (await (await db.Store.QueryByLabelAsync<TestNote>("論文")).ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task QueryByLabelAsync_includeDescendants為false_只回直接貼該標籤的實體_預設包含子孫()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);
        var direct = await db.AddNoteAsync("直接貼");
        var viaChild = await db.AddNoteAsync("貼子標籤");
        await db.Store.AttachAsync(direct, "論文");
        await db.Store.AttachAsync(viaChild, "文獻回顧");

        var without = await (await db.Store.QueryByLabelAsync<TestNote>("論文", includeDescendants: false)).ToListAsync();
        var byDefault = await (await db.Store.QueryByLabelAsync<TestNote>("論文")).ToListAsync();

        without.Should().ContainSingle().Which.Id.Should().Be(direct.Id);
        byDefault.Select(n => n.Id).Should().BeEquivalentTo(new[] { direct.Id, viaChild.Id });
    }

    [Fact]
    public async Task QueryByLabelsAsync_All_包含子孫與否()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);
        var viaChild = await db.AddNoteAsync("子標籤+急件");
        var direct = await db.AddNoteAsync("論文+急件");
        var paperOnly = await db.AddNoteAsync("只有論文");
        await db.Store.AttachAsync(viaChild, "文獻回顧", "急件");
        await db.Store.AttachAsync(direct, "論文", "急件");
        await db.Store.AttachAsync(paperOnly, "論文");

        var names = new[] { "論文", "急件" };
        var byDefault = await (await db.Store.QueryByLabelsAsync<TestNote>(names, LabelMatch.All)).ToListAsync();
        var without = await (await db.Store.QueryByLabelsAsync<TestNote>(names, LabelMatch.All, includeDescendants: false)).ToListAsync();

        byDefault.Select(n => n.Id).Should().BeEquivalentTo(new[] { viaChild.Id, direct.Id });
        without.Should().ContainSingle().Which.Id.Should().Be(direct.Id);
    }

    /// <summary>
    /// 繞過 store 直接改資料庫,讓兩個標籤互為父子(store 的循環檢查管不到這條路;
    /// 資料庫的外鍵也不擋環)。用來驗證走訪階層的程式碼遇到異常資料會結束,不會無限繞圈。
    /// </summary>
    private static async Task CorruptParentAsync(TestDb db, Guid labelId, Guid parentId)
    {
        using var other = db.CreateSecondContext();
        var label = await other.Set<Label>().SingleAsync(l => l.Id == labelId);
        label.ParentId = parentId;
        await other.SaveChangesAsync();
    }

    /// <summary>放到背景執行緒並設上限:走訪若陷入無限迴圈,測試是紅燈而不是整個測試回合卡住。</summary>
    private static Task<T> WithinTimeLimit<T>(Func<Task<T>> action)
        => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task 資料裡的階層已經成環_包含子孫的查詢仍會結束_每個實體只出現一次()
    {
        using var db = new TestDb();
        var paper = await db.Store.CreateAsync("論文");
        var review = await db.Store.CreateAsync("文獻回顧", parentId: paper.Id);
        await db.Store.AttachAsync(await db.AddNoteAsync("貼論文"), "論文");
        await db.Store.AttachAsync(await db.AddNoteAsync("貼文獻回顧"), "文獻回顧");
        await CorruptParentAsync(db, paper.Id, review.Id);   // 論文 ⇄ 文獻回顧

        var hits = await WithinTimeLimit(() => db.Store.FindByLabelAsync("論文"));

        hits.Select(h => h.DisplayName).Should().BeEquivalentTo(new[] { "貼論文", "貼文獻回顧" });
    }

    [Fact]
    public async Task 資料裡的階層已經成環_把環外的標籤搬到環上_循環檢查仍會結束()
    {
        using var db = new TestDb();
        var paper = await db.Store.CreateAsync("論文");
        var review = await db.Store.CreateAsync("文獻回顧", parentId: paper.Id);
        var outsider = await db.Store.CreateAsync("方法");
        await CorruptParentAsync(db, paper.Id, review.Id);   // 論文 ⇄ 文獻回顧

        // 沿祖先鏈往上走永遠碰不到「方法」自己:要靠步數上限跳出,讓這次與環無關的搬移照常完成
        await WithinTimeLimit(() => db.Store.UpdateAsync(outsider.Id, l => l.ParentId = paper.Id));

        using var other = db.CreateSecondContext();
        (await other.Set<Label>().AsNoTracking().SingleAsync(l => l.Id == outsider.Id)).ParentId.Should().Be(paper.Id);
    }

    [Fact]
    public async Task 名稱全域唯一_不同父標籤底下也不能同名()
    {
        using var db = new TestDb();
        var work = await db.Store.CreateAsync("工作");
        var life = await db.Store.CreateAsync("生活");
        await db.Store.CreateAsync("雜項", parentId: work.Id);
        var other = await db.Store.CreateAsync("其他", parentId: life.Id);

        var create = () => db.Store.CreateAsync("雜項", parentId: life.Id);
        await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'雜項' already exists*");

        var rename = () => db.Store.RenameAsync(other.Id, "雜項");
        await rename.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'雜項' is already taken*");

        var update = () => db.Store.UpdateAsync(other.Id, l => l.Name = "雜項");
        await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'雜項' is already taken*");
    }
}
