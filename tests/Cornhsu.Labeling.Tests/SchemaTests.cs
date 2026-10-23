using System.Data.Common;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// schema 契約:ApplyLabelModel 產生的表、欄、長度、索引、主鍵與外鍵刪除行為。
/// 這些一改,消費端就得產生 migration(屬 major),所以用 model metadata 釘住;
/// 另外兩條繞過 store 直接打資料庫,驗證約束真的落在資料庫裡,不只是 store 的事前檢查。
/// </summary>
public class SchemaTests
{
    [Fact]
    public void 名稱上限常數是64()
    {
        Label.MaxNameLength.Should().Be(64, "const 會被編譯進消費端,改值同時改 schema 欄寬");
    }

    [Fact]
    public void Label表_欄位與長度與必填與唯一索引與並發戳記()
    {
        using var db = new TestDb();
        var label = db.Context.Model.FindEntityType(typeof(Label))!;

        label.GetProperties().Select(p => p.GetColumnName()).Should().BeEquivalentTo(
            "Id", "Name", "Color", "Icon", "ParentId", "SortOrder", "CreatedAt", "ConcurrencyStamp");
        label.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal("Id");

        var name = label.FindProperty(nameof(Label.Name))!;
        name.GetMaxLength().Should().Be(Label.MaxNameLength);
        name.IsNullable.Should().BeFalse();
        label.GetIndexes().Should().ContainSingle(i => i.Properties.Count == 1 && i.Properties[0] == name)
            .Which.IsUnique.Should().BeTrue("名稱全域唯一,get-or-create 的競態處理也靠這個索引撞擊");

        var color = label.FindProperty(nameof(Label.Color))!;
        color.GetMaxLength().Should().Be(16);
        color.IsNullable.Should().BeTrue();
        var icon = label.FindProperty(nameof(Label.Icon))!;
        icon.GetMaxLength().Should().Be(128);
        icon.IsNullable.Should().BeTrue();

        var stamp = label.FindProperty(nameof(Label.ConcurrencyStamp))!;
        stamp.IsConcurrencyToken.Should().BeTrue();
        stamp.ValueGenerated.Should().Be(ValueGenerated.Never, "戳記由 store 輪換,不是資料庫產生的 rowversion");
    }

    [Fact]
    public void Label表_父標籤外鍵指向自己_刪除行為是Restrict()
    {
        using var db = new TestDb();
        var label = db.Context.Model.FindEntityType(typeof(Label))!;

        var fk = label.GetForeignKeys().Should().ContainSingle().Subject;
        fk.Properties.Select(p => p.Name).Should().Equal(nameof(Label.ParentId));
        fk.PrincipalEntityType.Should().BeSameAs(label);
        fk.IsRequired.Should().BeFalse("頂層標籤沒有父標籤");
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        fk.DependentToPrincipal!.Name.Should().Be(nameof(Label.Parent));
        fk.PrincipalToDependent!.Name.Should().Be(nameof(Label.Children));
    }

    [Theory]
    [InlineData(typeof(LabelLink<TestNote, Guid>), typeof(TestNote))]
    [InlineData(typeof(LabelLink<TestTodo, int>), typeof(TestTodo))]
    public void 連結表_複合主鍵_兩條cascade外鍵_EntityId索引(Type linkType, Type entityType)
    {
        using var db = new TestDb();
        var model = db.Context.Model;
        var link = model.FindEntityType(linkType)!;

        link.GetProperties().Select(p => p.GetColumnName()).Should().BeEquivalentTo("LabelId", "EntityId", "AttachedAt");
        link.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal("LabelId", "EntityId");

        var toLabel = link.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Label));
        toLabel.Properties.Select(p => p.Name).Should().Equal("LabelId");
        toLabel.DeleteBehavior.Should().Be(DeleteBehavior.Cascade, "刪標籤 → 連結消失");
        toLabel.PrincipalToDependent.Should().BeNull("Label 端沒有反向集合導覽");

        var toEntity = link.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == entityType);
        toEntity.Properties.Select(p => p.Name).Should().Equal("EntityId");
        toEntity.PrincipalKey.Should().BeSameAs(model.FindEntityType(entityType)!.FindPrimaryKey());
        toEntity.DeleteBehavior.Should().Be(DeleteBehavior.Cascade, "刪實體 → 連結消失,不留孤兒");

        link.GetForeignKeys().Should().HaveCount(2);
        link.GetIndexes().Should().Contain(i => i.Properties.Count == 1 && i.Properties[0].Name == "EntityId" && !i.IsUnique);
    }

    [Fact]
    public async Task 名稱唯一由資料庫索引強制_繞過store直接插同名也會被擋()
    {
        using var db = new TestDb();
        await db.Store.CreateAsync("論文");

        using var other = db.CreateSecondContext();
        other.Set<Label>().Add(new Label
        {
            Id = Guid.NewGuid(),
            Name = "論文",
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid(),
        });
        var act = () => other.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task 有子標籤的父標籤_繞過store直接刪_資料庫的Restrict外鍵擋下()
    {
        using var db = new TestDb();
        var parent = await db.Store.CreateAsync("論文");
        await db.Store.CreateAsync("文獻回顧", parentId: parent.Id);

        using var other = db.CreateSecondContext();
        var act = () => other.Set<Label>().Where(l => l.Id == parent.Id).ExecuteDeleteAsync();

        await act.Should().ThrowAsync<DbException>("DeleteAsync 的訊息承諾資料庫也會擋");
        (await other.Set<Label>().AsNoTracking().CountAsync()).Should().Be(2);
    }
}
