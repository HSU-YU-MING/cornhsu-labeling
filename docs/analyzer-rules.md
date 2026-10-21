# Analyzer rules

`Cornhsu.Labeling` ships a Roslyn analyzer inside the package — installing the package is all it takes
to enable these rules. Both are warnings, both are reported at compilation end (they need to see the
whole compilation before they can conclude that a registration is missing).

<a id="chsu001"></a>

## CHSU001 — Labelable type is not registered

A type implements `ILabelable<TKey>`, but this compilation contains no `r.Labelable<T>()` call for it.

```csharp
public class Note : ILabelable<int>   // ⚠ CHSU001
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

services.AddLabeling<AppDbContext>(r =>
{
    // r.Labelable<Note>() is missing
});
```

**Why it matters.** Nothing fails at build time, but the first `AttachAsync(note, "urgent")` throws
`InvalidOperationException: type Note is not registered. Call r.Labelable<Note>() inside AddLabeling.`
— usually in front of a user, at run time.

**Fix.** Register the type where you configure the labeling system:

```csharp
services.AddLabeling<AppDbContext>(r =>
{
    r.Labelable<Note>(n => n.Title);
});
```

**When to suppress.** The analyzer only sees one compilation at a time, and only calls that name the
type directly. The warning is a false positive when:

- the registration lives in another assembly (a composition-root project, for instance);
- the registration goes through a generic helper such as
  `static void Reg<T>(LabelRegistry r) where T : class, ILabelable => r.Labelable<T>()` (the analyzer
  cannot tell which types reach `T`);
- a generic entity is registered by a closed type: declaring `class Box<T> : ILabelable<int>` and
  registering `r.Labelable<Box<string>>()` still reports `Type 'Box'`, because the analyzer compares
  the registered `Box<string>` with the declared `Box<T>`.

Suppress it with `#pragma warning disable CHSU001` around the type, a
`[SuppressMessage("Cornhsu.Labeling", "CHSU001")]` attribute on it, or a `.editorconfig` entry.
`.editorconfig` severities apply to files, not types, so scope the section to the file that declares
the type:

```ini
[Models/Note.cs]
dotnet_diagnostic.CHSU001.severity = none
```

A `[*.cs]` section would turn the rule off for every type in the project.

<a id="chsu002"></a>

## CHSU002 — Only the non-generic ILabelable marker is implemented

A type implements the bare `ILabelable` marker rather than `ILabelable<TKey>`.

```csharp
public class Memo : ILabelable   // ⚠ CHSU002
{
    public Guid Id { get; set; }
}
```

**Why it matters.** `ILabelable` exists so the public API needs only one type parameter — the key type is
inferred from `ILabelable<TKey>`. With only the marker there is nothing to infer from, so registration
throws immediately.

**Fix.** State the key type:

```csharp
public class Memo : ILabelable<Guid>
{
    public Guid Id { get; set; }
}
```

There is no legitimate reason to suppress CHSU002 — a type in this state can never be registered successfully.
