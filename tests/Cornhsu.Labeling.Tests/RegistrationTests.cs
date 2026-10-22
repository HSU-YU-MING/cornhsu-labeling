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
    public void Factory建立store之後封存registry_再註冊拋例外()
    {
        // 2.0 起跟 AddLabeling 一樣:store 建好之後 registry 就不能再加型別。
        // 1.x 不封存,「先 Create 再註冊」不會報錯,但 model 若已經建好,後註冊的型別不在 model 裡,
        // 要到貼標或查詢時才以缺表之類的資料庫錯誤冒出來
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>();
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).Options;
        using var context = new TestDbContext(options, registry);

        LabelStoreFactory.Create(context, registry);
        var act = () => registry.Labelable<TestTodo>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("LabelRegistry is sealed; no further types can be registered.");
        registry.Descriptors.Select(d => d.ClrType).Should().Equal(typeof(TestNote));
    }

    [Fact]
    public async Task 同一個registry可以建立多個store_封存不影響後來的Create()
    {
        // 無 DI 的 App 常是每個 DbContext 各建一個 store,共用全域的 registry
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>(n => n.Title);
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).Options;
        using var first = new TestDbContext(options, registry);
        first.Database.EnsureCreated();
        LabelStoreFactory.Create(first, registry);

        using var second = new TestDbContext(options, registry);
        var store = LabelStoreFactory.Create(second, registry);
        var note = new TestNote { Id = Guid.NewGuid(), Title = "n" };
        second.Notes.Add(note);
        await second.SaveChangesAsync();
        await store.AttachAsync(note, "論文");

        (await store.FindByLabelAsync("論文")).Should().ContainSingle();
    }

    [Fact]
    public void Factory的參數檢查失敗時_不封存registry()
    {
        var registry = new LabelRegistry();

        var act = () => LabelStoreFactory.Create<TestDbContext>(null!, registry);

        act.Should().Throw<ArgumentNullException>();
        registry.Invoking(r => r.Labelable<TestNote>()).Should().NotThrow();
    }
}
