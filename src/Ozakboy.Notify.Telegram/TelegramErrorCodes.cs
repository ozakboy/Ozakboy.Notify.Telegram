namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 本套件產生的錯誤代碼常數。呼叫端可據此分支,不必比對錯誤訊息字串。
/// The error codes this package produces. Callers can branch on these instead of matching message text.
/// </summary>
/// <remarks>
/// <para>
/// 代碼一律小寫、以點分段,前段是來源(<c>telegram</c>)、後段是原因。這些字串屬於公開契約的一部分,
/// 一旦發佈就不再更動 —— 呼叫端的備援判斷(例如 Telegram 送不出去就改寄 Email)可能綁在上面。
/// Codes are lowercase and dot-segmented: the first segment is the source (<c>telegram</c>) and the rest is
/// the reason. They are part of the public contract and do not change once published, because callers'
/// fallback logic (email when Telegram fails, say) may key on them.
/// </para>
/// <para>
/// 是否值得重試看的是錯誤的 <c>Category</c>(<c>Error.IsTransient</c>),不是代碼。
/// 本套件已經替你重試過的失敗會標為 <c>ErrorCategory.Exhausted</c>,代碼維持原樣。
/// Whether a retry is worthwhile is read from the error's <c>Category</c> (<c>Error.IsTransient</c>), not its
/// code. Failures this package already retried are labelled <c>ErrorCategory.Exhausted</c>, with the code
/// left as it was.
/// </para>
/// </remarks>
public static class TelegramErrorCodes
{
    /// <summary>
    /// 設定值不合法。
    /// The supplied options are invalid.
    /// </summary>
    public const string InvalidOptions = "telegram.options.invalid";

    /// <summary>
    /// 權杖無效或已被撤銷(HTTP 401)。不是暫時性的:換一把權杖之前,重送多少次都一樣。
    /// The token is invalid or revoked (HTTP 401). Not transient: until the token changes, resending gets the
    /// same answer.
    /// </summary>
    public const string Unauthorized = "telegram.unauthorized";

    /// <summary>
    /// bot 無權送到這個聊天(HTTP 403):被踢出群組、被使用者封鎖,或沒有發言權限。不是暫時性的。
    /// The bot may not post to this chat (HTTP 403): removed from the group, blocked by the user, or without
    /// permission to post. Not transient.
    /// </summary>
    public const string Forbidden = "telegram.forbidden";

    /// <summary>
    /// 找不到設定的聊天(HTTP 400,<c>chat not found</c>)。通常是 chat id 打錯,或 bot 從未被加進該聊天。
    /// The configured chat does not exist (HTTP 400, <c>chat not found</c>), usually a mistyped chat id or a bot
    /// that was never added to the chat.
    /// </summary>
    public const string ChatNotFound = "telegram.chat_not_found";

    /// <summary>
    /// 群組已升級為超級群組,chat id 已改變。新的 id 在 <see cref="TelegramErrorDataKeys.MigrateToChatId"/>。
    /// The group was upgraded to a supergroup and its chat id changed. The new id is in
    /// <see cref="TelegramErrorDataKeys.MigrateToChatId"/>.
    /// </summary>
    /// <remarks>
    /// 單獨給一個代碼,是因為這個失敗會永久持續、而且修法很明確(改設定值);混在一般的 400 裡,
    /// 它看起來就像一次偶發的錯誤,告警會在沒人察覺的情況下全部送不出去。
    /// It gets its own code because the failure is permanent and the fix is obvious (change the setting); lumped
    /// in with other 400s it looks like a one-off, and every alert silently fails from then on.
    /// </remarks>
    public const string ChatMigrated = "telegram.chat_migrated";

    /// <summary>
    /// Telegram 拒絕了這則訊息(其他 HTTP 400)。不是暫時性的。
    /// Telegram rejected the message (any other HTTP 400). Not transient.
    /// </summary>
    public const string BadRequest = "telegram.bad_request";

    /// <summary>
    /// Telegram 回應 404。通常是 API 位址設定錯誤,或權杖格式不被接受。不是暫時性的。
    /// Telegram answered 404, usually a misconfigured API base address or a token format it does not accept.
    /// Not transient.
    /// </summary>
    public const string NotFound = "telegram.not_found";

    /// <summary>
    /// Telegram 限流(HTTP 429),或先前的 429 要求暫停的時間還沒過。錯誤資料可能帶有
    /// <see cref="TelegramErrorDataKeys.RetryAfterSeconds"/>。
    /// Telegram is rate limiting (HTTP 429), or the pause an earlier 429 asked for has not elapsed yet. The error
    /// data may carry <see cref="TelegramErrorDataKeys.RetryAfterSeconds"/>.
    /// </summary>
    public const string RateLimited = "telegram.rate_limited";

    /// <summary>
    /// Telegram 服務暫時無法使用(HTTP 5xx)。
    /// Telegram is temporarily unavailable (HTTP 5xx).
    /// </summary>
    public const string Unavailable = "telegram.unavailable";

    /// <summary>
    /// 單次請求在設定的時間內沒有完成。
    /// A request did not complete within the configured time.
    /// </summary>
    public const string Timeout = "telegram.timeout";

    /// <summary>
    /// 連線層失敗:DNS、TCP、TLS 或連線中斷。
    /// A transport-level failure: DNS, TCP, TLS, or a dropped connection.
    /// </summary>
    public const string Network = "telegram.network";

    /// <summary>
    /// 呼叫端主動取消。這不是失敗,但仍以錯誤形式回報以便統一處理。
    /// The caller cancelled. Not a fault, but reported as an error so callers handle one shape only.
    /// </summary>
    public const string Cancelled = "telegram.cancelled";

    /// <summary>
    /// 本地節流在允許的等待時間內排不到額度。訊息從未送出。
    /// Local throttling could not obtain a slot within the allowed wait. The message was never sent.
    /// </summary>
    public const string Throttled = "telegram.throttled";

    /// <summary>
    /// 回應無法確認已送達:狀態碼成功卻沒有 <c>"ok": true</c>,或是無法歸類的狀態碼。不是暫時性的。
    /// The response does not confirm delivery: a success status without <c>"ok": true</c>, or a status that
    /// fits no other category. Not transient.
    /// </summary>
    /// <remarks>
    /// 「沒有明確說送達」一律視為失敗但不重送:這種回應多半來自中間的代理伺服器而不是 Telegram,
    /// 訊息可能已經送達,重送的結果只會是重複;交給呼叫端決定要不要改走其他管道。
    /// Anything short of an explicit delivery is treated as a failure, but not resent: such a response usually
    /// comes from an intermediate proxy rather than Telegram, the message may already have arrived, and a resend
    /// would only duplicate it. Whether to switch channels is left to the caller.
    /// </remarks>
    public const string UnexpectedResponse = "telegram.unexpected_response";
}
