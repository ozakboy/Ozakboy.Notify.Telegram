namespace Ozakboy.Notify.Telegram.Tests.TestSupport;

/// <summary>
/// 測試用的固定值。權杖是假的,只用來辨識它有沒有出現在不該出現的地方。
/// Fixed values for tests. The token is fake and exists only to be spotted where it should not appear.
/// </summary>
internal static class TestValues
{
    public const string BotToken = "7000000001:AAFakeTokenForUnitTestsOnly_0123456789ab";

    /// <summary>
    /// 權杖冒號後的祕密段。外洩斷言一律以它為準:它不會因為冒號被編碼而躲過比對。
    /// The secret half after the colon. Leak assertions key on it, since encoding the colon cannot hide it.
    /// </summary>
    public const string TokenSecretPart = "AAFakeTokenForUnitTestsOnly_0123456789ab";

    public const string ChatId = "-1001234567890";

    public const string SendMessageUri = "https://api.telegram.org/bot" + BotToken + "/sendMessage";

    /// <summary>
    /// 單元測試的預設設定:逾時與節流等待拉長,避免假時鐘推進時誤觸;重試退避固定、無抖動。
    /// Default options for unit tests: long timeouts and throttling waits, so advancing the fake clock does not
    /// trip them by accident; fixed retry backoff with no jitter.
    /// </summary>
    public static TelegramNotifierOptions Options(Action<TelegramNotifierOptions>? configure = null)
    {
        var options = new TelegramNotifierOptions
        {
            BotToken = BotToken,
            ChatId = ChatId,
            RequestTimeout = TimeSpan.FromMinutes(10),
            ThrottleWaitLimit = TimeSpan.FromMinutes(10),
            TransientRetry = new RetryPolicy
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterRatio = 0d,
            },
        };

        configure?.Invoke(options);
        return options;
    }
}

/// <summary>
/// 一個通知器加上它的假時鐘、假處理器與日誌收集器。
/// A notifier together with its fake clock, stub handler, and log capture.
/// </summary>
internal sealed class NotifierHarness : IDisposable
{
    private readonly HttpClient _client;

    public NotifierHarness(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder,
        Action<TelegramNotifierOptions>? configure = null)
    {
        Clock = new FakeTimeProvider();
        Handler = new ScriptedTelegramHandler(responder, Clock);
        Logs = new LogCapture();
        _client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };

        // 節流的時間窗邊界固定在通知器建立的那一刻起算(限流器在建構式裡就建好了),之後每 MinimumInterval 一個。
        // 記下這一刻,測試才問得出「兩則落在同一個窗還是不同窗」。
        // The throttling windows are anchored at the moment the notifier is created — the limiter is built in its
        // constructor — and recur every MinimumInterval. Recording that instant is what lets a test ask whether two
        // messages fell in the same window or different ones.
        StartedAt = Clock.GetUtcNow();
        Notifier = new TelegramNotifier(_client, TestValues.Options(configure), new CapturingLogger<TelegramNotifier>(Logs), Clock);
    }

    public FakeTimeProvider Clock { get; }

    /// <summary>
    /// 通知器建立的假時鐘時刻,也就是節流時間窗的起算點。
    /// The fake-clock instant the notifier was created at, which is where the throttling windows start.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    public ScriptedTelegramHandler Handler { get; }

    public LogCapture Logs { get; }

    public TelegramNotifier Notifier { get; }

    public Task<Result> SendAsync(NotificationLevel level, string text, CancellationToken cancellationToken = default) =>
        FakeClockRunner.RunAsync(Clock, Notifier.SendAsync(new TelegramNotification(level, text), cancellationToken));

    /// <summary>
    /// 以指定的假時鐘步長送出。要跨越長時間窗(例如一分鐘的配額)的測試用較大的步長,免得多花真實時間。
    /// Sends with a given fake-clock step. Tests that must cross a long window (a minute's quota, say) use a
    /// larger step to avoid spending real time.
    /// </summary>
    public Task<Result> SendAsync(NotificationLevel level, string text, TimeSpan clockStep) =>
        FakeClockRunner.RunAsync(Clock, Notifier.SendAsync(new TelegramNotification(level, text)), clockStep);

    public void Dispose()
    {
        Notifier.Dispose();
        _client.Dispose();
    }
}
