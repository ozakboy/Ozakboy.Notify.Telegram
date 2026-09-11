using System.Globalization;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Notify.Telegram;

/// <summary>
/// Telegram 通知器的設定。
/// Options for the Telegram notifier.
/// </summary>
/// <remarks>
/// <para>
/// <b>沒有預設權杖,也不會替你從任何地方讀取。</b>權杖從哪裡來(環境變數、DPAPI 加密後的設定檔)是呼叫端的決定;
/// 套件若自作主張去讀某個固定的設定鍵,那個鍵遲早會被人以明文寫進 <c>appsettings.json</c> 並一起提交。
/// <b>There is no default token, and nothing is read from anywhere on your behalf.</b> Where the token comes
/// from — an environment variable, a DPAPI-encrypted settings file — is the caller's decision. A package that
/// quietly read some fixed configuration key would sooner or later see that key written into
/// <c>appsettings.json</c> in plain text and committed along with it.
/// </para>
/// <para>
/// 這是 <see langword="class"/> 而不是 <see langword="record"/>:record 自動產生的 <c>ToString()</c> 會把每個屬性
/// 印出來,設定物件一旦被寫進日誌,權杖就跟著出去了。
/// This is a <see langword="class"/> rather than a <see langword="record"/>: a record's generated
/// <c>ToString()</c> prints every property, so logging the options object would log the token with it.
/// </para>
/// <para>
/// 預設值對應 Telegram 公開的限制:同一個聊天約每秒 1 則、群組每分鐘 20 則。
/// 私人聊天可以把 <see cref="MaxMessagesPerMinute"/> 調高,但每秒 1 則那條不要動。
/// The defaults follow Telegram's published limits: about one message per second to the same chat, and 20 per
/// minute to a group. A private chat can raise <see cref="MaxMessagesPerMinute"/>, but leave the one-per-second
/// spacing alone.
/// </para>
/// </remarks>
public sealed class TelegramNotifierOptions
{
    /// <summary>
    /// Telegram 對單則訊息的長度上限,以 UTF-16 字元數計。
    /// Telegram's limit on a single message, counted in UTF-16 code units.
    /// </summary>
    /// <remarks>
    /// 以 UTF-16 字元數計算是保守的做法:一個 emoji 算兩個,實際能放的 Unicode 字元只會更多、不會更少,
    /// 因此照這個數字切出來的訊息不會因為長度被拒。
    /// Counting UTF-16 code units is the conservative reading: an emoji counts as two, so the real number of
    /// Unicode characters that fit can only be higher, never lower, and messages split to this figure are never
    /// rejected for length.
    /// </remarks>
    public const int MaxMessageLength = 4096;

    /// <summary>
    /// <see cref="MaxPartsPerMessage"/> 可設定的上限。
    /// The upper bound for <see cref="MaxPartsPerMessage"/>.
    /// </summary>
    public const int MaxPartsPerMessageLimit = 50;

    /// <summary>
    /// <see cref="MaxRetryAfter"/> 可設定的上限:一小時。
    /// The upper bound for <see cref="MaxRetryAfter"/>: one hour.
    /// </summary>
    /// <remarks>
    /// 在一次 <c>SendAsync</c> 裡等上一小時以上已經沒有意義:到那時告警早就過時,
    /// 呼叫端應該拿到失敗、改走其他管道。
    /// Waiting longer than an hour inside one <c>SendAsync</c> serves no purpose: the alert is stale by then, and
    /// the caller should get the failure back and switch channels.
    /// </remarks>
    public static readonly TimeSpan MaxRetryAfterLimit = TimeSpan.FromHours(1);

    /// <summary>
    /// 暫時性失敗的預設重試策略:最多送 3 次,退避從 1 秒起、上限 10 秒。
    /// The default retry policy for transient failures: at most three sends, backing off from one second up to
    /// ten.
    /// </summary>
    public static RetryPolicy DefaultTransientRetry { get; } = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Bot 權杖,即向 @BotFather 取得、形如 <c>123456789:AA…</c> 的字串。沒有預設值。
    /// The bot token as issued by @BotFather, shaped like <c>123456789:AA…</c>. There is no default.
    /// </summary>
    /// <remarks>
    /// 請從環境變數或加密後的設定檔取得(例如 <c>Ozakboy.Security</c> 的 DPAPI 保護),絕不寫進程式碼或明文設定檔。
    /// Supply it from an environment variable or an encrypted settings file (DPAPI protection from
    /// <c>Ozakboy.Security</c>, for instance); never put it in source code or a plain-text settings file.
    /// </remarks>
    public string? BotToken { get; set; }

    /// <summary>
    /// 目標聊天:數字 id(群組與頻道為負數)或公開頻道的 <c>@名稱</c>。
    /// The target chat: a numeric id (negative for groups and channels) or a public channel's <c>@name</c>.
    /// </summary>
    public string? ChatId { get; set; }

    /// <summary>
    /// Bot API 的根位址。預設為 <c>https://api.telegram.org/</c>;自架 Bot API 伺服器時才需要更改。
    /// The Bot API root. Defaults to <c>https://api.telegram.org/</c>; change it only for a self-hosted Bot API
    /// server.
    /// </summary>
    /// <remarks>
    /// 只接受 <c>https</c>;<c>http</c> 僅限本機回送位址(本機自架的伺服器)。權杖在路徑裡,
    /// 走明文 HTTP 等於把權杖交給路上每一台設備。
    /// Only <c>https</c> is accepted; <c>http</c> is allowed for loopback addresses only (a server on this
    /// machine). The token is in the path, and plain HTTP hands it to every device along the route.
    /// </remarks>
    public Uri ApiBaseAddress { get; set; } = new("https://api.telegram.org/");

    /// <summary>
    /// 單次 HTTP 請求的逾時。逾時視為暫時性失敗,依 <see cref="TransientRetry"/> 決定是否重送。
    /// The timeout for a single HTTP request. A timeout counts as transient and may be resent according to
    /// <see cref="TransientRetry"/>.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 同一個聊天兩則訊息之間的最短間隔。預設 1 秒,對應 Telegram 的每聊天限制。
    /// The shortest spacing between two messages to the same chat. Defaults to one second, matching Telegram's
    /// per-chat limit.
    /// </summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 每分鐘最多送出幾則。預設 20,對應 Telegram 對群組的限制。
    /// The most messages sent per minute. Defaults to 20, matching Telegram's limit for groups.
    /// </summary>
    public int MaxMessagesPerMinute { get; set; } = 20;

    /// <summary>
    /// 一則訊息在本地節流佇列中最多等多久。超過就放棄並回傳 <see cref="TelegramErrorCodes.Throttled"/>,訊息不會送出。
    /// How long a message may wait in the local throttling queue. Past it the send gives up with
    /// <see cref="TelegramErrorCodes.Throttled"/> and nothing goes out.
    /// </summary>
    /// <remarks>
    /// 短時間湧入大量告警時,排在後面的會等待。設得太長,呼叫端會在不知情的情況下卡住;
    /// 設得太短,正常的突發量就開始丟訊息。預設 60 秒,足以消化一分鐘配額的突發。
    /// When a burst of alerts arrives, the later ones wait. Too long and callers stall without knowing why; too
    /// short and ordinary bursts start losing messages. The default of 60 seconds absorbs a full minute's quota.
    /// </remarks>
    public TimeSpan ThrottleWaitLimit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 收到 429 時最多重送幾次。預設 3。設為 0 表示收到 429 就直接回傳失敗。
    /// How many times a message is resent after a 429. Defaults to three; 0 returns the failure on the first
    /// 429.
    /// </summary>
    public int MaxRateLimitRetries { get; set; } = 3;

    /// <summary>
    /// 願意照 Telegram 指示等待的最長時間。對方要求等更久時不等待,直接回傳 <see cref="TelegramErrorCodes.RateLimited"/>。
    /// The longest wait Telegram's instruction will be honoured for. When it asks for longer, the send does not
    /// wait and returns <see cref="TelegramErrorCodes.RateLimited"/> instead.
    /// </summary>
    /// <remarks>
    /// 不接受「提早重送」這個折衷:對方說等 N 秒,提早回去只會延長封鎖。等不起就放手,
    /// 讓呼叫端改寄 Email 之類的其他管道。上限為 <see cref="MaxRetryAfterLimit"/>。
    /// Coming back early is not an option: Telegram said N seconds, and returning sooner only extends the block.
    /// If the wait is unaffordable, let go and let the caller switch to another channel such as email. Capped at
    /// <see cref="MaxRetryAfterLimit"/>.
    /// </remarks>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 暫時性失敗(逾時、連線失敗、5xx)的重試策略。預設為 <see cref="DefaultTransientRetry"/>。
    /// The retry policy for transient failures (timeouts, connection failures, 5xx). Defaults to
    /// <see cref="DefaultTransientRetry"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>這些失敗重送,可能讓同一則訊息出現兩次。</b><c>sendMessage</c> 不是冪等的,Telegram 也沒有去重用的識別碼:
    /// 逾時或連線在回應途中斷掉時,訊息可能早就送達了。
    /// <b>Resending these failures may deliver the same message twice.</b> <c>sendMessage</c> is not idempotent
    /// and Telegram offers no deduplication key: after a timeout, or a connection dropped mid-response, the
    /// message may well have arrived already.
    /// </para>
    /// <para>
    /// 預設仍然重送,因為在告警情境下兩種錯誤的代價不對等:重複一則告警,收件人多看一眼;
    /// 漏掉一則告警,收件人可能完全不知道系統已經進了安全模式。這是「至少送達一次」的選擇。
    /// 若你的用途相反(重複的代價比漏送高),設為 <see cref="RetryPolicy.NoRetry"/> 即為「最多送達一次」。
    /// It resends by default because the two mistakes do not cost the same when alerting: a duplicated alert
    /// costs the recipient a second glance, while a lost one may leave them unaware that the system has entered
    /// safe mode. That is the at-least-once choice. If your use is the other way round — duplicates cost more than
    /// losses — set <see cref="RetryPolicy.NoRetry"/> for at-most-once.
    /// </para>
    /// <para>
    /// 429 不受這個策略影響,由 <see cref="MaxRateLimitRetries"/> 另外控制:429 代表 Telegram 明確拒收,
    /// 重送它不可能造成重複。
    /// A 429 is not governed by this policy but by <see cref="MaxRateLimitRetries"/>: a 429 means Telegram
    /// explicitly refused the message, so resending it cannot cause a duplicate.
    /// </para>
    /// </remarks>
    public RetryPolicy TransientRetry { get; set; } = DefaultTransientRetry;

    /// <summary>
    /// 一則通知最多切成幾段。超過的內容截斷,並在最後一段註明截掉了多少字元。預設 10,上限 <see cref="MaxPartsPerMessageLimit"/>。
    /// The most parts one notification is split into. Anything beyond is truncated, and the last part notes how
    /// many characters were cut. Defaults to 10, capped at <see cref="MaxPartsPerMessageLimit"/>.
    /// </summary>
    /// <remarks>
    /// 沒有這個上限的話,一段意外塞進來的大型內容(例如整份堆疊或整個回應本體)在每秒 1 則的節流下
    /// 會佔住佇列好幾分鐘,排在它後面的告警全部被延誤。
    /// Without this cap, a large blob pushed in by accident — a whole stack dump or response body — would hold the
    /// queue for minutes at one message per second, delaying every alert queued behind it.
    /// </remarks>
    public int MaxPartsPerMessage { get; set; } = 10;

    /// <summary>
    /// 檢查設定是否可用。錯誤訊息絕不回述權杖的值。
    /// Validates the options. Error messages never echo the token.
    /// </summary>
    /// <returns>
    /// 設定可用時為成功;否則為帶 <see cref="ErrorCategory.Validation"/> 的失敗,代碼為 <see cref="TelegramErrorCodes.InvalidOptions"/>。
    /// Success when the options are usable, otherwise a failure carrying <see cref="ErrorCategory.Validation"/>
    /// and the code <see cref="TelegramErrorCodes.InvalidOptions"/>.
    /// </returns>
    public Result Validate() =>
        ValidateCredentials()
            .Then(ValidateAddress)
            .Then(ValidateTiming)
            .Then(ValidateLimits);

    /// <summary>
    /// 粗檢權杖格式:數字、冒號、至少 30 個英數字(含 <c>_</c> 與 <c>-</c>)。
    /// A rough token-shape check: digits, a colon, then at least 30 letters, digits, <c>_</c> or <c>-</c>.
    /// </summary>
    /// <remarks>
    /// 只做粗檢,不驗證權杖是否真的有效 —— 那需要打一次 API。粗檢的目的是在啟動時就擋下
    /// 「貼錯欄位」「多貼了空白或引號」這類錯誤,而不是等到第一則告警送不出去才發現。
    /// This is only a shape check, not a validity check, which would need an API call. Its job is to catch
    /// "pasted into the wrong field" or "stray whitespace or quotes" at start-up, rather than discovering it when
    /// the first alert fails to send.
    /// </remarks>
    internal static bool LooksLikeBotToken(string token)
    {
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon < 1 || colon > 20)
        {
            return false;
        }

        for (var i = 0; i < colon; i++)
        {
            if (!char.IsAsciiDigit(token[i]))
            {
                return false;
            }
        }

        var secretLength = token.Length - colon - 1;
        if (secretLength < 30 || secretLength > 100)
        {
            return false;
        }

        for (var i = colon + 1; i < token.Length; i++)
        {
            var c = token[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 粗檢 chat id:可帶負號的整數,或 <c>@</c> 開頭的頻道名稱。
    /// A rough chat-id check: an integer, optionally negative, or a channel name starting with <c>@</c>.
    /// </summary>
    internal static bool LooksLikeChatId(string chatId)
    {
        if (chatId.StartsWith('@'))
        {
            if (chatId.Length < 2)
            {
                return false;
            }

            for (var i = 1; i < chatId.Length; i++)
            {
                var c = chatId[i];
                if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        return long.TryParse(chatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    private static Result Invalid(string message) => Error.Validation(TelegramErrorCodes.InvalidOptions, message);

    private static bool IsAllowedScheme(Uri address) =>
        string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && address.IsLoopback);

    private Result ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(BotToken))
        {
            return Invalid("未設定 BotToken。權杖請從環境變數或加密設定檔取得,本套件不提供預設值。No BotToken is configured; supply it from an environment variable or an encrypted settings file, as this package has no default.");
        }

        // 訊息只描述規則,絕不帶出收到的值:設定錯誤的例外訊息幾乎一定會被寫進日誌。
        // The message states the rule and never carries the value received: an options error almost always ends
        // up in a log.
        if (!LooksLikeBotToken(BotToken))
        {
            return Invalid("BotToken 格式不符,應為「數字:英數字串」(向 @BotFather 取得的那一串,不含空白或引號)。為避免外洩,這裡不回述收到的值。The BotToken is malformed: it should be digits, a colon, then letters, digits, '_' or '-', exactly as issued by @BotFather with no whitespace or quotes. The value received is deliberately not echoed.");
        }

        if (string.IsNullOrWhiteSpace(ChatId))
        {
            return Invalid("未設定 ChatId。No ChatId is configured.");
        }

        if (!LooksLikeChatId(ChatId))
        {
            return Invalid("ChatId 格式不符,應為整數(群組與頻道為負數)或 @頻道名稱。The ChatId is malformed; it should be an integer (negative for groups and channels) or an @channel name.");
        }

        return Result.Success();
    }

    private Result ValidateAddress()
    {
        if (ApiBaseAddress is null || !ApiBaseAddress.IsAbsoluteUri)
        {
            return Invalid("ApiBaseAddress 必須是絕對位址。ApiBaseAddress must be an absolute URI.");
        }

        if (!IsAllowedScheme(ApiBaseAddress))
        {
            return Invalid("ApiBaseAddress 只接受 https;http 僅限本機回送位址。權杖在路徑裡,明文傳輸等於公開權杖。ApiBaseAddress must use https, or http on a loopback address only; the token travels in the path, and plain text would expose it.");
        }

        if (!string.IsNullOrEmpty(ApiBaseAddress.UserInfo)
            || !string.IsNullOrEmpty(ApiBaseAddress.Query)
            || !string.IsNullOrEmpty(ApiBaseAddress.Fragment))
        {
            return Invalid("ApiBaseAddress 不可包含帳密、query 或 fragment。ApiBaseAddress must not contain user info, a query, or a fragment.");
        }

        return Result.Success();
    }

    private Result ValidateTiming()
    {
        if (RequestTimeout <= TimeSpan.Zero)
        {
            return Invalid("RequestTimeout 必須為正值。RequestTimeout must be positive.");
        }

        if (MinimumInterval <= TimeSpan.Zero)
        {
            return Invalid("MinimumInterval 必須為正值。MinimumInterval must be positive.");
        }

        if (ThrottleWaitLimit <= TimeSpan.Zero)
        {
            return Invalid("ThrottleWaitLimit 必須為正值。ThrottleWaitLimit must be positive.");
        }

        if (MaxRetryAfter <= TimeSpan.Zero || MaxRetryAfter > MaxRetryAfterLimit)
        {
            return Invalid(string.Create(
                CultureInfo.InvariantCulture,
                $"MaxRetryAfter 必須大於 0 且不超過 {MaxRetryAfterLimit}。MaxRetryAfter must be positive and no more than {MaxRetryAfterLimit}."));
        }

        return Result.Success();
    }

    private Result ValidateLimits()
    {
        if (MaxMessagesPerMinute < 1)
        {
            return Invalid("MaxMessagesPerMinute 必須為正整數。MaxMessagesPerMinute must be positive.");
        }

        if (MaxRateLimitRetries < 0)
        {
            return Invalid("MaxRateLimitRetries 不可為負。MaxRateLimitRetries must not be negative.");
        }

        if (TransientRetry is null)
        {
            return Invalid("未設定 TransientRetry。要關閉重試請用 RetryPolicy.NoRetry。No TransientRetry is configured; use RetryPolicy.NoRetry to switch retries off.");
        }

        if (MaxPartsPerMessage < 1 || MaxPartsPerMessage > MaxPartsPerMessageLimit)
        {
            return Invalid(string.Create(
                CultureInfo.InvariantCulture,
                $"MaxPartsPerMessage 必須介於 1 到 {MaxPartsPerMessageLimit}。MaxPartsPerMessage must be between 1 and {MaxPartsPerMessageLimit}."));
        }

        return Result.Success();
    }
}
