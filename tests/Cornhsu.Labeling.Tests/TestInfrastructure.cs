using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cornhsu.Labeling.Tests;

/// <summary>Guid 主鍵的可標記型別。</summary>
public class TestNote : ILabelable<Guid>
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>int 主鍵的可標記型別——與 TestNote 混用,驗證混合主鍵情境。</summary>
public class TestTodo : ILabelable<int>
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
}

/// <summary>實作了 ILabelable&lt;Guid&gt; 但故意不註冊,用來測未註冊型別的例外。</summary>
public class TestOrphan : ILabelable<Guid>
{
    public Guid Id { get; set; }
}

/// <summary>只實作非泛型 marker,用來測「無法推斷主鍵型別」的註冊例外。</summary>
public class TestMarkerOnly : ILabelable
{
}

public class TestDbContext : DbContext
{
    private readonly LabelRegistry _registry;

    public TestDbContext(DbContextOptions<TestDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    public DbSet<TestNote> Notes => Set<TestNote>();
    public DbSet<TestTodo> Todos => Set<TestTodo>();

    protected override void OnModelCreating(ModelBuilder b) => b.ApplyLabelModel(_registry);
}

/// <summary>
/// 用 SQLite in-memory(不是 EF InMemory Provider)——EF InMemory 不執行外鍵約束,
/// 測不到本套件的賣點。連線必須手動開啟並持有,關掉連線資料就沒了。
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public TestDbContext Context { get; }
    public ILabelStore Store { get; }
    public LabelRegistry Registry { get; }

    public TestDb(Action<LabelRegistry>? configure = null)
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var services = new ServiceCollection();
        // EnableServiceProviderCaching(false):EF 的 model cache 以 DbContext 型別為 key(見 §8.6),
        // 測試裡每個 TestDb 的 registry 都不同,必須隔離內部 provider 才不會拿到別的測試快取的 model。
        // 正式 App 不需要這行——registry 是全 App 單例。
        services.AddDbContext<TestDbContext>(o => o.UseSqlite(_conn).EnableServiceProviderCaching(false));
        services.AddLabeling<TestDbContext>(configure ?? Default);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        Context = _scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Store = _scope.ServiceProvider.GetRequiredService<ILabelStore>();
        Registry = _scope.ServiceProvider.GetRequiredService<LabelRegistry>();

        Context.Database.EnsureCreated();
    }

    private static void Default(LabelRegistry r)
    {
        r.Labelable<TestNote>(n => n.Title);
        r.Labelable<TestTodo>(t => t.Content);
    }

    public async Task<TestNote> AddNoteAsync(string title = "note")
    {
        var note = new TestNote { Id = Guid.NewGuid(), Title = title };
        Context.Notes.Add(note);
        await Context.SaveChangesAsync();
        return note;
    }

    public async Task<TestTodo> AddTodoAsync(string content = "todo")
    {
        var todo = new TestTodo { Content = content };   // int Id 由資料庫發號
        Context.Todos.Add(todo);
        await Context.SaveChangesAsync();
        return todo;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _conn.Dispose();
    }
}
