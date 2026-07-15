using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 給「沒有 DI 容器」的應用程式(WPF/WinForms 常見的 singleton 服務架構)直接建立
/// <see cref="ILabelStore"/> 的正門。有 DI 容器時請優先使用
/// <see cref="ServiceCollectionExtensions.AddLabeling{TContext}"/>。
/// </summary>
public static class LabelStoreFactory
{
    /// <summary>
    /// 以指定的 DbContext 與 registry 建立 <see cref="ILabelStore"/>。
    /// 回傳的 store 不擁有 context——生命週期與釋放由呼叫端的 context 決定。
    /// </summary>
    /// <typeparam name="TContext">應用程式的 DbContext 型別。</typeparam>
    /// <param name="context">
    /// 後端 DbContext;其 OnModelCreating 必須以同一個 <paramref name="registry"/>
    /// 呼叫 ApplyLabelModel。
    /// </param>
    /// <param name="registry">
    /// 可標記型別的註冊表。必須與建立 model 用的是**同一個實例**,
    /// 且全 App 只能有一份(EF 的 model cache 以 DbContext 型別為 key)。
    /// </param>
    /// <param name="logger">可選的 logger;null 表示不記錄(NullLogger)。</param>
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
