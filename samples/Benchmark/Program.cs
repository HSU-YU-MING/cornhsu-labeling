// 效能量測(規畫書原則:先量再優化)。
// 5 個型別 × 10k 筆、~100k 連結,SQLite 檔案資料庫。
// 量測項目決定 v1.0 前的兩個 API 設計:批次讀(GetLabelsOfManyAsync)與多標籤 AND/OR。
// 執行:dotnet run -c Release --project samples/Benchmark
using System.Diagnostics;
using Cornhsu.Labeling;
using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

const int EntitiesPerType = 10_000;
var dbPath = Path.Combine(Path.GetTempPath(), "cornhsu-labeling-bench.db");
if (File.Exists(dbPath)) File.Delete(dbPath);

var services = new ServiceCollection();
services.AddDbContext<BenchDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
services.AddLabeling<BenchDbContext>(r =>
{
    r.Labelable<NoteE>(n => n.Title);
    r.Labelable<TodoE>(t => t.Title);
    r.Labelable<EventE>(e => e.Title);
    r.Labelable<StickyE>(s => s.Title);
    r.Labelable<ProjectE>(p => p.Title);
});
using var provider = services.BuildServiceProvider();

// ---- 種資料 ----
Console.WriteLine($"種資料:5 型別 × {EntitiesPerType:N0} 筆 …");
var seedWatch = Stopwatch.StartNew();
Guid hotId, rareId, rootId, andAId, andBId;
using (var scope = provider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BenchDbContext>();
    db.Database.EnsureCreated();

    // 標籤:hot(每型別 ~10% 命中)、rare(~0.1%)、andA/andB(重疊約一半)、
    // 階層 根→子1/子2/子3(量 includeDescendants),再補一般標籤到 30 個
    var labels = new List<Label>();
    Label NewLabel(string name, Guid? parent = null)
    {
        var l = new Label { Id = Guid.NewGuid(), Name = name, ParentId = parent, CreatedAt = DateTimeOffset.UtcNow };
        labels.Add(l);
        return l;
    }
    var hot = NewLabel("hot"); var rare = NewLabel("rare");
    var andA = NewLabel("andA"); var andB = NewLabel("andB");
    var root = NewLabel("根");
    var kids = new[] { NewLabel("子1", root.Id), NewLabel("子2", root.Id), NewLabel("子3", root.Id) };
    for (var i = 0; i < 21; i++) NewLabel($"一般{i:D2}");
    db.AddRange(labels);
    (hotId, rareId, rootId, andAId, andBId) = (hot.Id, rare.Id, root.Id, andA.Id, andB.Id);

    var rnd = new Random(42);
    var generalIds = labels.Where(l => l.Name.StartsWith("一般")).Select(l => l.Id).ToArray();

    List<Guid> LabelsFor(int i)
    {
        // 每筆 1~3 個一般標籤;固定比例貼上量測用標籤
        var picked = new List<Guid>();
        for (var k = rnd.Next(1, 4); k > 0; k--) picked.Add(generalIds[rnd.Next(generalIds.Length)]);
        if (i % 10 == 0) picked.Add(hotId);                       // 10% hot
        if (i % 1000 == 0) picked.Add(rareId);                    // 0.1% rare
        if (i % 10 is 1 or 2) picked.Add(andAId);                 // 20% andA
        if (i % 10 is 2 or 3) picked.Add(andBId);                 // 20% andB(與 andA 重疊 10%)
        if (i % 100 == 0) picked.Add(kids[i % 3].Id);             // 1% 子標籤
        return picked.Distinct().ToList();
    }

    void Seed<TEntity, TKey>(Func<int, TEntity> create)
        where TEntity : class, ILabelable<TKey>
        where TKey : notnull
    {
        var entities = Enumerable.Range(0, EntitiesPerType).Select(create).ToList();
        db.AddRange(entities);
        db.SaveChanges();                                          // int 主鍵由資料庫發號,先存才有 Id
        var links = new List<LabelLink<TEntity, TKey>>();
        for (var i = 0; i < entities.Count; i++)
            foreach (var labelId in LabelsFor(i))
                links.Add(new LabelLink<TEntity, TKey> { LabelId = labelId, EntityId = entities[i].Id, AttachedAt = DateTimeOffset.UtcNow });
        db.AddRange(links);
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    Seed<NoteE, int>(i => new NoteE { Title = $"note {i}" });
    Seed<TodoE, int>(i => new TodoE { Title = $"todo {i}" });
    Seed<EventE, int>(i => new EventE { Title = $"event {i}" });
    Seed<StickyE, Guid>(i => new StickyE { Id = Guid.NewGuid(), Title = $"sticky {i}" });
    Seed<ProjectE, Guid>(i => new ProjectE { Id = Guid.NewGuid(), Title = $"project {i}" });
}
Console.WriteLine($"種資料完成:{seedWatch.Elapsed.TotalSeconds:F1}s,DB 檔案 {new FileInfo(dbPath).Length / 1024 / 1024} MB");
Console.WriteLine();

// ---- 量測 ----
// 每項:暖身 2 次、量 10 次、報中位數。每次量測用新 scope(避免 change tracker 累積)。
async Task<(double medianMs, int result)> MeasureAsync(Func<IServiceScope, Task<int>> body)
{
    var times = new List<double>();
    var result = 0;
    for (var i = 0; i < 12; i++)
    {
        using var scope = provider.CreateScope();
        var sw = Stopwatch.StartNew();
        result = await body(scope);
        sw.Stop();
        if (i >= 2) times.Add(sw.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    return ((times[4] + times[5]) / 2, result);
}

static ILabelStore StoreOf(IServiceScope s) => s.ServiceProvider.GetRequiredService<ILabelStore>();
static BenchDbContext DbOf(IServiceScope s) => s.ServiceProvider.GetRequiredService<BenchDbContext>();

async Task ReportAsync(string name, Func<IServiceScope, Task<int>> body)
{
    var (ms, result) = await MeasureAsync(body);
    Console.WriteLine($"{name,-58} {ms,8:F1} ms   (結果 {result:N0})");
}

Console.WriteLine("── 跨型別查詢 FindByLabelAsync(5 型別 = 5 次查詢)──");
await ReportAsync("FindByLabelAsync(hot)  ~10% 命中 = 5k 筆", async s => (await StoreOf(s).FindByLabelAsync("hot")).Count);
await ReportAsync("FindByLabelAsync(rare) ~0.1% 命中 = 50 筆", async s => (await StoreOf(s).FindByLabelAsync("rare")).Count);
await ReportAsync("FindByLabelAsync(根, includeDescendants) 階層", async s => (await StoreOf(s).FindByLabelAsync("根")).Count);
Console.WriteLine();

Console.WriteLine("── 單型別強型別查詢 ──");
await ReportAsync("QueryByLabelAsync<NoteE>(hot) + CountAsync", async s => await (await StoreOf(s).QueryByLabelAsync<NoteE>("hot")).CountAsync());
Console.WriteLine();

Console.WriteLine("── 清單畫面 N+1:50 筆實體逐筆 GetLabelsOfAsync ──");
await ReportAsync("迴圈 50 次 GetLabelsOfAsync(現況,N+1)", async s =>
{
    var db = DbOf(s);
    var store = StoreOf(s);
    var notes = await db.Notes.AsNoTracking().OrderBy(n => n.Id).Take(50).ToListAsync();
    var total = 0;
    foreach (var n in notes)
        total += (await store.GetLabelsOfAsync(n)).Count;
    return total;
});
await ReportAsync("GetLabelsOfManyAsync(50 筆,一次查詢)", async s =>
{
    var db = DbOf(s);
    var notes = await db.Notes.AsNoTracking().OrderBy(n => n.Id).Take(50).ToListAsync();
    var map = await StoreOf(s).GetLabelsOfManyAsync(notes);
    return map.Sum(p => p.Value.Count);
});
Console.WriteLine();

Console.WriteLine("── 多標籤 AND / OR ──");
await ReportAsync("QueryByLabelsAsync<NoteE>(All,2 標籤) + Count", async s =>
    await (await StoreOf(s).QueryByLabelsAsync<NoteE>(new[] { "andA", "andB" }, LabelMatch.All)).CountAsync());
await ReportAsync("QueryByLabelsAsync<NoteE>(Any,2 標籤) + Count", async s =>
    await (await StoreOf(s).QueryByLabelsAsync<NoteE>(new[] { "andA", "andB" }, LabelMatch.Any)).CountAsync());
await ReportAsync("FindByLabelsAsync(All,2 標籤,跨 5 型別)", async s =>
    (await StoreOf(s).FindByLabelsAsync(new[] { "andA", "andB" }, LabelMatch.All)).Count);
Console.WriteLine();
Console.WriteLine($"完成。DB 檔案保留於 {dbPath}(下次執行會重建)");

public class NoteE : ILabelable<int> { public int Id { get; set; } public string Title { get; set; } = ""; }
public class TodoE : ILabelable<int> { public int Id { get; set; } public string Title { get; set; } = ""; }
public class EventE : ILabelable<int> { public int Id { get; set; } public string Title { get; set; } = ""; }
public class StickyE : ILabelable<Guid> { public Guid Id { get; set; } public string Title { get; set; } = ""; }
public class ProjectE : ILabelable<Guid> { public Guid Id { get; set; } public string Title { get; set; } = ""; }

public class BenchDbContext : DbContext
{
    private readonly LabelRegistry _registry;
    public BenchDbContext(DbContextOptions<BenchDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    public DbSet<NoteE> Notes => Set<NoteE>();
    public DbSet<TodoE> Todos => Set<TodoE>();
    public DbSet<EventE> Events => Set<EventE>();
    public DbSet<StickyE> Stickies => Set<StickyE>();
    public DbSet<ProjectE> Projects => Set<ProjectE>();

    protected override void OnModelCreating(ModelBuilder b) => b.ApplyLabelModel(_registry);
}
