# Analyzer rules

`Cornhsu.Labeling` ships a Roslyn analyzer inside the package — installing the package is all it takes
to enable these rules. Both are warnings, both are reported at compilation end (they need to see the
whole compilation before they can conclude that a registration is missing).

## CHSU001 — Labelable type is not registered

A type implements `ILabelable<TKey>`, but this compilation contains no `r.Labelable<T>()` call for it.

```csharp
public class Note : ILabelable<int>   // ⚠ CHSU001
{
    public int Id { get; set; }
}

services.AddLabeling<AppDbContext>(r =>
{
    // r.Labelable<Note>() is missing
});
```

**Why it matters.** Nothing fails at build time, but the first `AttachAsync(note, "urgent")` throws
`InvalidOperationException: type Note is not registered.` — usually in front of a user, at run time.

**Fix.** Register the type where you configure the labeling system:

```csharp
services.AddLabeling<AppDbContext>(r =>
{
    r.Labelable<Note>(n => n.Title);
});
```

**When to suppress.** The analyzer only sees one compilation at a time. If your registration lives in
another assembly (a composition-root project, for instance), the warning is a false positive — suppress
it with `#pragma warning disable CHSU001`, a `[SuppressMessage]` attribute, or a `.editorconfig` entry:

```ini
[*.cs]
dotnet_diagnostic.CHSU001.severity = none
```

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
