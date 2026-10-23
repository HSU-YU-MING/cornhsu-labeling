using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.Tests;

/// <summary>Label.Name 設成 NOCASE collation 的 DbContext。</summary>
public class NoCaseDbContext : DbContext
{
    private readonly LabelRegistry _registry;

    public NoCaseDbContext(DbContextOptions<NoCaseDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    public DbSet<TestNote> Notes => Set<TestNote>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyLabelModel(_registry);
        b.Entity<Label>().Property(l => l.Name).UseCollation("NOCASE");
    }
}

/// <summary>
/// 名稱比對不分大小寫的 SQLite in-memory 資料庫。SQL Server 預設的 collation(SQL_Latin1_General_CP1_CI_AS)
/// 不分大小寫,但 CI 預設那條線跑的是區分大小寫的 SQLite,那種差異在那裡測不到;
/// 把 Label.Name 設成 NOCASE 就能在預設那條線重現。不吃 CORNHSU_TEST_PROVIDER,一律 SQLite。
/// </summary>
public sealed class CaseInsensitiveDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public NoCaseDbContext Context { get; }
    public ILabelStore Store { get; }

    public CaseInsensitiveDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var registry = new LabelRegistry().Labelable<TestNote>(n => n.Title);
        var options = new DbContextOptionsBuilder<NoCaseDbContext>()
            .UseSqlite(_conn)
            .EnableServiceProviderCaching(false)   // 理由同 TestDb:每個實例的 registry 不同,不共用 model 快取
            .Options;
        Context = new NoCaseDbContext(options, registry);
        Context.Database.EnsureCreated();
        Store = LabelStoreFactory.Create(Context, registry);
    }

    public async Task<TestNote> AddNoteAsync(string title = "note")
    {
        var note = new TestNote { Id = Guid.NewGuid(), Title = title };
        Context.Notes.Add(note);
        await Context.SaveChangesAsync();
        return note;
    }

    public void Dispose()
    {
        Context.Dispose();
        _conn.Dispose();
    }
}
