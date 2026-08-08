using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// The front door for applications with no DI container (the singleton-service shape common in
/// WPF/WinForms) to construct an <see cref="ILabelStore"/> directly. When you do have a container,
/// prefer <see cref="ServiceCollectionExtensions.AddLabeling{TContext}"/>.
/// </summary>
public static class LabelStoreFactory
{
    /// <summary>
    /// Creates an <see cref="ILabelStore"/> over the given DbContext and registry.
    /// The returned store does not own the context — its lifetime and disposal stay with the caller.
    /// </summary>
    /// <typeparam name="TContext">Your application's DbContext type.</typeparam>
    /// <param name="context">
    /// The backing DbContext; its OnModelCreating must have called ApplyLabelModel with this same
    /// <paramref name="registry"/>.
    /// </param>
    /// <param name="registry">
    /// The registry of labelable types. It must be the **same instance** used to build the model,
    /// and there must be exactly one per application (EF's model cache is keyed by DbContext type).
    /// </param>
    /// <param name="logger">Optional logger; null means no logging (NullLogger).</param>
    public static ILabelStore Create<TContext>(TContext context, LabelRegistry registry,
        ILogger<ILabelStore>? logger = null)
        where TContext : DbContext
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        return new EfLabelStore<TContext>(context, registry, logger is null ? null : new LoggerAdapter<TContext>(logger));
    }

    /// <summary>把呼叫端的 ILogger&lt;ILabelStore&gt; 轉成內部型別的 logger(內部型別不公開)。</summary>
    private sealed class LoggerAdapter<TContext> : ILogger<EfLabelStore<TContext>> where TContext : DbContext
    {
        private readonly ILogger _inner;
        public LoggerAdapter(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
