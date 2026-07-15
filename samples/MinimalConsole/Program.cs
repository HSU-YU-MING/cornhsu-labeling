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

var memo = new Memo { Id = Guid.NewGuid(), Title = "讀論文" };          // Guid 主鍵
var chore = new Chore { Description = "整理文獻資料夾" };                // int 主鍵,資料庫發號
db.AddRange(memo, chore);
await db.SaveChangesAsync();

await store.AttachAsync(memo, "論文", "急件");
await store.AttachAsync(chore, "論文");

Console.WriteLine("標了「論文」的所有東西(跨型別、混合主鍵):");
foreach (var hit in await store.FindByLabelAsync("論文"))
    Console.WriteLine($"  [{hit.EntityTypeKey}] {hit.DisplayName}(Id = {hit.EntityId})");

var counts = await store.GetUsageCountsAsync();
foreach (var label in await store.GetAllAsync())
    Console.WriteLine($"標籤「{label.Name}」使用 {counts.GetValueOrDefault(label.Id)} 次");

public class Memo : ILabelable<Guid>
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

public class Chore : ILabelable<int>
{
    public int Id { get; set; }
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
