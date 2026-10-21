using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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
            .WithMessage("type TestOrphan is not registered. Call r.Labelable<TestOrphan>() inside AddLabeling.");
        (await db.Store.GetAllAsync()).Should().BeEmpty("型別檢查要在自動建立標籤之前,否則會留下沒人用的標籤");
    }

    /// <summary>已註冊型別 TestNote 的衍生類別,本身沒有註冊。</summary>
    private sealed class SpecialNote : TestNote
    {
    }

    [Fact]
    public async Task 註冊了基底類別_衍生類別本身沒註冊_執行期一樣拋未註冊()
    {
        // 型別比對是精確比對,不沿繼承鏈往上找。analyzer 的 CHSU001 對衍生類別照報,靠的就是這一條
        // (見 AnalyzerTests「已註冊基底類別的衍生類別本身沒註冊_照報」);改成接受衍生類別,那條警告就變誤報。
        using var db = new TestDb();
        var special = new SpecialNote { Id = Guid.NewGuid() };

        var act = () => db.Store.AttachAsync(special, "論文");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("type SpecialNote is not registered. Call r.Labelable<SpecialNote>() inside AddLabeling.");
        (await db.Store.GetAllAsync()).Should().BeEmpty();
    }

    [Fact] // #12
    public void 重複註冊同一個TypeKey_拋例外()
    {
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>();

        var act = () => registry.Labelable<TestTodo>(typeKey: "TestNote");

        act.Should().Throw<InvalidOperationException>().WithMessage("TypeKey 'TestNote' is already registered.");
    }

    [Fact]
    public void 封存後再註冊_拋例外()
    {
        using var db = new TestDb();   // AddLabeling 內部已 Seal()

        var act = () => db.Registry.Labelable<TestOrphan>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("LabelRegistry is sealed; no further types can be registered.");
        db.Registry.Descriptors.Select(d => d.ClrType).Should().Equal(typeof(TestNote), typeof(TestTodo));
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
        model.FindEntityType(typeof(LabelLink<TestNote, Guid>))!.GetTableName().Should().Be("TL_Memo");
        model.FindEntityType(typeof(LabelLink<TestTodo, int>))!.GetTableName().Should().Be("TL_TestTodo");
        model.FindEntityType(typeof(Label))!.GetTableName().Should().Be("MyLabels");
    }

    [Fact] // 泛型主鍵推斷
    public void 只實作marker沒有指定主鍵型別_註冊時拋出清楚的例外()
    {
        var registry = new LabelRegistry();

        var act = () => registry.Labelable<TestMarkerOnly>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("type TestMarkerOnly only implements the non-generic ILabelable marker. " +
                         "Implement ILabelable<TKey> with your key type instead, e.g. ILabelable<int> or ILabelable<Guid>.");
    }

    [Fact] // 泛型主鍵推斷
    public void 主鍵型別自動推斷正確()
    {
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>();
        registry.Labelable<TestTodo>();

        registry.Descriptors.Single(d => d.ClrType == typeof(TestNote)).KeyType.Should().Be(typeof(Guid));
        registry.Descriptors.Single(d => d.ClrType == typeof(TestTodo)).KeyType.Should().Be(typeof(int));
    }

    [Fact] // 泛型主鍵推斷
    public void 實作多個ILabelable泛型介面_主鍵型別有歧義_拋出清楚的例外()
    {
        var registry = new LabelRegistry();

        var act = () => registry.Labelable<TestDualKey>();

        var message = act.Should().Throw<InvalidOperationException>()
            .WithMessage("type TestDualKey implements several ILabelable<TKey> interfaces (*), so the key type cannot be inferred. Keep exactly one.")
            .Which.Message;
        message.Should().Contain("Int32").And.Contain("Guid");
        registry.Descriptors.Should().BeEmpty();
    }

    [Fact]
    public void 新建的Registry預設值()
    {
        var registry = new LabelRegistry();

        registry.LabelTableName.Should().Be("Label");
        registry.LinkTablePrefix.Should().Be("LabelLink_");
        registry.AutoCreateLabels.Should().BeTrue();
        registry.Descriptors.Should().BeEmpty();
    }

    [Fact] // CLAUDE.md 地雷:TypeKey 就是資料表名,預設取 CLR 類別名
    public void 沒指定TypeKey時_表名是預設前綴加CLR類別名()
    {
        using var db = new TestDb();

        var model = db.Context.Model;
        model.FindEntityType(typeof(Label))!.GetTableName().Should().Be("Label");
        model.FindEntityType(typeof(LabelLink<TestNote, Guid>))!.GetTableName().Should().Be("LabelLink_TestNote");
        model.FindEntityType(typeof(LabelLink<TestTodo, int>))!.GetTableName().Should().Be("LabelLink_TestTodo");
        db.Registry.Descriptors.Select(d => d.TypeKey).Should().Equal("TestNote", "TestTodo");
    }

    /// <summary>泛型的可標記型別。</summary>
    private sealed class TestBox<T> : ILabelable<Guid>
    {
        public Guid Id { get; set; }
    }

    [Fact] // README 與 Labelable 的 XML:泛型實體一定要釘 typeKey
    public void 泛型實體沒釘TypeKey_預設是含反引號的CLR名稱_第二個建構型別註冊不了()
    {
        var registry = new LabelRegistry();
        registry.Labelable<TestBox<int>>();

        registry.Descriptors.Single().TypeKey.Should().Be("TestBox`1", "預設取 typeof(T).Name,表名會是 LabelLink_TestBox`1");
        var act = () => registry.Labelable<TestBox<string>>();
        act.Should().Throw<InvalidOperationException>().WithMessage("TypeKey 'TestBox`1' is already registered.");
    }

    [Fact]
    public void 泛型實體釘住TypeKey_各個建構型別可以各自註冊()
    {
        var registry = new LabelRegistry();

        registry.Labelable<TestBox<int>>(typeKey: "IntBox").Labelable<TestBox<string>>(typeKey: "StringBox");

        registry.Descriptors.Select(d => d.TypeKey).Should().Equal("IntBox", "StringBox");
    }

    [Fact]
    public void 同一個CLR型別換個TypeKey再註冊一次_拋例外()
    {
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>();

        var act = () => registry.Labelable<TestNote>(typeKey: "Memo");

        act.Should().Throw<InvalidOperationException>().WithMessage("type TestNote is already registered.");
        registry.Descriptors.Should().ContainSingle();
    }

    [Fact]
    public void Labelable回傳同一個registry_可串接()
    {
        var registry = new LabelRegistry();

        var returned = registry.Labelable<TestNote>().Labelable<TestTodo>(typeKey: "Task");

        returned.Should().BeSameAs(registry);
        registry.Descriptors.Should().HaveCount(2);
    }

    [Fact]
    public void Descriptors依註冊順序_帶出ClrType與KeyType與TypeKey()
    {
        var registry = new LabelRegistry();
        registry.Labelable<TestTodo>(typeKey: "Task");
        registry.Labelable<TestNote>();

        registry.Descriptors.Select(d => (d.ClrType, d.KeyType, d.TypeKey)).Should().Equal(
            (typeof(TestTodo), typeof(int), "Task"),
            (typeof(TestNote), typeof(Guid), "TestNote"));
    }

    [Fact]
    public void Factory不封存registry_Create之後仍可註冊()
    {
        // 1.x 的現行契約:只有 AddLabeling 會封存。改成在 Create 裡封存,
        // 會讓「先 Create 再註冊」的既有寫法開始拋例外,屬破壞性變更。
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>();
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).Options;
        using var context = new TestDbContext(options, registry);

        LabelStoreFactory.Create(context, registry);
        var act = () => registry.Labelable<TestTodo>();

        act.Should().NotThrow();
    }
}
