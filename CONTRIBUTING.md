# Contributing

This project is maintained by one person. The most useful thing you can send is not a pull
request — it is **a report from a schema and a database I have never seen**.

The library was validated on the production data of one real product, and its CI covers three
databases and three EF Core versions. Your key types, your provider's collation and your migration
history are a sample I don't have.

## The most valuable reports

**A wrong result** — a query that returns the wrong entities, a link row that is missing or left
behind, a label that attaches to the wrong thing. The [bug form](https://github.com/HSU-YU-MING/cornhsu-labeling/issues/new?template=1-bug-report.yml)
asks for the three things that decide whether it is reproducible — **EF Core version, database
provider and primary key type** — because they are exactly the axes the CI matrix covers. A report
that names all three can usually be turned into a failing test the same day.

**An analyzer false positive** — `CHSU001` or `CHSU002` on a type that is fine, or silence on one
that throws "type is not registered" at run time. A false positive trains people to disable the
analyzer, which defeats its purpose. Use the [analyzer form](https://github.com/HSU-YU-MING/cornhsu-labeling/issues/new?template=2-analyzer.yml).

**Write in English or 中文, whichever is easier.**

**One exception: security.** Something an attacker could use should be reported privately — see
[SECURITY.md](SECURITY.md). Note that cross-tenant visibility with a single shared registry is the
documented design, not a vulnerability; a wrong query result is a correctness bug, however badly it
behaves, and belongs in the open.

### Before you open one

Check [Limitations](README.md#limitations) and [Design trade-offs](README.md#design-trade-offs).
Cross-type queries being N queries, label names being global, and the registry needing to be a
singleton are deliberate. A report about one is still welcome — it tells me the trade-off hurts in
practice — just say so.

## If you want to send code

Every change needs a test; a bug fix gets a test that fails before the change. Behaviour that
depends on the provider should be checked on all three:

```bash
dotnet test
CORNHSU_TEST_PROVIDER=sqlserver dotnet test tests/Cornhsu.Labeling.Tests
CORNHSU_TEST_PROVIDER=postgres  dotnet test tests/Cornhsu.Labeling.Tests
pwsh scripts/verify-readme-snippets.ps1
```

SQL Server uses `(localdb)\MSSQLLocalDB` and PostgreSQL a local instance on port 5432 by default;
CI runs all three, so if you can only run SQLite locally, say so in the pull request.

The public API is frozen within a major version. A change that breaks it — including a change to
`ILabelStore` — waits for the next major; new operations go through optional interfaces and
extension methods, as 2.1 did.
