namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 本套件寫進 <see cref="Ozakboy.Core.Abstractions.Error.Data"/> 的資料鍵常數。
/// The keys this package writes into <see cref="Ozakboy.Core.Abstractions.Error.Data"/>.
/// </summary>
/// <remarks>
/// 與 <see cref="TelegramErrorCodes"/> 一樣屬於公開契約,發佈後就不再更動。取值請用
/// <c>Error.TryGetInt64</c> / <c>TryGetDecimal</c> / <c>TryGetData</c>,不要自己 <c>Parse</c>。
/// 資料裡<b>永遠不會</b>出現權杖,也不會出現訊息內文與原始回應內容 —— 回應內容可能夾帶任何東西。
/// Like <see cref="TelegramErrorCodes"/>, these keys are part of the public contract and do not change once
/// published. Read them with <c>Error.TryGetInt64</c> / <c>TryGetDecimal</c> / <c>TryGetData</c> rather than
/// parsing by hand. The data <b>never</b> contains the token, nor the message text or the raw response body,
/// since a response body can carry anything.
/// </remarks>
public static class TelegramErrorDataKeys
{
    /// <summary>
    /// HTTP 狀態碼,以 <c>TryGetInt64</c> 讀回。
    /// The HTTP status code; read it back with <c>TryGetInt64</c>.
    /// </summary>
    public const string StatusCode = "statusCode";

    /// <summary>
    /// Telegram 回應中的 <c>error_code</c>,以 <c>TryGetInt64</c> 讀回。
    /// The <c>error_code</c> from Telegram's response; read it back with <c>TryGetInt64</c>.
    /// </summary>
    public const string TelegramErrorCode = "telegramErrorCode";

    /// <summary>
    /// Telegram 回應中的 <c>description</c>(已截斷、已遮罩),以 <c>TryGetData</c> 讀回。
    /// The <c>description</c> from Telegram's response, truncated and redacted; read it back with
    /// <c>TryGetData</c>.
    /// </summary>
    public const string Description = "description";

    /// <summary>
    /// Telegram 要求等待的秒數(回應中的 <c>parameters.retry_after</c>,沒有時退回 <c>Retry-After</c> 標頭),
    /// 以 <c>TryGetDecimal</c> 讀回。
    /// How many seconds Telegram asked to wait (<c>parameters.retry_after</c> from the response, falling back to
    /// the <c>Retry-After</c> header); read it back with <c>TryGetDecimal</c>.
    /// </summary>
    public const string RetryAfterSeconds = "retryAfterSeconds";

    /// <summary>
    /// 群組升級後新的 chat id,以 <c>TryGetInt64</c> 讀回。僅出現在 <see cref="TelegramErrorCodes.ChatMigrated"/>。
    /// The new chat id after a group upgrade; read it back with <c>TryGetInt64</c>. Only present on
    /// <see cref="TelegramErrorCodes.ChatMigrated"/>.
    /// </summary>
    public const string MigrateToChatId = "migrateToChatId";

    /// <summary>
    /// 放棄之前這一則一共送出幾次,以 <c>TryGetInt64</c> 讀回。
    /// How many times the failing part went out before giving up; read it back with <c>TryGetInt64</c>.
    /// </summary>
    public const string Attempts = "attempts";

    /// <summary>
    /// 失敗前已經送達的分段數,以 <c>TryGetInt64</c> 讀回。
    /// How many parts had been delivered before the failure; read it back with <c>TryGetInt64</c>.
    /// </summary>
    /// <remarks>
    /// 一則長訊息切成多段時,前面幾段可能已經送達。呼叫端改走其他管道(例如 Email)時,
    /// 據此可以知道收件人手上已經有哪些內容。
    /// When a long message is split, the earlier parts may already have arrived. A caller falling back to another
    /// channel (email, say) can tell from this what the recipient already has.
    /// </remarks>
    public const string PartsSent = "partsSent";

    /// <summary>
    /// 這則通知總共切成幾段,以 <c>TryGetInt64</c> 讀回。
    /// How many parts the notification was split into; read it back with <c>TryGetInt64</c>.
    /// </summary>
    public const string PartsTotal = "partsTotal";

    /// <summary>
    /// 通知等級名稱(<c>Info</c> / <c>Warning</c> / <c>Critical</c>),以 <c>TryGetData</c> 讀回。
    /// The notification level name (<c>Info</c> / <c>Warning</c> / <c>Critical</c>); read it back with
    /// <c>TryGetData</c>.
    /// </summary>
    public const string Level = "level";

    /// <summary>
    /// 底層 <c>Ozakboy.Http</c> 的原始錯誤代碼(例如 <c>http.timeout</c>),以 <c>TryGetData</c> 讀回。僅出現在傳輸層失敗。
    /// The underlying <c>Ozakboy.Http</c> error code (such as <c>http.timeout</c>); read it back with
    /// <c>TryGetData</c>. Only present on transport failures.
    /// </summary>
    public const string TransportErrorCode = "transportErrorCode";
}
