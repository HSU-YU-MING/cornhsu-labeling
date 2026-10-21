// README 的守門員。
//
// 這個檔案不是示範,也不是測試——它的唯一工作是讓 README 教的每一個呼叫「真的編譯得過」。
// 走鐘的機制很固定:改一支 API 的簽章時,tests/ 會跟著改(不然紅燈),README 不會(它是散文)。
// 於是文件會安靜地過期,而讀者是照文件寫的。
//
// 所以這裡刻意用跟 README 一字不差的型別名(Note / TodoItem / AppDbContext)與呼叫形狀,
// 讓「編譯失敗」變成「你動到了對外教的東西,回去看 README」的訊號。
//
// 加新的公開 API 而且寫進 README 時,也要在這裡補一行。
// 唯一沒被守到的是 README 裡 IDesignTimeDbContextFactory 那行宣告以外的散文敘述。
using Cornhsu.Labeling;
using Cornhsu.Labeling.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

// 先報出這一輪真的跑在哪條線上。CI 的矩陣是用 -p:CornhsuEfVersion= 換 EF 版本,
// 而一個被靜默忽略的旗標會讓矩陣看起來在跑三個版本、其實跑了三次同一個 ——
// 印出來就不必相信旗標,直接看得到。
Console.WriteLine(
    $"— {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
    $"EF Core {typeof(DbContext).Assembly.GetName().Version}");

using var conn = new SqliteConnection("DataSource=:memory:");
conn.Open();

// ---- README「Quick start」2. Register ----
var services = new ServiceCollection();
services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));
services.AddLabeling<AppDbContext>(r =>
{
    r.Labelable<Note>(n => n.Title);
    r.Labelable<TodoItem>(t => t.Content);
});

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
db.Database.EnsureCreated();
var store = scope.ServiceProvider.GetRequiredService<ILabelStore>();

var note = new Note { Id = Guid.NewGuid(), Title = "Reading list" };
var todo = new TodoItem { Content = "File the papers" };
db.AddRange(note, todo);
await db.SaveChangesAsync();

// ---- README「Quick start」4. Use it ----
await store.AttachAsync(note, "paper", "urgent");            // missing labels are created automatically
await store.AttachAsync(todo, "paper");                      // 讓下面的跨型別查詢真的混到兩種主鍵
var all   = await store.FindByLabelAsync("paper");            // cross-type, IReadOnlyList<LabelHit>
var notes = await store.QueryByLabelAsync<Note>("paper");     // strongly typed IQueryable<Note>

// Multi-label AND / OR:
var urgent = await store.FindByLabelsAsync(
    new[] { "paper", "urgent" }, LabelMatch.All);             // tagged paper AND urgent
var either = await store.QueryByLabelsAsync<Note>(
    new[] { "paper", "urgent" }, LabelMatch.Any);             // tagged paper OR urgent

// Reading the labels of 50 rows for a list view (one query, not 50):
var visibleNotes = await db.Notes.ToListAsync();
var labelsByNote = await store.GetLabelsOfManyAsync(visibleNotes);
foreach (var n in visibleNotes)
    Render(n, labelsByNote[n]);                              // every entity has an entry (possibly empty)

// Bulk attach ("select several, tag them all urgent"; idempotent; all the links go in one
// SaveChanges, after any labels it has to auto-create, which are saved one at a time):
var selectedNotes = visibleNotes;
await store.AttachManyAsync(selectedNotes, new[] { "urgent" });

// Cross-type hits can have different key types (Note uses Guid, TodoItem uses int),
// which is why LabelHit.EntityId is object. To get it typed:
var todoIds = all
    .Where(h => h.EntityClrType == typeof(TodoItem))
    .Select(h => h.EntityIdAs<int>());

// ---- 上面那些 README 用註解做出的承諾,在這裡真的驗一次 ----
// 只驗 README 明講的事;行為的完整驗證在 tests/,不要在這裡長成第二套測試。
Check(all.Count == 2, "FindByLabelAsync 跨型別:Note 與 TodoItem 都在結果裡");
Check(await notes.CountAsync() == 1, "QueryByLabelAsync<Note> 是可以繼續組合的 IQueryable");
Check(notes is IAsyncEnumerable<Note>, "有命中時是 EF 查詢:README Limitations 教的 is IAsyncEnumerable<T> 才分得出空結果");
Check(urgent.Count == 1 && await either.CountAsync() == 1, "LabelMatch.All / Any 都吃得到");
Check(labelsByNote.Count == visibleNotes.Count, "GetLabelsOfManyAsync:每個 entity 都有一格");
Check(todoIds.Single() == todo.Id, "EntityIdAs<int>() 拿得到 TodoItem 的 int 主鍵");

// ---- README「Quick start」2. Register 之後那段:型別鍵與表名 ----
// README 說連結表名 = LabelLink_ + 型別鍵、型別鍵預設取類別名(所以改類別名 = 改表名),可用 typeKey 釘住。
var diRegistry = scope.ServiceProvider.GetRequiredService<LabelRegistry>();
Check(diRegistry.Descriptors.Single(d => d.ClrType == typeof(Note)).TypeKey == nameof(Note)
      && db.Model.FindEntityType(typeof(LabelLink<Note, Guid>))!.GetTableName() == diRegistry.LinkTablePrefix + nameof(Note)
      && db.Model.FindEntityType(typeof(Label))!.GetTableName() == diRegistry.LabelTableName,
      "型別鍵預設是類別名;連結表名 = LinkTablePrefix + 型別鍵,Label 表名 = LabelTableName");
var pinned = new LabelRegistry().Labelable<Note>(n => n.Title, typeKey: "Note");
Check(pinned.Descriptors.Single().TypeKey == "Note", "typeKey 參數釘得住型別鍵");

// ---- README「Managing labels」----
// Curated labels: create them up front with a color, an icon and a parent
var work   = await store.CreateAsync("work", color: "#FF5722", icon: "briefcase");
var review = await store.CreateAsync("review", parentId: work.Id);   // a child of "work"
await store.AttachAsync(note, "review");                    // README 沒有這行:讓下面的「含子孫」真的找得到東西
await store.RenameAsync(review.Id, "peer review");          // links store the Id, so they are untouched
await store.UpdateAsync(work.Id, l => l.SortOrder = 1);     // any field but Id; a new ParentId is checked for cycles
var found  = await store.FindAsync("work");                 // null when there is no such label
var labels = await store.GetAllAsync();                     // ordered by SortOrder, then Name

// Queries include descendant labels by default: "work" also finds what is tagged "peer review"
var underWork = await store.FindByLabelAsync("work");
var onlyWork  = await store.FindByLabelAsync("work", includeDescendants: false);

await store.DetachAsync(note, "urgent");                    // names that are not attached are ignored
var noteLabels = await store.GetLabelsOfAsync(note);

// Tag cloud: label Id → number of links across every type. Labels used nowhere have no entry.
var counts = await store.GetUsageCountsAsync();
var uses   = counts.GetValueOrDefault(work.Id);

// README 沒有這段:先驗「還有子標籤時拋例外」,再掛一筆伴生資料(README「Extending Label」),
// 清掉追蹤器,讓下面的刪除只能靠資料庫的 cascade,而不是 EF 追蹤器代勞。
var deleteParentThrew = false;
try { await store.DeleteAsync(work.Id); } catch (InvalidOperationException) { deleteParentThrew = true; }
db.Add(new LabelMeta { LabelId = review.Id });
await db.SaveChangesAsync();
db.ChangeTracker.Clear();

await store.DeleteAsync(review.Id);    // links on every type are removed by cascade;
                                       // throws while the label still has children

Check(found?.Id == work.Id && labels.Any(l => l.Name == "peer review"), "CreateAsync / RenameAsync / FindAsync / GetAllAsync");
Check(underWork.Any(h => h.EntityClrType == typeof(Note)) && onlyWork.Count == 0,
      "includeDescendants 預設含子孫標籤,傳 false 只看標籤本身");
Check(noteLabels.All(l => l.Name != "urgent"), "DetachAsync 撕得掉");
Check(!counts.ContainsKey(work.Id) && uses == 0, "GetUsageCountsAsync:沒被用到的標籤不在字典裡");
Check(deleteParentThrew, "DeleteAsync:還有子標籤時拋例外");
Check(await store.FindAsync("peer review") is null
      && !await db.Set<LabelMeta>().AnyAsync(m => m.LabelId == review.Id)
      && (await store.GetLabelsOfAsync(note)).All(l => l.Name != "peer review"),
      "DeleteAsync:標籤、它的連結與伴生表那一列都由 cascade 清掉");

// ---- README「Without a DI container」----
await WithoutDiContainer();

// ---- README「curated label set」:r.AutoCreateLabels = false ----
// 用另一個 DbContext 型別跑,因為 EF 的 model cache 以 DbContext 型別為 key
// (見 LabelRegistry 的類別註解),同型別配兩份 registry 是自找麻煩。
using var curatedConn = new SqliteConnection("DataSource=:memory:");
curatedConn.Open();
var curatedServices = new ServiceCollection();
curatedServices.AddDbContext<CuratedDbContext>(o => o.UseSqlite(curatedConn));
curatedServices.AddLabeling<CuratedDbContext>(r =>
{
    r.Labelable<Note>(n => n.Title);
    r.AutoCreateLabels = false;
});

using var curatedProvider = curatedServices.BuildServiceProvider();
using var curatedScope = curatedProvider.CreateScope();
var curatedDb = curatedScope.ServiceProvider.GetRequiredService<CuratedDbContext>();
curatedDb.Database.EnsureCreated();
var curatedStore = curatedScope.ServiceProvider.GetRequiredService<ILabelStore>();

var curatedNote = new Note { Id = Guid.NewGuid(), Title = "Curated" };
curatedDb.Add(curatedNote);
await curatedDb.SaveChangesAsync();

var threw = false;
try
{
    await curatedStore.AttachAsync(curatedNote, "not-in-the-curated-set");
}
catch (InvalidOperationException)
{
    threw = true;   // README: "throws a clear exception for an unknown label"
}
Check(threw, "AutoCreateLabels = false:未知標籤拋例外,而不是默默建一個");

Console.WriteLine("✔ README 教的每一個呼叫都編譯並執行過了");

// README「Without a DI container」:中間那段照抄;前後是讓它真的跑得起來的鷹架。
// 這裡沿用 AppDbContext 而且註冊的型別與 DI 那份完全相同,EF 依 DbContext 型別快取的 model
// 才會是同一個形狀(README 說全 App 只能有一個 registry,就是在講這件事)。
static async Task WithoutDiContainer()
{
    using var conn = new SqliteConnection("DataSource=:memory:");
    conn.Open();
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

    var registry = new LabelRegistry();
    registry.Labelable<Note>(n => n.Title);
    registry.Labelable<TodoItem>(t => t.Content);

    using var db = new AppDbContext(options, registry);
    var store = LabelStoreFactory.Create(db, registry);   // optional third argument: ILogger<ILabelStore>

    db.Database.EnsureCreated();
    var note = new Note { Id = Guid.NewGuid(), Title = "No container" };
    db.Add(note);
    await db.SaveChangesAsync();
    await store.AttachAsync(note, "paper");
    Check((await store.FindByLabelAsync("paper")).Count == 1, "LabelStoreFactory.Create:不經 DI 容器也能用");
}

static void Render(Note note, IReadOnlyList<Label> labels)
    => Console.WriteLine($"  {note.Title}: {string.Join(", ", labels.Select(l => l.Name))}");

static void Check(bool ok, string what)
{
    if (!ok) throw new InvalidOperationException($"README 的承諾對不上:{what}");
    Console.WriteLine($"  ✔ {what}");
}

// ---- README「Quick start」1. Implement ILabelable<TKey> ----
public class Note : ILabelable<Guid>
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

public class TodoItem : ILabelable<int>     // an existing project's int identity key just works
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
}

// ---- README「Extending Label」:1:1 companion table ----
// Your companion table: the fields the package should not know about
public class LabelMeta
{
    public Guid LabelId { get; set; }          // 1:1 with the Cornhsu Label
    public Label Label { get; set; } = default!;
    public string LabelType { get; set; } = "tag";   // your business semantics
    public string? AllowedModule { get; set; }
}

// ---- README「Quick start」3. One line in your DbContext ----
// README 把 ApplyLabelModel 與 companion table 分成兩段講,這裡併在同一個 DbContext 裡,
// 因為要一起建表才驗得到 cascade delete 真的接上了。
public class AppDbContext : DbContext
{
    private readonly LabelRegistry _registry;
    public AppDbContext(DbContextOptions<AppDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    public DbSet<Note> Notes => Set<Note>();
    public DbSet<TodoItem> TodoItems => Set<TodoItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyLabelModel(_registry);

        // Your own DbContext configuration, alongside ApplyLabelModel
        b.Entity<LabelMeta>(e =>
        {
            e.HasKey(x => x.LabelId);
            e.HasOne(x => x.Label).WithOne()
             .HasForeignKey<LabelMeta>(x => x.LabelId)
             .OnDelete(DeleteBehavior.Cascade);        // deleting a label cleans up its metadata
        });
    }
}

/// 只為了驗 AutoCreateLabels = false 而存在,README 沒有這個型別。
public class CuratedDbContext : DbContext
{
    private readonly LabelRegistry _registry;
    public CuratedDbContext(DbContextOptions<CuratedDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder b) => b.ApplyLabelModel(_registry);
}

// ---- README「Using EF Core migrations」----
// 只需要編譯得過:它的價值在於「AppDbContext 的建構子簽章 + registry 的組法」有人守著。
// 非 ASP.NET 的 App(WPF、console)是照這段抄的。
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // The types registered here must match the runtime registration in AddLabeling
        var registry = new LabelRegistry();
        registry.Labelable<Note>(n => n.Title);
        registry.Labelable<TodoItem>(t => t.Content);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=app.db")
            .Options;
        return new AppDbContext(options, registry);
    }
}
