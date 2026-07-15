// 第二個消費者:證明抽象不是只服務 QuillNest。
// 只依賴 SQLite,約 30 行核心程式碼。
using Cornhsu.Labeling;
using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using var conn = new SqliteConnection("DataSource=:memory:");
conn.Open();

var services = new ServiceCollection();
services.AddDbContext<SampleDbContext>(o => o.UseSqlite(conn));
services.AddLabeling<SampleDbContext>(r =>
{
    r.Labelable<Memo>(m => m.Title);
    r.Labelable<Chore>(c => c.Description);
});

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
db.Database.EnsureCreated();
var store = scope.ServiceProvider.GetRequiredService<ILabelStore>();

var memo = new Memo { Id = Guid.NewGuid(), Title = "讀論文" };
var chore = new Chore { Id = Guid.NewGuid(), Description = "整理文獻資料夾" };
db.AddRange(memo, chore);
await db.SaveChangesAsync();

await store.AttachAsync(memo, "論文", "急件");
await store.AttachAsync(chore, "論文");

Console.WriteLine("標了「論文」的所有東西(跨型別):");
foreach (var hit in await store.FindByLabelAsync("論文"))
    Console.WriteLine($"  [{hit.EntityTypeKey}] {hit.DisplayName}");

var counts = await store.GetUsageCountsAsync();
foreach (var label in await store.GetAllAsync())
    Console.WriteLine($"標籤「{label.Name}」使用 {counts.GetValueOrDefault(label.Id)} 次");

public class Memo : ILabelable
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

public class Chore : ILabelable
{
    public Guid Id { get; set; }
    public string Description { get; set; } = "";
}

public class SampleDbContext : DbContext
{
    private readonly LabelRegistry _registry;

    public SampleDbContext(DbContextOptions<SampleDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    public DbSet<Memo> Memos => Set<Memo>();
    public DbSet<Chore> Chores => Set<Chore>();

    protected override void OnModelCreating(ModelBuilder b) => b.ApplyLabelModel(_registry);
}
