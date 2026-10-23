using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// log:自動建立標籤寫一筆 Information(提醒策展式 App 關掉 AutoCreateLabels),建立/刪除寫 Debug。
/// DI 與 LabelStoreFactory 兩條路徑都要接得到。log 與 provider 無關,固定用 SQLite。
/// </summary>
public class LoggingTests
{
    private const string AutoCreated =
        "auto-created label '自動' while attaching (AutoCreateLabels is on; curated-label apps should set it to false)";

    [Fact]
    public async Task DI路徑_自動建立寫一筆Information_建立刪除寫Debug_category是store的型別()
    {
        var capture = new CapturingLoggerProvider();
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        services.AddDbContext<TestDbContext>(o => o.UseSqlite(conn).EnableServiceProviderCaching(false));
        services.AddLabeling<TestDbContext>(r => r.Labelable<TestNote>(n => n.Title));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        context.Database.EnsureCreated();
        var store = scope.ServiceProvider.GetRequiredService<ILabelStore>();
        var a = new TestNote { Id = Guid.NewGuid(), Title = "a" };
        var b = new TestNote { Id = Guid.NewGuid(), Title = "b" };
        context.Notes.AddRange(a, b);
        await context.SaveChangesAsync();

        await store.AttachAsync(a, "自動");
        await store.AttachAsync(b, "自動");   // 已存在:不再記「自動建立」
        var manual = await store.CreateAsync("手動");
        await store.DeleteAsync(manual.Id);

        // EF 本身也寫進同一個 logger factory,只看本套件的 category
        var ours = capture.Entries.Where(e => e.Category.StartsWith("Cornhsu.Labeling", StringComparison.Ordinal)).ToList();
        ours.Select(e => e.Category).Distinct().Should().Equal("Cornhsu.Labeling.EntityFrameworkCore.EfLabelStore");
        ours.Should().ContainSingle(e => e.Level == LogLevel.Information).Which.Message.Should().Be(AutoCreated);
        ours.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message).Should().Equal(
            $"created label '手動' (Id: {manual.Id})",
            $"deleted label '手動' (Id: {manual.Id})");
    }

    [Fact]
    public async Task Factory路徑_傳入的logger收到log_category是呼叫端的()
    {
        var capture = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var registry = new LabelRegistry();
        registry.Labelable<TestNote>(n => n.Title);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(conn).EnableServiceProviderCaching(false).Options;
        using var context = new TestDbContext(options, registry);
        context.Database.EnsureCreated();
        var note = new TestNote { Id = Guid.NewGuid(), Title = "n" };
        context.Notes.Add(note);
        await context.SaveChangesAsync();

        var store = LabelStoreFactory.Create(context, registry, loggerFactory.CreateLogger<ILabelStore>());
        await store.AttachAsync(note, "自動");

        capture.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
            .Which.Should().Be(new LogEntry("Cornhsu.Labeling.ILabelStore", LogLevel.Information, AutoCreated));
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    /// <summary>把所有 log 收進記憶體的 provider。</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose() { }

        private void Add(LogEntry entry)
        {
            lock (_entries) _entries.Add(entry);
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly CapturingLoggerProvider _owner;

            public CapturingLogger(string category, CapturingLoggerProvider owner) => (_category, _owner) = (category, owner);

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _owner.Add(new LogEntry(_category, logLevel, formatter(state, exception)));
        }
    }
}
