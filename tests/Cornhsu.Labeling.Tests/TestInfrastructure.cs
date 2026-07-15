using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
/// 測試資料庫。預設 SQLite in-memory(不是 EF InMemory Provider——它不執行外鍵約束,
/// 測不到本套件的賣點);設環境變數 CORNHSU_TEST_PROVIDER=sqlserver|postgres
/// 可讓同一套測試跑在真的 SQL Server / PostgreSQL 上(每個 TestDb 一個獨立資料庫,
/// 結束時 EnsureDeleted)。連線字串可用 CORNHSU_TEST_SQLSERVER / CORNHSU_TEST_POSTGRES 覆寫。
/// </summary>
public sealed class TestDb : IDisposable
{
    private static readonly string Provider =
        Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() ?? "sqlite";

    private readonly SqliteConnection? _conn;   // 只有 sqlite 用(in-memory 連線要活著)
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly DbContextOptions<TestDbContext> _options;

    public TestDbContext Context { get; }
    public ILabelStore Store { get; }
    public LabelRegistry Registry { get; }

    public TestDb(Action<LabelRegistry>? configure = null)
    {
        var services = new ServiceCollection();

        // EnableServiceProviderCaching(false):EF 的 model cache 以 DbContext 型別為 key(見 §8.6),
        // 測試裡每個 TestDb 的 registry 都不同,必須隔離內部 provider 才不會拿到別的測試快取的 model。
        // 正式 App 不需要這行——registry 是全 App 單例。
        switch (Provider)
        {
            case "sqlserver":
            {
                var server = Environment.GetEnvironmentVariable("CORNHSU_TEST_SQLSERVER")
                    ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
                var cs = $"{server};Database=cornhsu_test_{Guid.NewGuid():N}";
                services.AddDbContext<TestDbContext>(o => o.UseSqlServer(cs).EnableServiceProviderCaching(false));
                break;
            }
            case "postgres":
            {
                var server = Environment.GetEnvironmentVariable("CORNHSU_TEST_POSTGRES")
                    ?? "Host=localhost;Username=postgres;Password=postgres";
                var cs = $"{server};Database=cornhsu_test_{Guid.NewGuid():N}";
                services.AddDbContext<TestDbContext>(o => o.UseNpgsql(cs).EnableServiceProviderCaching(false));
                break;
            }
            default:
            {
                _conn = new SqliteConnection("DataSource=:memory:");
                _conn.Open();
                var conn = _conn;
                services.AddDbContext<TestDbContext>(o => o.UseSqlite(conn).EnableServiceProviderCaching(false));
                break;
            }
        }

        services.AddLabeling<TestDbContext>(configure ?? Default);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        Context = _scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Store = _scope.ServiceProvider.GetRequiredService<ILabelStore>();
        Registry = _scope.ServiceProvider.GetRequiredService<LabelRegistry>();
        _options = (DbContextOptions<TestDbContext>)Context.GetService<IDbContextOptions>();

        Context.Database.EnsureCreated();
    }

    /// <summary>
    /// 同一個資料庫上的第二個 context(模擬另一個使用端;並發測試用)。呼叫端負責 Dispose。
    /// </summary>
    public TestDbContext CreateSecondContext() => new(_options, Registry);

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
        if (Provider != "sqlite")
            Context.Database.EnsureDeleted();   // 伺服器型 provider:把這顆測試資料庫整個刪掉
        _scope.Dispose();
        _provider.Dispose();
        _conn?.Dispose();
    }
}
