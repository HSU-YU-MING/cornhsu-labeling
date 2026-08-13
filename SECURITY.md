# Security

## What this package is, and what that means

Cornhsu.Labeling is a **library that runs inside your application**, not a tool you point at
untrusted input. It makes no network requests, reads no configuration files, holds no
credentials, and has no server, daemon or persistent state of its own. Everything it touches,
it touches through the `DbContext` you handed it.

That removes most of the usual threat surface and leaves three questions actually worth asking.

### Can a label name reach your SQL?

No. Label names are end-user input in most applications ("let people type their own tags"),
and they are only ever used as **parameterized values** through EF Core's LINQ provider.
There is no raw SQL anywhere in this package — no `FromSql`, no `ExecuteSqlRaw`, no string
concatenation into a query.

Table names *are* built by string concatenation (`LinkTablePrefix` + type key, giving
`LabelLink_Note`), but both halves come from **your registration code at startup** — the CLR
type name, or the `typeKey` you pin explicitly — never from runtime input. A user cannot type
a label name that becomes part of a table name.

Label names are length-checked in code rather than left to the database, because SQLite does
not enforce `HasMaxLength` and a package that only validated on SQL Server would let
oversized names through on SQLite. The limit is `Label.MaxNameLength` (64), and the exception
deliberately echoes only the first 16 characters of the offending name.

If you find a way to make a label name, display-name projection, or type key alter the shape
of a generated query or reach the database as anything other than a parameter, that is a
vulnerability. Please report it.

### Labels are global — isolation is your job

`Label.Name` is globally unique, and `FindByLabelAsync` / `FindByLabelsAsync` search **every
registered type** with no filter beyond the label itself. In a multi-tenant or
per-permission application this is not isolation, and it was never meant to be: the README
says fields carrying business meaning — tenant, module, permissions — are not the package's
business, because it cannot understand or enforce them for you.

So if two tenants share one database and one `LabelRegistry`, tenant A's query will return
tenant B's rows. **That is the documented design, not a bug.** Scope it yourself — a query
filter on your entities, a companion table, or separate databases. It is listed here rather
than only in the README because "labels are shared" is exactly the kind of assumption people
make in the other direction.

### Label names end up in your logs

`ILabelStore` logs label names: at Debug when a label is created or deleted, and at
**Information** when a label is auto-created during attach. If your users' label names can
contain personal data, those names reach whatever log sink your application configured, at a
level most deployments keep enabled. Set `LabelRegistry.AutoCreateLabels = false` if you want
attach to refuse unknown labels instead, or filter this package's category in your logging
configuration.

### The analyzer runs in your build

`Cornhsu.Labeling.Analyzers` is a Roslyn analyzer, so it executes inside your compiler
process. It only inspects syntax and symbols and reports diagnostics; it writes no files and
makes no network calls. An analyzer that could be made to do either would be a vulnerability.

## What is *not* a security issue

A wrong query result, a missing link row, an orphaned label, or an analyzer false positive is
a **correctness bug**, however badly it behaves. Please open a normal issue for those — they
are the reports this project most wants, and they belong in the open.

Cross-tenant visibility with a single shared registry is the documented design described
above, not a vulnerability.

## Reporting a vulnerability

Use GitHub's private reporting: **Security → Report a vulnerability** on this repository.
That opens a channel only you and the maintainer can see.

Please don't open a public issue for a vulnerability. Everything else in this project is
discussed in the open, but a working exploit against a library that sits next to people's
databases deserves a fix released before it is described publicly.

This is a single-maintainer project, so expect a human response in days rather than hours.
Tell me what you found, how to reproduce it, and what it lets an attacker do; if you have a
suggested fix, even better. I will credit you in the release notes unless you'd rather I
didn't.

## Supported versions

**The latest release only.** Fixes ship forward, not as patches to older versions. No version
number is written on this page on purpose — the git tag is the single source of truth for
what a version is, and a number repeated here would eventually contradict it. See
[RELEASING.md](RELEASING.md) for what major / minor / patch mean for this package.

## Supply chain

- Published to NuGet via **OIDC Trusted Publishing** — no long-lived API key exists in this
  repository or in GitHub secrets, so there is no publishing credential to steal.
- Builds are deterministic, with SourceLink and symbol packages, so a published binary can be
  traced back to the commit that produced it.
- The version number is derived from the git tag at release time and from nowhere else.
- Dependencies are watched by Dependabot.
- **NuGet audit warnings (NU1901–NU1904) are deliberately not escalated to errors** in
  `Directory.Build.props`. This is not "vulnerability reports ignored": package references
  here are pinned to the **lowest** version this package supports (EF Core 8.0.x), because
  raising that floor is a breaking change for consumers still on EF 8. Your application
  unifies to whatever patched version *you* reference, and that is the version that actually
  runs. If you want the audit to fail your build, that is the right place for it — your
  build, not ours.
