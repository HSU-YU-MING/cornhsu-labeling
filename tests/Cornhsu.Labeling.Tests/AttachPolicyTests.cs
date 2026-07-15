using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>AutoCreateLabels 政策:策展式標籤 App 停用自動建立後的行為。</summary>
public class AttachPolicyTests
{
    private static void CuratedRegistry(LabelRegistry r)
    {
        r.AutoCreateLabels = false;
        r.Labelable<TestNote>(n => n.Title);
        r.Labelable<TestTodo>(t => t.Content);
    }

    [Fact]
    public async Task 停用自動建立_貼既有標籤正常運作()
    {
        using var db = new TestDb(CuratedRegistry);
        await db.Store.CreateAsync("論文", color: "#2196F3", icon: "📄");
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "論文");

        (await db.Store.GetLabelsOfAsync(note)).Should().ContainSingle().Which.Icon.Should().Be("📄");
    }

    [Fact]
    public async Task 停用自動建立_貼不存在的標籤_拋出列出全部缺漏的例外()
    {
        using var db = new TestDb(CuratedRegistry);
        await db.Store.CreateAsync("論文");
        var note = await db.AddNoteAsync();

        var act = () => db.Store.AttachAsync(note, "論文", "急件", "沒這個");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*'急件'*'沒這個'*AutoCreateLabels*CreateAsync*");

        // 不留任何副作用:沒建立標籤、也沒貼上任何連結
        (await db.Store.GetAllAsync()).Should().ContainSingle().Which.Name.Should().Be("論文");
        (await db.Context.Set<LabelLink<TestNote, Guid>>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task 預設行為不變_仍會自動建立()
    {
        using var db = new TestDb();   // 預設 AutoCreateLabels = true
        var note = await db.AddNoteAsync();

        await db.Store.AttachAsync(note, "新標籤");

        (await db.Store.FindAsync("新標籤")).Should().NotBeNull();
    }
}
