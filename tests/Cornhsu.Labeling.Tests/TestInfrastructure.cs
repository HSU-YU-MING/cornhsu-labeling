using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

/// <summary>同時實作兩個 ILabelable&lt;TKey&gt;,用來測「主鍵型別有歧義」的註冊例外。</summary>
public class TestDualKey : ILabelable<int>, ILabelable<Guid>
{
    int ILabelable<int>.Id => 0;
    Guid ILabelable<Guid>.Id => Guid.Empty;
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

    /// <param name="configure">registry 設定;null 時註冊 TestNote 與 TestTodo。</param>
    /// <param name="interceptors">掛到 context options 上的 EF 攔截器(模擬競態、寫入失敗用)。
    /// 同一份 options 也給 <see cref="CreateSecondContext"/> 用,攔截器要自己分辨是哪個 context。</param>
    public TestDb(Action<LabelRegistry>? configure = null, params IInterceptor[] interceptors)
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
                services.AddDbContext<TestDbContext>(o => o.UseSqlServer(cs).EnableServiceProviderCaching(false)
                    .AddInterceptors(interceptors));
                break;
            }
            case "postgres":
            {
                var server = Environment.GetEnvironmentVariable("CORNHSU_TEST_POSTGRES")
                    ?? "Host=localhost;Username=postgres;Password=postgres";
                var cs = $"{server};Database=cornhsu_test_{Guid.NewGuid():N}";
                services.AddDbContext<TestDbContext>(o => o.UseNpgsql(cs).EnableServiceProviderCaching(false)
                    .AddInterceptors(interceptors));
                break;
            }
            default:
            {
                _conn = new SqliteConnection("DataSource=:memory:");
                _conn.Open();
                var conn = _conn;
                services.AddDbContext<TestDbContext>(o => o.UseSqlite(conn).EnableServiceProviderCaching(false)
                    .AddInterceptors(interceptors));
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

/// <summary>
/// 自訂模型的 DbContext 用的資料庫(soft-delete、影子外鍵這類 TestDbContext 沒有的形狀)。
/// 跟 <see cref="TestDb"/> 一樣吃 CORNHSU_TEST_PROVIDER:每個實例一個獨立資料庫,結束時刪掉。
/// </summary>
public sealed class CustomDb<TContext> : IDisposable where TContext : DbContext
{
    private static readonly string Provider =
        Environment.GetEnvironmentVariable("CORNHSU_TEST_PROVIDER")?.ToLowerInvariant() ?? "sqlite";

    private readonly SqliteConnection? _conn;
    private readonly Func<DbContextOptions<TContext>, LabelRegistry, TContext> _factory;

    public DbContextOptions<TContext> Options { get; }
    public LabelRegistry Registry { get; }
    public TContext Context { get; }
    public ILabelStore Store { get; }

    public CustomDb(LabelRegistry registry, Func<DbContextOptions<TContext>, LabelRegistry, TContext> factory,
        params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TContext>().EnableServiceProviderCaching(false).AddInterceptors(interceptors);
        switch (Provider)
        {
            case "sqlserver":
            {
                var server = Environment.GetEnvironmentVariable("CORNHSU_TEST_SQLSERVER")
                    ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
                builder.UseSqlServer($"{server};Database=cornhsu_test_{Guid.NewGuid():N}");
                break;
            }
            case "postgres":
            {
                var server = Environment.GetEnvironmentVariable("CORNHSU_TEST_POSTGRES")
                    ?? "Host=localhost;Username=postgres;Password=postgres";
                builder.UseNpgsql($"{server};Database=cornhsu_test_{Guid.NewGuid():N}");
                break;
            }
            default:
                _conn = new SqliteConnection("DataSource=:memory:");
                _conn.Open();
                builder.UseSqlite(_conn);
                break;
        }

        Options = builder.Options;
        Registry = registry;
        _factory = factory;
        Context = factory(Options, registry);
        Context.Database.EnsureCreated();
        Store = LabelStoreFactory.Create(Context, registry);
    }

    /// <summary>同一個資料庫上的另一個 context。呼叫端負責 Dispose。</summary>
    public TContext CreateSecondContext() => _factory(Options, Registry);

    public void Dispose()
    {
        if (Provider != "sqlite")
            Context.Database.EnsureDeleted();
        Context.Dispose();
        _conn?.Dispose();
    }
}
