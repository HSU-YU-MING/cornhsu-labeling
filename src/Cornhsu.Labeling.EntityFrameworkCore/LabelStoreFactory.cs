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
    /// Like <see cref="ServiceCollectionExtensions.AddLabeling{TContext}"/>, this seals the registry, which fixes what goes
    /// into the EF model: register every labelable type and set <see cref="LabelRegistry.LabelTableName"/> and
    /// <see cref="LabelRegistry.LinkTablePrefix"/> before the first call, because afterwards
    /// <see cref="LabelRegistry.Labelable{TEntity}"/> and changing those two throw <see cref="InvalidOperationException"/>.
    /// <see cref="LabelRegistry.AutoCreateLabels"/> stays changeable. Creating more stores over the same registry is fine.
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

        // 跟 AddLabeling 一樣封存:store 建好之後再註冊的型別,EF 已經建好(或之後會沿用快取)的 model 裡不會有,
        // 要到貼標或查詢時才以缺表之類的資料庫錯誤冒出來。在這裡擋下,錯誤出現在寫錯的那一行
        registry.Seal();
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
