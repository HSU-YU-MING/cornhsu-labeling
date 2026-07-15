using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>§9.2 #11–#13:註冊行為與表名設定。</summary>
public class RegistrationTests
{
    [Fact] // #11
    public async Task 對未註冊型別呼叫AttachAsync_拋出訊息清楚的例外()
    {
        using var db = new TestDb();
        var orphan = new TestOrphan { Id = Guid.NewGuid() };

        var act = () => db.Store.AttachAsync(orphan, "論文");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*TestOrphan*未註冊*Labelable<TestOrphan>*");
    }

    [Fact] // #12
    public void 重複註冊同一個TypeKey_拋例外()
    {
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>();

        var act = () => registry.Labelable<TestTodo>(typeKey: "TestNote");

        act.Should().Throw<InvalidOperationException>().WithMessage("*TestNote*已被註冊*");
    }

    [Fact]
    public void 封存後再註冊_拋例外()
    {
        using var db = new TestDb();   // AddLabeling 內部已 Seal()

        var act = () => db.Registry.Labelable<TestOrphan>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*已封存*");
    }

    [Fact] // #13
    public void 自訂LinkTablePrefix後_表名正確反映()
    {
        using var db = new TestDb(r =>
        {
            r.LinkTablePrefix = "TL_";
            r.LabelTableName = "MyLabels";
            r.Labelable<TestNote>(typeKey: "Memo");
            r.Labelable<TestTodo>();
        });

        var model = db.Context.Model;
        model.FindEntityType(typeof(LabelLink<TestNote>))!.GetTableName().Should().Be("TL_Memo");
        model.FindEntityType(typeof(LabelLink<TestTodo>))!.GetTableName().Should().Be("TL_TestTodo");
        model.FindEntityType(typeof(Label))!.GetTableName().Should().Be("MyLabels");
    }
}
