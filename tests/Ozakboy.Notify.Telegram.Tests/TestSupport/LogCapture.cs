using Microsoft.Extensions.Logging;

namespace Ozakboy.Notify.Telegram.Tests.TestSupport;

/// <summary>
/// 收集日誌的每一個表面:格式化後的訊息、結構化參數的值、例外的完整文字,以及日誌範圍(scope)。
/// Collects every surface of logging: the formatted message, the values of structured arguments, the full text
/// of the exception, and logging scopes.
/// </summary>
/// <remarks>
/// 只收格式化訊息是不夠的:<c>IHttpClientFactory</c> 的 <c>LogicalHandler</c> 把位址放在 scope 裡,
/// 而多數日誌框架會把例外物件的 <c>ToString()</c> 與結構化參數原樣寫出去。外洩測試要看的是日誌框架拿得到的全部東西。
/// Capturing only the formatted message is not enough: <c>IHttpClientFactory</c>'s <c>LogicalHandler</c> puts
/// the URI in a scope, and most logging frameworks write the exception's <c>ToString()</c> and the structured
/// arguments verbatim. A leak test has to look at everything a logging framework can reach.
/// </remarks>
internal sealed class LogCapture
{
    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// 每一筆紀錄的所有表面,各自成為一段文字。
    /// Every surface of every entry, each as a piece of text.
    /// </summary>
    public IEnumerable<string> Surfaces => Entries.SelectMany(entry => entry.Surfaces);

    public void Add(Entry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    internal sealed record Entry(string Category, LogLevel Level, int EventId, string Message, IReadOnlyList<string> Surfaces);
}

/// <summary>
/// 寫入 <see cref="LogCapture"/> 的日誌器。
/// A logger writing into a <see cref="LogCapture"/>.
/// </summary>
internal class CapturingLogger : ILogger
{
    private readonly LogCapture _capture;
    private readonly string _category;

    public CapturingLogger(LogCapture capture, string category)
    {
        _capture = capture;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        var surfaces = new List<string> { state.ToString() ?? string.Empty };
        surfaces.AddRange(StructuredValues(state));
        _capture.Add(new LogCapture.Entry(_category, LogLevel.None, 0, "scope", surfaces));
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        var surfaces = new List<string> { message };
        surfaces.AddRange(StructuredValues(state));
        if (exception is not null)
        {
            surfaces.Add(exception.ToString());
        }

        _capture.Add(new LogCapture.Entry(_category, logLevel, eventId.Id, message, surfaces));
    }

    private static IEnumerable<string> StructuredValues<TState>(TState state) =>
        state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Select(pair => $"{pair.Key}={pair.Value}")
            : [];
}

/// <summary>
/// 泛型版本,可直接注入需要 <see cref="ILogger{TCategoryName}"/> 的建構式。
/// The generic flavour, injectable wherever an <see cref="ILogger{TCategoryName}"/> is required.
/// </summary>
internal sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>
{
    public CapturingLogger(LogCapture capture)
        : base(capture, typeof(T).FullName ?? typeof(T).Name)
    {
    }
}

/// <summary>
/// 讓整個相依性注入容器(包括 <c>IHttpClientFactory</c> 的預設日誌)都寫進同一個 <see cref="LogCapture"/>。
/// Routes the whole dependency-injection container — <c>IHttpClientFactory</c>'s default logging included —
/// into one <see cref="LogCapture"/>.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly LogCapture _capture;

    public CapturingLoggerProvider(LogCapture capture)
    {
        _capture = capture;
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_capture, categoryName);

    public void Dispose()
    {
    }
}
