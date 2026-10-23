# Cornhsu.Labeling

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.Labeling.svg?label=Cornhsu.Labeling)](https://www.nuget.org/packages/Cornhsu.Labeling)
[![NuGet](https://img.shields.io/nuget/v/Cornhsu.Labeling.EntityFrameworkCore.svg?label=Cornhsu.Labeling.EntityFrameworkCore)](https://www.nuget.org/packages/Cornhsu.Labeling.EntityFrameworkCore)
[![Downloads](https://img.shields.io/nuget/dt/Cornhsu.Labeling.EntityFrameworkCore.svg)](https://www.nuget.org/packages/Cornhsu.Labeling.EntityFrameworkCore)
[![CI](https://github.com/HSU-YU-MING/cornhsu-labeling/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-labeling/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[Project write-up](https://cornhsu.com/cornhsu-labeling) · [NuGet](https://www.nuget.org/packages/Cornhsu.Labeling.EntityFrameworkCore) · [繁體中文](README.zh-Hant.md) · MIT**

Polymorphic labeling for EF Core. **One label, attachable to any type** — with a real foreign
key behind every single link.

Tested on SQLite / SQL Server / PostgreSQL with EF Core 8 and on SQLite with EF Core 9 / 10, and
validated on the production data of a real product ([QuillNest](https://cornhsu.com/quillnest)).

## Architecture

```mermaid
flowchart LR
    subgraph app["Your application"]
        direction TB
        note["Note : ILabelable&lt;Guid&gt;"]
        todo["TodoItem : ILabelable&lt;int&gt;"]
        reg["LabelRegistry<br/>r.Labelable&lt;T&gt;()<br/>key type inferred automatically"]
    end

    subgraph pkg["Cornhsu.Labeling"]
        direction TB
        store["ILabelStore<br/>CRUD · attach/detach · cross-type queries<br/>batch reads/writes · AND/OR · hierarchy"]
        analyzer["Roslyn analyzer<br/>unregistered → compile-time warning"]
    end

    subgraph db["Database (SQLite / SQL Server / PostgreSQL)"]
        direction TB
        label[("Label<br/>unique name · hierarchy · concurrency stamp")]
        link1[("LabelLink_Note<br/>real FK · cascade")]
        link2[("LabelLink_TodoItem<br/>real FK · cascade")]
    end

    note -.->|"compile-time check"| analyzer
    note --> reg
    todo --> reg
    reg -->|"ApplyLabelModel<br/>generates one link table per type"| db
    app -->|"attach / query"| store
    store --> label
    store --> link1
    store --> link2
```

## Why it exists

EF Core has no built-in polymorphic association. In "label A is attached to entity B", B might be
a `Note`, a `TodoItem` or a `CalendarEvent` — different tables, so everyone ends up hand-rolling
the same thing once per project.

**Before** — every new module means another hand-written join table and another copy of the queries:

```csharp
public class NoteLabel     { public int NoteId; public int LabelId; }
public class TodoItemLabel { public int TodoItemId; public int LabelId; }
// ...Nth module, Nth hand-written table, Nth duplicated Attach/Detach/Query code
```

**After** — one line of registration, the tables appear on their own, and every query goes
through `ILabelStore`:

```csharp
services.AddLabeling<AppDbContext>(r =>
{
    r.Labelable<Note>(n => n.Title);
    r.Labelable<TodoItem>(t => t.Content);
});
```

## Quick start

```
dotnet add package Cornhsu.Labeling.EntityFrameworkCore
```

**1. Implement `ILabelable<TKey>` on your entities.** No base class is required, `int` and `Guid`
(and other key types) all work, and you can mix them freely:

```csharp
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
```

**2. Register.** The key type is inferred, so there is no second type parameter to write:

```csharp
services.AddDbContext<AppDbContext>(o => o.UseSqlite(cs));
services.AddLabeling<AppDbContext>(r =>
{
    r.Labelable<Note>(n => n.Title);
    r.Labelable<TodoItem>(t => t.Content);
});
```

Each type's link table is named `LabelLink_` + its type key, and the type key defaults to the class
name — so renaming `Note` later renames its table too (the next migration drops `LabelLink_Note` and
creates an empty table under the new name). If the class name may change, pin the key at
registration: `r.Labelable<Note>(n => n.Title, typeKey: "Note")`. The prefix and the `Label` table
name are `r.LinkTablePrefix` and `r.LabelTableName`; set them inside the registration callback — they go
into the EF model, so they cannot be changed once `AddLabeling` has run and sealed the registry.

A generic entity type must always pin its key. The default is the CLR name, which for `Box<int>` is
``Box`1``: the table name gets a backtick, and every construction of `Box<…>` shares that key, so a
second one cannot be registered next to the first.

**3. One line in your DbContext:**

```csharp
public class AppDbContext : DbContext
{
    private readonly LabelRegistry _registry;
    public AppDbContext(DbContextOptions<AppDbContext> options, LabelRegistry registry)
        : base(options) => _registry = registry;

    protected override void OnModelCreating(ModelBuilder b) => b.ApplyLabelModel(_registry);
}
```

**4. Use it:**

```csharp
await store.AttachAsync(note, "paper", "urgent");            // missing labels are created automatically
var all   = await store.FindByLabelAsync("paper");           // cross-type, IReadOnlyList<LabelHit>
var notes = await store.QueryByLabelAsync<Note>("paper");    // strongly typed IQueryable<Note>

// Multi-label AND / OR:
var urgent = await store.FindByLabelsAsync(
    new[] { "paper", "urgent" }, LabelMatch.All);            // tagged paper AND urgent
var either = await store.QueryByLabelsAsync<Note>(
    new[] { "paper", "urgent" }, LabelMatch.Any);            // tagged paper OR urgent

// Reading the labels of 50 rows for a list view (one query, not 50):
var labelsByNote = await store.GetLabelsOfManyAsync(visibleNotes);
foreach (var n in visibleNotes)
    Render(n, labelsByNote[n]);                              // every entity has an entry (possibly empty)

// Bulk attach ("select several, tag them all urgent"; idempotent; all the links go in one
// SaveChanges, after any labels it has to auto-create, which are saved one at a time):
await store.AttachManyAsync(selectedNotes, new[] { "urgent" });

// Apps with a curated label set (labels carry colors/icons, created via an admin screen)
// can turn auto-creation off:
// r.AutoCreateLabels = false;   ← set it at registration
// AttachAsync then throws a clear exception for an unknown label instead of quietly
// creating a bare one.

// Cross-type hits can have different key types (Note uses Guid, TodoItem uses int),
// which is why LabelHit.EntityId is object. To get it typed:
var todoIds = all
    .Where(h => h.EntityClrType == typeof(TodoItem))
    .Select(h => h.EntityIdAs<int>());
```

A complete runnable example lives in [samples/MinimalConsole](samples/MinimalConsole/Program.cs).

## Managing labels

Labels can also be created up front, organized into a hierarchy, and maintained through the same
`ILabelStore`:

```csharp
// Curated labels: create them up front with a color, an icon and a parent
var work   = await store.CreateAsync("work", color: "#FF5722", icon: "briefcase");
var review = await store.CreateAsync("review", parentId: work.Id);   // a child of "work"
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

await store.DeleteAsync(review.Id);    // links on every type are removed by cascade;
                                       // throws while the label still has children
```

`UpdateAsync` changes only the label you pass it. Inside the lambda you may also move other labels
(through `Parent` or `Children`); changing another label's fields, adding a label, deleting or
detaching a label (the one being updated included), changing another label's tracking state, or
adding or removing a link throws `InvalidOperationException` and saves nothing — call `UpdateAsync`,
`RenameAsync`, `CreateAsync`, `DeleteAsync`, `AttachAsync` or `DetachAsync` instead. Changes the
lambda makes to your own entities are saved along with the update. When the update is rejected, the
store rolls back property values and entities added, removed or detached through the context — but not
changes made through your entities' navigation properties (adding to a collection or a many-to-many
relationship, or pointing a reference at a new entity), and nothing of yours at all when the context has
automatic change detection turned off.

## Without a DI container

Desktop apps that hold their services as singletons (WPF, WinForms) or small console tools can build
the store directly. The registry must be the same instance your DbContext passes to
`ApplyLabelModel`, and there must be only one per application:

```csharp
var registry = new LabelRegistry();
registry.Labelable<Note>(n => n.Title);
registry.Labelable<TodoItem>(t => t.Content);

using var db = new AppDbContext(options, registry);
var store = LabelStoreFactory.Create(db, registry);   // optional third argument: ILogger<ILabelStore>
```

The store does not own the DbContext; disposing it stays with you. `Create` seals the registry, as
`AddLabeling` does, which fixes what goes into the EF model: register every type and set
`LabelTableName` and `LinkTablePrefix` before the first call, because afterwards `Labelable` and changing
those two throw. `AutoCreateLabels` can still be changed at any time. Creating more stores over the same
registry (one per DbContext, say) is fine.

## Using EF Core migrations

`ApplyLabelModel` adds `Label` and one `LabelLink_*` table per type to your model, so
`dotnet ef migrations add` generates them (with full foreign keys) without any extra setup.

There is one detail worth knowing: your DbContext's constructor needs a `LabelRegistry`, and
`dotnet ef` can only resolve it from your DI container when your app builds a host (ASP.NET Core,
or the .NET Generic Host); otherwise you must supply an `IDesignTimeDbContextFactory`. **This
matters especially for non-ASP.NET applications** (WPF, console, and so on):

```csharp
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
```

## Extending Label

`Label` is deliberately minimal: only visual-identity fields (`Name`, `Color`, `Icon`, hierarchy
and ordering). That boundary is intentional — **fields carrying business meaning (label type,
module or tenant isolation, permissions…) are not the package's business**, because it cannot
understand them and cannot enforce them on your behalf.

When you need app-specific fields, use a **1:1 companion table** keyed on `Label.Id`, which keeps
the package's labeling capability separate from your business data:

```csharp
// Your companion table: the fields the package should not know about
public class LabelMeta
{
    public Guid LabelId { get; set; }          // 1:1 with the Cornhsu Label
    public Label Label { get; set; } = default!;
    public string LabelType { get; set; } = "tag";   // your business semantics
    public string? AllowedModule { get; set; }
}

// Your own DbContext configuration, alongside ApplyLabelModel
b.Entity<LabelMeta>(e =>
{
    e.HasKey(x => x.LabelId);
    e.HasOne(x => x.Label).WithOne()
     .HasForeignKey<LabelMeta>(x => x.LabelId)
     .OnDelete(DeleteBehavior.Cascade);        // deleting a label cleans up its metadata
});
```

> Why not "inherit from Label" instead? Because that forces the entire package to become generic
> and brings the invasiveness right back — the same reason the design trade-offs below reject
> "require your entities to share a base class". Minimal core plus a companion table costs nothing
> for people who do not need to extend, and leaves a clean path for people who do.

## Design trade-offs

"Label A is attached to entity B" has to be recorded in some table, but B is many different
tables. Three options:

### Option A: one table for everything (discriminator column)

```
LabelLink(LabelId, EntityType TEXT, EntityId GUID)
```

| | |
|---|---|
| ✅ | Cross-type queries are a single SQL statement |
| ✅ | Adding a module costs nothing |
| ❌ | **`EntityId` cannot have a foreign key** — the database has no idea which table it points at |
| ❌ | Delete a note and the link stays behind as an orphan; only application code can clean it up |
| ❌ | `EntityType` is a string, so renaming a class during a refactor breaks the data |

### Option B: one join table per type ← **this one**

```
LabelLink_Note(LabelId → Label.Id, EntityId → Note.Id)
LabelLink_TodoItem(LabelId → Label.Id, EntityId → TodoItem.Id)
```

| | |
|---|---|
| ✅ | **Real foreign keys**: the database enforces integrity and cascade delete cleans up automatically |
| ✅ | Type-safe; with the type key pinned at registration, renaming a class cannot break it |
| ❌ | A cross-type query has to union N tables |
| ❌ | A new module means a new table ← **the only pain point, and removing it is this package's whole value** |

### Option C: all entities inherit a shared base (TPH)

| | |
|---|---|
| ✅ | The cleanest model |
| ❌ | **Extremely invasive**: it forces users to change their existing inheritance |
| ❌ | C# has single inheritance — if your class already has a base class, there is simply no way out |

### The decision, and why

**Option B.** A generic `LabelLink<TEntity, TKey>` lets EF Core generate one table per registered type
(EF Core treats a closed generic type as its own entity), which automates away Option B's only
pain point.

> **You get Option B's referential integrity without paying Option B's manual cost.**

The performance cost of cross-type queries is real (N types = N queries), but it is
**measurable and optimizable**; losing referential integrity is **irreversible architectural
debt**. Trading performance for correctness is a good deal.

One more normalization dividend: a label's name is stored exactly once and every link points at
it by `LabelId`, so renaming a label is a single O(1) UPDATE with no cascade at all.

## Compile-time safety net (Roslyn analyzer)

The package ships an analyzer — installing it is all it takes. Two rules:

| Rule | When it fires | Why |
|---|---|---|
| `CHSU001` | A type implements `ILabelable<TKey>` but this compilation has no `r.Labelable<T>()` for it | Attaching or querying it at run time would throw "type is not registered" — this moves the error to compile time |
| `CHSU002` | A type implements only the non-generic `ILabelable` marker | Registration is guaranteed to throw, because the key type cannot be inferred |

Full details, examples and fixes: [docs/analyzer-rules.md](docs/analyzer-rules.md).

`CHSU001` is a false positive when the registration lives in another assembly or goes through a
generic helper (`r.Labelable<T>()` where `T` is a type parameter). Silence it with
`#pragma warning disable CHSU001` around the type, or an `.editorconfig` entry for the file that
declares it (`.editorconfig` severities apply per file, not per type).

## Performance

A naive benchmark (SQLite file database, 5 registered types × 10,000 entities, roughly 120,000
links, medians on an ordinary desktop; the harness is in
[samples/Benchmark](samples/Benchmark/Program.cs)):

| Operation | Median |
|---|---|
| `FindByLabelAsync` (10% hit rate = 5,000 rows across 5 types) | ~21 ms |
| `FindByLabelAsync` (0.1% hit rate = 50 rows) | ~1 ms |
| `FindByLabelAsync` (including descendants, 500 rows) | ~3 ms |
| `QueryByLabelAsync<T>` + `CountAsync` | ~1 ms |
| 50 individual `GetLabelsOfAsync` calls (the N+1 anti-pattern) | ~5 ms |
| `GetLabelsOfManyAsync` (50 rows in one query) | ~1 ms |
| `QueryByLabelsAsync<T>` (All / Any, 2 labels) | ~2 ms |

Conclusion: the naive "one query per type" strategy for cross-type queries is more than adequate
at this scale, so the package does not merge them. For list views use `GetLabelsOfManyAsync` rather
than calling `GetLabelsOfAsync` in a loop — it is 5× faster on local SQLite, and on a database
with network latency the difference is 49 round trips.

The name-based queries match all the names in one query (a `UNION ALL` with one branch per name,
up to 100 names per query), plus one read of the hierarchy when descendants are included, so the
database's collation decides what matches. Against 1.1.0, which read every label in one query and
matched names in memory, the benchmark shows no difference beyond noise, and 50 or 100 names on
LocalDB take about 7 and 9 ms, as in 1.1.0.

`UpdateAsync` looks at everything the DbContext is tracking, twice per call: once before your lambda
runs (a full change detection, when it is on, plus a note of each entry's state), so it can roll the
changes back if the update is rejected or the save fails, and once after, so it can tell what the
lambda changed and check a parent changed through the `Parent` or `Children` navigations for cycles.
With 1,000 tracked labels and 10,000 other tracked entities a call takes about 5–7 ms (a rejected
one about 8–12 ms), against about 2.5 ms in 1.1.0. A context that tracks few entities — for example
one that reads its lists with `AsNoTracking()` — does not notice the difference.

## Limitations

- **Entity keys may be `int`/`long`/`Guid`/`string` or any type with equality, and you may mix
  them within one application**; `Label`'s own key is always a `Guid` (it is the package's table).
- **`LabelHit.EntityId` is an `object`** — a cross-type query can return hits with different key
  types, which is the unavoidable cost of generic keys. Use `EntityIdAs<TKey>()` to get it typed.
- **A cross-type query is N queries** (N = number of registered types). The naive version ships
  first; it will be optimized into a `UNION ALL` if measurement ever shows a bottleneck.
- **`LabelRegistry` must be an application-wide singleton.** EF Core's model cache is keyed by
  DbContext type, so handing the same DbContext type a different registry gives you a wrongly
  cached model — silently, with no error. `AddLabeling` already registers it as a Singleton.
  Multi-tenant setups where each tenant has different labelable types are not supported
  (that needs a custom `IModelCacheKeyFactory`).
- **Label names are globally unique, including across the hierarchy** — "Work/Misc" and
  "Life/Misc" cannot both have a child named "Misc". This is a deliberate trade-off: the entire
  API addresses labels by name (`AttachAsync` and `FindByLabelAsync` both take name strings), and
  allowing duplicates under different parents would make every name-addressed call ambiguous.
  When you need that structure, put the qualifier in the name itself (e.g. "Life · Misc").
  Supporting per-parent uniqueness in future would necessarily come with a path-addressing API
  (`"Life/Misc"`) — a breaking change for some future major version.
- **Label names are trimmed of surrounding whitespace** (consistently, at every entry point).
  Case sensitivity is left to the database collation (SQLite is case-sensitive by default,
  SQL Server is not), for every method that takes a name. A collation decides more than case: SQL
  Server's, even the case-sensitive `_CS_AS` ones, also treat full- and half-width forms, `ß` and `ss`,
  ligatures and the two kana scripts as equal and ignore trailing spaces. For exact matching, give
  `Label.Name` a binary collation after `ApplyLabelModel` — `_BIN2` on SQL Server, `"C"` on
  PostgreSQL, SQLite's default `BINARY` — with
  `b.Entity<Label>().Property(l => l.Name).UseCollation(…)`; changing it takes a migration.
- **Get-or-create has a race**: two paths creating the same name concurrently will collide on the
  unique index, which is handled by re-reading and adopting the existing label. If the re-read
  shows it was not a same-name race, the original exception propagates.
- **Concurrent modification protection**: `Label.ConcurrencyStamp` is a concurrency token, rotated
  on every modification through the store. It does not rely on any database feature, so behaviour
  is identical across providers. When two callers modify the same label, the one that saves second
  gets a `DbUpdateConcurrencyException` instead of silently overwriting — catch it, then re-read
  and retry or prompt the user. Before the exception reaches you, the store reloads the labels it was
  writing from the database — the one being updated, and any label the `UpdateAsync` lambda moved —
  so re-reading through the same store (and context) returns the database's values and a retry can
  succeed. That reload also drops any changes you had made to those labels yourself and not saved yet;
  if the conflict is on something else you had changed yourself, the store leaves it alone.
- **Tested against SQLite, SQL Server and PostgreSQL** (the same test suite, on every CI run, with
  EF Core 8; the EF Core 9 and 10 runs use SQLite only). Note that name case semantics vary with
  collation.
- **`ILabelStore`'s write methods call your DbContext's `SaveChangesAsync`** — any other pending
  changes in that same context get committed along with them. That is the inherent trade-off of
  sharing a DbContext with the application; if you need isolation, call the store from a separate
  DbContext scope. When a write method throws — a rejected update or a failed save — it first takes
  back the changes it made to the context, so they are not saved later by accident and the context
  stays usable. Your own pending changes are left as they were, with one exception: after a
  concurrency conflict, the label being written is reloaded (see "Concurrent modification protection" above).
  Inside a transaction of your own (`BeginTransaction`, or a `TransactionScope`), a failed write is
  taken back even if EF had already accepted it, because you may still roll back: the labels and links
  it touched return to their state before the call, original values and concurrency stamps included,
  so after a rollback a retry through the same store works. Changes of your own that EF accepted in
  that same save are not taken back; if the context carried any, discard it after rolling back, as
  EF Core recommends.
- **EF Core 8+ only.** The dependency floor is 8.0.11 (the 8.0-series version with known
  vulnerability advisories patched); consumers on EF Core 9 or 10 unify automatically.
- For tests, use **SQLite in-memory rather than the EF InMemory provider** — the latter does not
  enforce foreign keys, so it cannot test this package's central guarantee.

## See also

[**EF Core 的多型標籤要怎麼做**](https://cornhsu.com/articles/ef-core-polymorphic-tagging)
(in Traditional Chinese) — background on the category rather than this package: why a relational
database cannot constrain a generic foreign key in the first place, the four workable
architectures and what each one actually costs, how to choose between them, and four mistakes
that are expensive to undo. It recommends this package's approach, but is written to be useful
to someone who never installs it — the hand-rolled version is about a hundred lines.

## License

MIT
