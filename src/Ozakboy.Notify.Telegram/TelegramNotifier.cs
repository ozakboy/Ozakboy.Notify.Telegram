using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ozakboy.Core.Abstractions;
using Ozakboy.Http;
using Ozakboy.Http.RateLimiting;
using Ozakboy.Notify.Telegram.Internal;
using Ozakboy.Security.Masking;

namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 透過 Telegram Bot API 的 <c>sendMessage</c> 送出分級通知:節流、429 等待後重送、暫時性失敗重試,且權杖絕不外流。
/// Sends levelled notifications through the Telegram Bot API's <c>sendMessage</c>: throttled, resent after a
/// 429 wait, retried on transient failures, and never leaking the token.
/// </summary>
/// <remarks>
/// <para>
/// <b>權杖在網址路徑裡。</b>Bot API 的位址是 <c>https://api.telegram.org/bot&lt;TOKEN&gt;/sendMessage</c>,沒有
/// 標頭或本體可以替代。任何會輸出請求位址的地方,都會把權杖一起輸出。欄位名規則看不到路徑的一段,
/// 攔得住它的只有「已登記祕密的字面替換」。本套件查過的輸出點與對策如下:
/// <b>The token is in the URL path.</b> The Bot API address is
/// <c>https://api.telegram.org/bot&lt;TOKEN&gt;/sendMessage</c>, and no header or body alternative exists. Anything
/// that prints the request URI prints the token with it. Name-based masking cannot see a path segment; only literal
/// replacement of a registered secret catches it. The output points this package examined, and what it does about
/// each:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>IHttpClientFactory</c> 預設掛上的 <c>LogicalHandler</c> / <c>ClientHandler</c> 日誌會在 Information 層級寫出
/// 完整位址(.NET 只遮 query,不遮路徑),<c>LogicalHandler</c> 還會把位址放進日誌範圍(scope)。
/// → 具名用戶端以 <c>AddOzakboyHttpPipeline</c> 註冊,它會移除這些預設日誌。
/// <c>IHttpClientFactory</c>'s default <c>LogicalHandler</c> / <c>ClientHandler</c> logging writes the full URI at
/// Information level (.NET redacts the query, not the path), and <c>LogicalHandler</c> also puts it in a logging
/// scope. → The named client is registered with <c>AddOzakboyHttpPipeline</c>, which removes that default logging.
/// </description></item>
/// <item><description>
/// 管線自己的 <c>SanitizingLoggingHandler</c> 會記錄位址,失敗時也記錄例外。
/// → 權杖(完整權杖與冒號後的祕密段)以 <c>HttpPipelineOptions.KnownSecrets</c> 登記在這個用戶端的遮罩器,
/// 它記下的位址與例外文字都經過字面替換,交給日誌器的例外是 <see cref="SanitizedException"/> 而不是原始物件。
/// 請求與回應本體的日誌保持關閉(本體裡是 chat id 與訊息內容)。本類別自己的日誌只記等級、分段、錯誤代碼與耗時,不記位址。
/// The pipeline's own <c>SanitizingLoggingHandler</c> logs the URI, and the exception on failure. → The token (the
/// full token and the secret half after the colon) is registered on this client's masker through
/// <c>HttpPipelineOptions.KnownSecrets</c>, so the URI and exception text it records go through literal
/// replacement, and the exception it hands the logger is a <see cref="SanitizedException"/>, not the original.
/// Request and response body logging stays off (the body holds the chat id and the message text). This class's own
/// log lines carry only the level, part, error code, and timing — never the URI.
/// </description></item>
/// <item><description>
/// 由傳輸層例外組出的錯誤:連線層例外的訊息可能含位址。→ 門面以 <c>CreateOzakboyHttpPipelineClient</c> 建立,
/// 帶著同一個遮罩器;它回傳的錯誤已經遮罩,例外已經換成 <see cref="SanitizedException"/>。
/// Errors built from transport exceptions, whose messages may include the URI. → The facade is built with
/// <c>CreateOzakboyHttpPipelineClient</c> and carries the same masker; the errors it returns are already masked,
/// with the exception swapped for a <see cref="SanitizedException"/>.
/// </description></item>
/// <item><description>
/// 管線看不到的來源:Telegram 回應本體裡的 <c>description</c>(4xx 對門面而言是成功取得回應,本體由本類別自己解析),
/// 以及本類別自己組出的錯誤(讀本體失敗、排隊或退避時被取消)。→ 所有交出去的錯誤在單一出口再經過 <c>TokenRedactor</c>:
/// 代碼、訊息、每一筆資料都做權杖字面替換,例外一律換成 <see cref="RedactedException"/>;錯誤只帶已遮罩的
/// <c>description</c>,不帶原始本體。管線已經會遮罩,這一道仍然保留,因為這些來源從不經過管線的遮罩器。
/// Sources the pipeline never sees: the <c>description</c> in Telegram's response body (a 4xx is a successfully
/// obtained response to the facade, and this class parses the body itself), and errors this class builds on its own
/// (a failed body read, a cancellation while queued or backing off). → Every error handed out passes through
/// <c>TokenRedactor</c> at a single exit: the code, the message, and every data entry get literal token replacement,
/// and the exception is always swapped for a <see cref="RedactedException"/>; errors carry only the redacted
/// <c>description</c>, never the raw body. This stays although the pipeline masks too, because these sources never
/// pass through the pipeline's masker.
/// </description></item>
/// </list>
/// <para>
/// 其他做法為什麼不夠:<b>把權杖放進 <see cref="HttpClient.BaseAddress"/>、日誌只記相對路徑</b>行不通 ——
/// <see cref="HttpClient"/> 在進入處理器管線之前就把位址組成絕對位址,每一個處理器看到的都是完整位址。
/// <b>只靠管線的遮罩器</b>擋不住它看不到的來源(上面第 4 點);<b>只靠出口遮罩</b>擋不住日誌。
/// 兩道(管線遮罩器登記權杖、出口遮罩)缺一不可,預設日誌則由管線移除。
/// Why the alternatives fall short: <b>putting the token in <see cref="HttpClient.BaseAddress"/> and logging only
/// the relative path</b> does not work — <see cref="HttpClient"/> resolves the absolute URI before the handler
/// pipeline runs, so every handler sees the full address. <b>Relying on the pipeline's masker alone</b> leaves the
/// sources it cannot see (point 4 above); <b>relying on exit redaction alone</b> leaves the logs. Both — the token
/// registered on the pipeline's masker, and redaction at the exit — are needed, with the default logging removed
/// by the pipeline.
/// </para>
/// <para>
/// 仍在本套件控制範圍外的輸出點:<c>System.Net.Http</c> 的 <c>Activity</c>(分散式追蹤的 <c>url.full</c> 標籤)與
/// <c>EventSource</c>(<c>RequestStart</c> 事件),兩者都只在有追蹤或事件監聽器時才產生;見 README。
/// Output points still beyond this package's control: <c>System.Net.Http</c>'s <c>Activity</c> (the
/// <c>url.full</c> tracing tag) and its <c>EventSource</c> (<c>RequestStart</c>), both produced only when a
/// tracing or event listener is attached; see the README.
/// </para>
/// <para>
/// <b>與 <c>Ozakboy.Http</c> 的分工。</b>本類別用它的管線(<c>AddOzakboyHttpPipeline</c> 的遮罩日誌、權杖遮罩器與逾時)、
/// <see cref="HttpPipelineClient"/>(例外收斂為 Result、逾時與呼叫端取消的區分),以及優先閘門裡的
/// <see cref="WeightedRateLimiter"/>(建在官方 <c>System.Threading.RateLimiting</c> 上、以 <see cref="TimeProvider"/> 驅動)。
/// 管線的限流段關閉、<c>RetryHandler</c> 設為 <c>RetryPolicy.NoRetry</c>,重送與節流都收在本類別:
/// Telegram 把等待秒數放在回應本體的 <c>parameters.retry_after</c>,<c>RetryHandler</c> 只認得 <c>Retry-After</c> 標頭,
/// 會用自己的退避提早重送;<c>sendMessage</c> 是 <c>POST</c>,要讓 <c>RetryHandler</c> 重試就得把它標成冪等,
/// 那是在對重試層說謊;而管線的限流器依抵達順序放行,給不了 Critical 優先,也不知道 429 暫停要整個通知器共用。
/// 本類別的每一次送出(含重送)都重新排隊、各付一份節流額度,與 0.3.0 管線「每次嘗試各付權重」的原則一致。
/// 重試因此收在本類別的單一迴圈裡,那裡知道每一種失敗「送出去了沒有」。
/// <b>Division of labour with <c>Ozakboy.Http</c>.</b> This class uses its pipeline (the masked logging, token
/// masker, and timeouts from <c>AddOzakboyHttpPipeline</c>), its <see cref="HttpPipelineClient"/> (exceptions
/// folded into Result, timeouts told apart from caller cancellation), and, inside the priority gate,
/// <see cref="WeightedRateLimiter"/> (built on the official <c>System.Threading.RateLimiting</c>, driven by
/// <see cref="TimeProvider"/>). The pipeline's rate-limiting section is off and its <c>RetryHandler</c> is set to
/// <c>RetryPolicy.NoRetry</c>, leaving resending and throttling to this class: Telegram puts the wait in the body's
/// <c>parameters.retry_after</c>, which <c>RetryHandler</c> cannot see — it knows only the <c>Retry-After</c> header
/// and would resend early on its own backoff; <c>sendMessage</c> is a <c>POST</c>, so letting <c>RetryHandler</c>
/// retry it means labelling it idempotent, which lies to the retry layer; and the pipeline's limiter admits in
/// arrival order, so it cannot put a Critical first and knows nothing of a 429 pause shared across the notifier.
/// Every send this class makes, resends included, queues again and pays for its own slot, the same rule the 0.3.0
/// pipeline follows ("every attempt pays its own weight"). Retrying therefore lives in one loop in this class,
/// which knows for each kind of failure whether anything went out.
/// </para>
/// <para>
/// <b>重送策略。</b>429 代表 Telegram 明確拒收,依 <c>retry_after</c> 等待後重送,不可能造成重複。
/// 逾時、連線失敗與 5xx 則無從得知訊息是否已送達,預設仍然重送(至少送達一次),理由見
/// <see cref="TelegramNotifierOptions.TransientRetry"/>。400 / 401 / 403 / 404 不重送。
/// <b>Resend strategy.</b> A 429 means Telegram explicitly refused the message; it is resent after the
/// <c>retry_after</c> wait and cannot cause a duplicate. After a timeout, a connection failure, or a 5xx there is
/// no telling whether the message arrived; by default it is still resent (at-least-once), for the reasons given
/// on <see cref="TelegramNotifierOptions.TransientRetry"/>. 400, 401, 403, and 404 are never resent.
/// </para>
/// <para>
/// <b>純文字。</b>不設 <c>parse_mode</c>。MarkdownV2 有 18 個保留字元,少跳脫一個整則就被拒;
/// 而切成多段時,一組格式標記可能被切在兩段之間,後一段必定被拒。告警的內容多半來自例外訊息、交易對名稱這類
/// 無法預測的字串,純文字是唯一不會因為內容而送不出去的格式。
/// <b>Plain text.</b> No <c>parse_mode</c> is set. MarkdownV2 has 18 reserved characters and one missed escape
/// gets the whole message rejected; and when a message is split, a formatting pair can straddle two parts,
/// guaranteeing the second is rejected. Alert text mostly comes from unpredictable strings — exception messages,
/// symbol names — and plain text is the only format that can never fail to send because of its content.
/// </para>
/// <para>
/// <b>Critical 優先通道。</b>需求 FR-12 要求 Critical 告警在 5 秒內送達;節流若只依抵達順序放行,一串 Info / Warning
/// 之後的 Critical 要等它們全部送完,最壞要等到節流等待上限(預設 60 秒)。因此每一次送出(含重送)都先經過優先閘門:
/// Critical 永遠排在所有 Info / Warning 前面,下一個可用的節流額度就是它的,最多等一個節流間隔。
/// 優先<b>只影響排隊順序</b>:Critical 仍付每聊天間隔與每分鐘上限的額度,也要等 429 的暫停期結束 ——
/// 搶快只會換來 429,結果更晚送到;暫停結束後第一個出去的就是 Critical。已經在送的那一則不會被中斷。
/// <b>Critical priority lane.</b> FR-12 requires Critical alerts to arrive within 5 seconds. With throttling in
/// arrival order only, a Critical behind a run of Info and Warning waits for all of them, in the worst case until
/// the throttling wait limit (60 seconds by default). So every send, resends included, first passes a priority
/// gate: a Critical is always ahead of every queued Info and Warning and takes the next free throttling slot, at
/// most one interval away. Priority changes <b>queue order only</b>: a Critical still pays for a slot under the
/// per-chat and per-minute limits and still waits out a 429 pause — rushing only earns a 429 and arrives later —
/// and the first message out after the pause is the Critical. A message already being sent is never interrupted.
/// </para>
/// </remarks>
public sealed partial class TelegramNotifier : ITelegramNotifier, IDisposable
{
    /// <summary>
    /// 註冊到 <see cref="IHttpClientFactory"/> 的具名用戶端名稱。
    /// The name of the client registered with <see cref="IHttpClientFactory"/>.
    /// </summary>
    /// <remarks>
    /// 公開這個名稱只是為了讓測試能替換最內層的處理器。<b>不要</b>在這個用戶端上掛任何會記錄請求位址的處理器
    /// (包括 <c>ConfigureHttpClientDefaults</c> 全域加上的那些)—— 位址裡就是權杖,而權杖只登記在管線自己的遮罩器上,
    /// 別的處理器不會遮它。
    /// The name is public only so that tests can replace the innermost handler. Do <b>not</b> attach any handler
    /// that logs request URIs to this client (including ones added globally through
    /// <c>ConfigureHttpClientDefaults</c>): the URI contains the token, which is registered only on the pipeline's
    /// own masker, and no other handler masks it.
    /// </remarks>
    public const string HttpClientName = "Ozakboy.Notify.Telegram";

    private readonly Func<SecretMasker, HttpTimeoutOptions, TimeProvider, HttpPipelineClient> _pipelineSource;
    private readonly Uri _sendMessageUri;
    private readonly string _chatId;
    private readonly HttpTimeoutOptions _timeouts;
    private readonly RetryPolicy _transientRetry;
    private readonly int _maxRateLimitRetries;
    private readonly TimeSpan _maxRetryAfter;
    private readonly int _maxParts;
    private readonly PrioritySendGate _gate;
    private readonly TokenRedactor _redactor;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    /// <summary>
    /// 以 <see cref="IHttpClientFactory"/> 建立通知器。每次送出都向工廠取用 <see cref="HttpClientName"/> 用戶端。
    /// Creates the notifier over an <see cref="IHttpClientFactory"/>, taking the <see cref="HttpClientName"/>
    /// client from it for every send.
    /// </summary>
    /// <remarks>
    /// 門面由本類別建立:錯誤以只登記了這把權杖的遮罩器遮罩,逾時取自 <see cref="TelegramNotifierOptions.RequestTimeout"/>。
    /// <see cref="TelegramServiceCollectionExtensions.AddTelegramNotifier"/> 註冊的通知器不走這條路,
    /// 它以 <c>CreateOzakboyHttpPipelineClient</c> 取得帶著管線同一份遮罩器與逾時的門面。
    /// The facade is built by this class: errors are masked with a masker carrying only this token, and timeouts
    /// come from <see cref="TelegramNotifierOptions.RequestTimeout"/>. The notifier that
    /// <see cref="TelegramServiceCollectionExtensions.AddTelegramNotifier"/> registers does not take this route; it
    /// gets its facade from <c>CreateOzakboyHttpPipelineClient</c>, with the pipeline's own masker and timeouts.
    /// </remarks>
    /// <param name="httpClientFactory">用戶端工廠。The client factory.</param>
    /// <param name="options">設定。建立時即複製一份,之後修改原物件不影響通知器。The options, copied on construction; later changes have no effect.</param>
    /// <param name="logger">日誌器,可為 <see langword="null"/>。The logger, which may be <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClientFactory"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="httpClientFactory"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。訊息不會回述權杖。Thrown when the options are invalid; the message never echoes the token.
    /// </exception>
    public TelegramNotifier(
        IHttpClientFactory httpClientFactory,
        TelegramNotifierOptions options,
        ILogger<TelegramNotifier>? logger = null,
        TimeProvider? timeProvider = null)
        : this(FromFactory(httpClientFactory), options, logger, timeProvider)
    {
    }

    /// <summary>
    /// 以既有的 <see cref="HttpClient"/> 建立通知器,供不使用相依性注入的場合。
    /// Creates the notifier over an existing <see cref="HttpClient"/>, for use without dependency injection.
    /// </summary>
    /// <param name="httpClient">
    /// 用戶端,由呼叫端負責釋放。它的 <see cref="HttpClient.Timeout"/> 應設為無限,逾時交給 <see cref="TelegramNotifierOptions.RequestTimeout"/>;
    /// 若它來自 <see cref="IHttpClientFactory"/>,務必確認已移除預設日誌,否則位址(含權杖)會被記錄。以
    /// <c>AddOzakboyHttpPipeline</c> 註冊的用戶端這兩件事都已做好,但權杖要登記進它的 <c>KnownSecrets</c>,
    /// 否則管線自己的日誌處理器記下的位址會帶著權杖。門面由本類別建立,錯誤以登記了權杖的遮罩器遮罩。
    /// The client, which the caller disposes. Its <see cref="HttpClient.Timeout"/> should be infinite, leaving
    /// timeouts to <see cref="TelegramNotifierOptions.RequestTimeout"/>; if it comes from
    /// <see cref="IHttpClientFactory"/>, make sure the default logging has been removed, or the URI — token
    /// included — will be logged. A client registered with <c>AddOzakboyHttpPipeline</c> has both taken care of, but
    /// the token must go into its <c>KnownSecrets</c>, or the pipeline's own logging handler records the URI with
    /// the token in it. The facade is built by this class, and errors are masked with a masker that knows the token.
    /// </param>
    /// <param name="options">設定。The options.</param>
    /// <param name="logger">日誌器,可為 <see langword="null"/>。The logger, which may be <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源;測試請傳入假時鐘。The time source; tests pass a fake clock.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClient"/> 或 <paramref name="options"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="httpClient"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。訊息不會回述權杖。Thrown when the options are invalid; the message never echoes the token.
    /// </exception>
    public TelegramNotifier(
        HttpClient httpClient,
        TelegramNotifierOptions options,
        ILogger<TelegramNotifier>? logger = null,
        TimeProvider? timeProvider = null)
        : this(FromClient(httpClient), options, logger, timeProvider)
    {
    }

    /// <summary>
    /// 以 <c>AddOzakboyHttpPipeline</c> 註冊的具名用戶端建立通知器,供 <see cref="TelegramServiceCollectionExtensions.AddTelegramNotifier"/> 使用。
    /// Creates the notifier over a named client registered with <c>AddOzakboyHttpPipeline</c>, for
    /// <see cref="TelegramServiceCollectionExtensions.AddTelegramNotifier"/>.
    /// </summary>
    /// <param name="pipelineSource">
    /// 每次送出呼叫一次,通常是 <c>provider.CreateOzakboyHttpPipelineClient(HttpClientName)</c>。門面的遮罩器與逾時
    /// 來自管線註冊處,與管線裡的處理器同一份,本類別不另外指定。
    /// Called once per send, usually <c>provider.CreateOzakboyHttpPipelineClient(HttpClientName)</c>. The facade's
    /// masker and timeouts come from the pipeline registration — the very ones its handlers use — and this class
    /// supplies none of its own.
    /// </param>
    /// <param name="options">設定。The options.</param>
    /// <param name="logger">日誌器,可為 <see langword="null"/>。The logger, which may be <see langword="null"/>.</param>
    /// <param name="timeProvider">時間來源。The time source.</param>
    internal TelegramNotifier(
        Func<HttpPipelineClient> pipelineSource,
        TelegramNotifierOptions options,
        ILogger<TelegramNotifier>? logger,
        TimeProvider? timeProvider)
        : this(FromRegistration(pipelineSource), options, logger, timeProvider)
    {
    }

    private TelegramNotifier(
        Func<SecretMasker, HttpTimeoutOptions, TimeProvider, HttpPipelineClient> pipelineSource,
        TelegramNotifierOptions options,
        ILogger? logger,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        // Validate 已確認兩者非空;這裡的擲出只是讓編譯器看得懂,實際上不會發生。
        // Validate has already confirmed both are present; these throws only inform the compiler.
        var token = options.BotToken ?? throw new ArgumentException("未設定 BotToken。No BotToken is configured.", nameof(options));
        var chatId = options.ChatId ?? throw new ArgumentException("未設定 ChatId。No ChatId is configured.", nameof(options));

        _pipelineSource = pipelineSource;
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _redactor = new TokenRedactor(token);
        _sendMessageUri = BuildSendMessageUri(options.ApiBaseAddress, token);
        _chatId = chatId;
        _transientRetry = options.TransientRetry;
        _maxRateLimitRetries = options.MaxRateLimitRetries;
        _maxRetryAfter = options.MaxRetryAfter;
        _maxParts = options.MaxPartsPerMessage;

        // 只有手動建立門面時用得到;經 AddOzakboyHttpPipeline 註冊的門面用註冊處那一份(同樣由 CreateTimeouts 導出)。
        // Used only when the facade is built by hand; one registered through AddOzakboyHttpPipeline uses the
        // registration's copy, derived by the same CreateTimeouts.
        _timeouts = CreateTimeouts(options);

        // 排隊與節流交給優先閘門:兩個桶(每聊天的最短間隔、每分鐘的總量)同時滿足才放行,Critical 先於 Info / Warning。
        // 同一則通知的各段依序送出(前一段完成才排下一段),段與段之間可能夾著別的通知。
        // Queueing and throttling belong to the priority gate: a send goes out only when both buckets (per-chat
        // spacing and the per-minute total) allow it, with Critical ahead of Info and Warning. The parts of one
        // notification go out in order (each waits for the previous one), though another notification may land
        // between two parts.
        _gate = new PrioritySendGate(options.MinimumInterval, options.MaxMessagesPerMinute, options.ThrottleWaitLimit, _timeProvider);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 分段依序送出;某一段失敗就停止,不送後面的段落(順序已經亂了,後段單獨出現只會更難讀)。
    /// 失敗的錯誤帶有 <see cref="TelegramErrorDataKeys.PartsSent"/> 與 <see cref="TelegramErrorDataKeys.PartsTotal"/>。
    /// Parts go out in order; when one fails, sending stops and the remaining parts are not sent (the order is
    /// already broken, and a later part on its own is only harder to read). The failure carries
    /// <see cref="TelegramErrorDataKeys.PartsSent"/> and <see cref="TelegramErrorDataKeys.PartsTotal"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="notification"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="notification"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">通知器已釋放時擲出。Thrown when the notifier has been disposed.</exception>
    public async Task<Result> SendAsync(TelegramNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var parts = TelegramMessageSplitter.Split(notification.Level, notification.Text, _maxParts);
        for (var index = 0; index < parts.Count; index++)
        {
            var sent = await SendPartAsync(
                BuildPayload(parts[index]),
                notification.Level == NotificationLevel.Critical,
                cancellationToken).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                // 所有要交出去的錯誤都在這一個出口遮罩,不散落在各個失敗分支。
                // Every error that leaves is redacted at this single exit rather than in each failure branch.
                var error = _redactor.Redact(sent.Error
                    .WithData(TelegramErrorDataKeys.Level, notification.Level.ToString())
                    .WithData(TelegramErrorDataKeys.PartsSent, index)
                    .WithData(TelegramErrorDataKeys.PartsTotal, parts.Count));

                LogSendFailed(_logger, notification.Level, index + 1, parts.Count, error.Code, error.Category, error.Message);
                return error;
            }
        }

        LogSent(_logger, notification.Level, parts.Count);
        return Result.Success();
    }

    /// <summary>
    /// 仍在節流佇列裡排隊的送出數。供測試確認排隊狀態。
    /// How many sends are still queued for throttling. Lets tests confirm the queue state.
    /// </summary>
    internal int QueuedCount => _gate.QueuedCount;

    /// <summary>
    /// 釋放節流閘門;仍在排隊的送出會以 <see cref="TelegramErrorCodes.Cancelled"/> 結束。
    /// Releases the throttling gate; sends still queued end with <see cref="TelegramErrorCodes.Cancelled"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>
    /// 由 <see cref="TelegramNotifierOptions.RequestTimeout"/> 導出兩層逾時。註冊管線與手動建立門面用的都是這一份。
    /// Derives both timeout layers from <see cref="TelegramNotifierOptions.RequestTimeout"/>. Registering the
    /// pipeline and building the facade by hand both use this.
    /// </summary>
    /// <remarks>
    /// 每一次 HTTP 呼叫就是一次嘗試(重試由本類別的迴圈負責),因此整體逾時就等於單次逾時。
    /// Each HTTP call is one attempt — retrying is this class's loop's job — so the overall timeout equals the
    /// per-attempt one.
    /// </remarks>
    internal static HttpTimeoutOptions CreateTimeouts(TelegramNotifierOptions options) =>
        new()
        {
            AttemptTimeout = options.RequestTimeout,
            OverallTimeout = options.RequestTimeout,
        };

    private static Func<SecretMasker, HttpTimeoutOptions, TimeProvider, HttpPipelineClient> FromFactory(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        return (masker, timeouts, timeProvider) =>
            new HttpPipelineClient(httpClientFactory.CreateClient(HttpClientName), timeouts, timeProvider, masker);
    }

    private static Func<SecretMasker, HttpTimeoutOptions, TimeProvider, HttpPipelineClient> FromClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        return (masker, timeouts, timeProvider) => new HttpPipelineClient(httpClient, timeouts, timeProvider, masker);
    }

    private static Func<SecretMasker, HttpTimeoutOptions, TimeProvider, HttpPipelineClient> FromRegistration(Func<HttpPipelineClient> pipelineSource)
    {
        ArgumentNullException.ThrowIfNull(pipelineSource);

        // 遮罩器與逾時由管線註冊處帶入(與管線裡的處理器同一份),刻意不用通知器自己的那一份。
        // The masker and timeouts come from the pipeline registration — the ones its handlers use — so the
        // notifier's own are deliberately ignored.
        return (_, _, _) => pipelineSource();
    }

    /// <summary>
    /// 組出 <c>sendMessage</c> 的位址。
    /// Builds the <c>sendMessage</c> address.
    /// </summary>
    /// <remarks>
    /// 以字串串接組出,<b>不用</b> <c>new Uri(baseUri, "bot123:AA…/sendMessage")</c>:相對參照的第一段含冒號時,
    /// 依 RFC 3986 會被解讀成 scheme(<c>bot123:</c>),結果是一個完全不同的絕對位址。
    /// 權杖只含數字、冒號、英數字、<c>_</c> 與 <c>-</c>(已由設定驗證),不需要跳脫。
    /// Built by string concatenation, <b>not</b> <c>new Uri(baseUri, "bot123:AA…/sendMessage")</c>: under RFC 3986
    /// a relative reference whose first segment contains a colon reads as a scheme (<c>bot123:</c>), producing an
    /// entirely different absolute URI. The token holds only digits, a colon, letters, <c>_</c> and <c>-</c>
    /// (checked by option validation), so nothing needs escaping.
    /// </remarks>
    private static Uri BuildSendMessageUri(Uri baseAddress, string token)
    {
        var root = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress.AbsoluteUri : baseAddress.AbsoluteUri + "/";
        return new Uri(root + "bot" + token + "/sendMessage", UriKind.Absolute);
    }

    private async Task<Result> SendPartAsync(byte[] payload, bool critical, CancellationToken cancellationToken)
    {
        var sends = 0;
        var transientFailures = 0;
        var rateLimitRetries = 0;

        while (true)
        {
            // 剩下的 429 暫停比願意等的還長:不排隊、不等,直接交回呼叫端,讓它改走其他管道。
            // The remaining 429 pause is longer than the wait allowed: do not queue and do not wait; hand it back
            // so the caller can switch channels.
            var cooldown = _gate.CooldownRemaining;
            if (cooldown > _maxRetryAfter)
            {
                return TelegramErrorMapper.CoolingDown(cooldown).WithData(TelegramErrorDataKeys.Attempts, sends);
            }

            // 每一次送出(包括重送)都重新排隊並付出節流額度,重試風暴才不會變成 429 風暴;
            // 重送保有原本的等級,所以 429 暫停結束後第一個出去的仍是 Critical。
            // Every send, resends included, queues again and pays for a throttling slot, so a retry storm cannot
            // turn into a 429 storm; a resend keeps its level, so the first message out after a 429 pause is still
            // the Critical.
            var turn = await _gate.WaitForTurnAsync(critical, cancellationToken).ConfigureAwait(false);
            if (turn.IsFailure)
            {
                return turn.Error.WithData(TelegramErrorDataKeys.Attempts, sends);
            }

            sends++;
            var outcome = await SendOnceAsync(payload, cancellationToken).ConfigureAwait(false);
            if (outcome.IsSuccess)
            {
                return outcome;
            }

            var error = outcome.Error.WithData(TelegramErrorDataKeys.Attempts, sends);

            if (string.Equals(error.Code, TelegramErrorCodes.RateLimited, StringComparison.Ordinal))
            {
                // 429 是 Telegram 明確拒收:訊息沒有送達,照它說的時間等待後重送不會造成重複。
                // A 429 is an explicit refusal: nothing was delivered, and resending after the stated wait cannot
                // create a duplicate.
                // 秒數以 double 換算成 TimeSpan 只影響等待長短,不參與任何金額計算。
                // Converting the seconds through double only affects a wait length, never any monetary figure.
                var retryAfter = error.TryGetDecimal(TelegramErrorDataKeys.RetryAfterSeconds, out var seconds)
                    ? TimeSpan.FromSeconds((double)seconds)
                    : TimeSpan.Zero;

                // 暫停期是整個通知器共用的:同一時間其他通知(Critical 也一樣)都不該去撞 Telegram。
                // The pause is shared by the whole notifier: no other notification, Critical included, should hit
                // Telegram meanwhile.
                _gate.ExtendCooldown(retryAfter);

                if (retryAfter > _maxRetryAfter)
                {
                    return error;
                }

                if (rateLimitRetries >= _maxRateLimitRetries)
                {
                    return _maxRateLimitRetries > 0 ? TelegramErrorMapper.Exhaust(error) : error;
                }

                rateLimitRetries++;
                LogRateLimited(_logger, (long)Math.Ceiling(retryAfter.TotalSeconds), rateLimitRetries, _maxRateLimitRetries);
                continue;
            }

            transientFailures++;
            if (!_transientRetry.ShouldRetry(transientFailures, error))
            {
                var worthRetrying = _transientRetry.MaxAttempts > 1 && _transientRetry.ShouldRetry(1, error);
                return worthRetrying ? TelegramErrorMapper.Exhaust(error) : error;
            }

            var delay = _transientRetry.GetDelay(transientFailures);
            LogTransientRetry(_logger, _redactor.Redact(error.Code), transientFailures, (long)delay.TotalMilliseconds);

            var waited = await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            if (waited.IsFailure)
            {
                return waited.Error.WithData(TelegramErrorDataKeys.Attempts, sends);
            }
        }
    }

    private async Task<Result> SendOnceAsync(byte[] payload, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json", "utf-8");

        // POST + JSON 本體:chat id 與訊息內容都不進位址,不會出現在任何只記位址的地方(代理伺服器日誌之類)。
        // POST with a JSON body: neither the chat id nor the text goes into the URI, so neither shows up
        // anywhere that records only URIs (proxy logs and the like).
        using var request = new HttpRequestMessage(HttpMethod.Post, _sendMessageUri) { Content = content };

        // 明確標為非冪等。管線上的 RetryHandler 已設為 NoRetry,本來就不會重試;仍然明寫,讓「這個請求不可盲目重送」
        // 成為看得見的宣告,日後有人改了管線的重試策略也不會意外被重試。
        // Declared non-idempotent. The pipeline's RetryHandler is set to NoRetry and would not retry anyway; saying
        // so still keeps "do not blindly resend" a visible statement, so a later change to the pipeline's retry
        // policy cannot retry it by accident.
        request.AsNonIdempotent();

        var pipeline = _pipelineSource(_redactor.Masker, _timeouts, _timeProvider);
        var sent = await pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return TelegramErrorMapper.FromTransport(sent.Error);
        }

        using var response = sent.GetValueOrThrow();

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return TelegramErrorMapper.FromTransport(HttpErrorMapper.FromException(exception, _redactor.Masker));
        }
        catch (OperationCanceledException exception)
        {
            return TelegramErrorMapper.FromTransport(HttpErrorMapper.FromException(exception, _redactor.Masker));
        }

        // 只有 Telegram 明確回 "ok": true 才算送達。狀態碼成功但本體不對,多半是中間的代理伺服器在回話。
        // Only an explicit "ok": true from Telegram counts as delivered. A success status with the wrong body
        // usually means an intermediate proxy is answering.
        var parsed = TelegramApiResponse.TryParse(body);
        if (response.IsSuccessStatusCode && parsed is { Ok: true })
        {
            return Result.Success();
        }

        TimeSpan? headerRetryAfter = HttpErrorMapper.TryGetRetryAfter(response, _timeProvider, out var headerDelay)
            ? headerDelay
            : null;

        return TelegramErrorMapper.FromResponse(response.StatusCode, parsed, headerRetryAfter);
    }

    private async Task<Result> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return Result.Success();
        }

        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException exception)
        {
            return TelegramErrorMapper.Cancelled(exception);
        }
    }

    /// <summary>
    /// 組出 <c>sendMessage</c> 的 JSON 本體。
    /// Builds the JSON body for <c>sendMessage</c>.
    /// </summary>
    /// <remarks>
    /// 不設 <c>parse_mode</c>(純文字,理由見類別說明)。關閉連結預覽:告警裡的網址若被 Telegram 抓取預覽,
    /// 等於把內部位址交給第三方伺服器去連,而且預覽圖只會把訊息撐長。
    /// No <c>parse_mode</c> (plain text; see the type remarks). Link previews are off: letting Telegram fetch a
    /// preview of a URL in an alert hands an internal address to a third-party server to connect to, and the
    /// preview only makes the message longer.
    /// </remarks>
    private byte[] BuildPayload(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("chat_id", _chatId);
            writer.WriteString("text", text);
            writer.WriteStartObject("link_preview_options");
            writer.WriteBoolean("is_disabled", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // 日誌只記等級、分段、錯誤代碼、分類與耗時:不記位址(含權杖)、不記 chat id、不記訊息內容。
    // Logs carry only the level, part numbers, error code, category, and timing: never the URI (which holds the
    // token), the chat id, or the message text.
    [LoggerMessage(EventId = 3000, Level = LogLevel.Debug, Message = "Telegram 通知已送出:等級 {Level},共 {PartCount} 則")]
    private static partial void LogSent(ILogger logger, NotificationLevel level, int partCount);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Telegram 通知送出失敗:等級 {Level},第 {PartNumber}/{PartCount} 則,{ErrorCode}({Category}):{ErrorMessage}")]
    private static partial void LogSendFailed(ILogger logger, NotificationLevel level, int partNumber, int partCount, string errorCode, ErrorCategory category, string errorMessage);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Telegram 回應 429,依指示等待 {RetryAfterSeconds} 秒後重送(第 {Retry}/{MaxRetries} 次)")]
    private static partial void LogRateLimited(ILogger logger, long retryAfterSeconds, int retry, int maxRetries);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Telegram 暫時性失敗 {ErrorCode}(第 {Failures} 次),{DelayMilliseconds} ms 後重送")]
    private static partial void LogTransientRetry(ILogger logger, string errorCode, int failures, long delayMilliseconds);
}
