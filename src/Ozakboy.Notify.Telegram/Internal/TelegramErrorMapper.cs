using System.Globalization;
using System.Net;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Notify.Telegram.Internal;

/// <summary>
/// 把 Telegram 的回應與傳輸層失敗對映成本套件的錯誤代碼。
/// Maps Telegram responses and transport failures onto this package's error codes.
/// </summary>
/// <remarks>
/// 「是否暫時性」一律由 <see cref="ErrorCategory"/> 表達:401 / 403 / 404 / 400 都不是暫時性的,
/// 請求本身或設定有問題,原樣重送只會得到同一個答案;429、5xx、逾時與連線失敗才是。
/// "Transient" is always expressed through <see cref="ErrorCategory"/>: 401, 403, 404, and 400 are not
/// transient — the request or the configuration is wrong, and resending it unchanged earns the same answer —
/// while 429, 5xx, timeouts, and connection failures are.
/// </remarks>
internal static class TelegramErrorMapper
{
    public static Error FromResponse(HttpStatusCode statusCode, TelegramApiResponse? response, TimeSpan? headerRetryAfter)
    {
        var httpStatus = (int)statusCode;

        // 以回應本體的 error_code 為準,沒有才看 HTTP 狀態碼:兩者在 Telegram 上一致,
        // 但本體不是 Telegram 格式時(例如代理伺服器的 HTML 錯誤頁),只剩狀態碼可以依靠。
        // Telegram's own error_code wins, with the HTTP status as the fallback. They agree on Telegram, but when
        // the body is not Telegram's (a proxy's HTML error page, say) the status is all there is.
        var effective = response?.ErrorCode ?? httpStatus;
        var (code, category, summary) = Classify(effective, httpStatus, response);

        var message = string.IsNullOrWhiteSpace(response?.Description)
            ? summary
            : string.Create(CultureInfo.InvariantCulture, $"{summary} Telegram: {response.Description}");

        var error = new Error(code, message, category)
            .WithData(TelegramErrorDataKeys.StatusCode, httpStatus);

        if (response?.ErrorCode is { } telegramCode)
        {
            error = error.WithData(TelegramErrorDataKeys.TelegramErrorCode, telegramCode);
        }

        if (!string.IsNullOrWhiteSpace(response?.Description))
        {
            error = error.WithData(TelegramErrorDataKeys.Description, response.Description);
        }

        // 本體的 retry_after 優先於標頭:Telegram 一定會放在本體,標頭只是退路。
        // The body's retry_after beats the header: Telegram always puts it in the body; the header is a fallback.
        if ((response?.RetryAfter ?? headerRetryAfter) is { } retryAfter)
        {
            error = error.WithData(TelegramErrorDataKeys.RetryAfterSeconds, (decimal)retryAfter.TotalSeconds);
        }

        if (response?.MigrateToChatId is { } migrateTo)
        {
            error = error.WithData(TelegramErrorDataKeys.MigrateToChatId, migrateTo);
        }

        return error;
    }

    /// <summary>
    /// 把 <c>Ozakboy.Http</c> 的傳輸層錯誤改標成本套件的代碼,原始代碼保留在
    /// <see cref="TelegramErrorDataKeys.TransportErrorCode"/>。
    /// Relabels an <c>Ozakboy.Http</c> transport error with this package's code, keeping the original in
    /// <see cref="TelegramErrorDataKeys.TransportErrorCode"/>.
    /// </summary>
    public static Error FromTransport(Error transportError)
    {
        ArgumentNullException.ThrowIfNull(transportError);

        var (code, summary) = transportError.Category switch
        {
            ErrorCategory.Timeout => (TelegramErrorCodes.Timeout, "送出 Telegram 訊息逾時。Sending the Telegram message timed out."),
            ErrorCategory.Cancelled => (TelegramErrorCodes.Cancelled, "送出 Telegram 訊息已取消。Sending the Telegram message was cancelled."),
            _ => (TelegramErrorCodes.Network, "連線 Telegram 失敗。Could not reach Telegram."),
        };

        // 分類沿用原本的(逾時仍是逾時、連線失敗仍是連線失敗),只有「無法歸類」的例外一律視為連線失敗,
        // 與 Ozakboy.Http 對未知例外的處理一致。
        // The category carries over (a timeout stays a timeout), and only an unclassifiable failure is treated
        // as a network one, matching how Ozakboy.Http treats unknown exceptions.
        var category = transportError.Category switch
        {
            ErrorCategory.Timeout or ErrorCategory.Cancelled or ErrorCategory.Network => transportError.Category,
            _ => ErrorCategory.Network,
        };

        return new Error(code, $"{summary} {transportError.Message}", category)
        {
            Exception = transportError.Exception,
            Data = transportError.Data,
        }.WithData(TelegramErrorDataKeys.TransportErrorCode, transportError.Code);
    }

    /// <summary>
    /// 在本地節流佇列裡等超過上限。訊息沒有送出。
    /// Waited in the local throttling queue past the limit. Nothing was sent.
    /// </summary>
    public static Error ThrottleTimedOut(TimeSpan waitLimit) =>
        new(
            TelegramErrorCodes.Throttled,
            string.Create(
                CultureInfo.InvariantCulture,
                $"本地節流在 {waitLimit} 內排不到發送額度,訊息未送出。Local throttling found no send slot within {waitLimit}; the message was not sent."),
            ErrorCategory.RateLimited);

    /// <summary>
    /// 排隊期間被取消。訊息沒有送出。
    /// Cancelled while queued. Nothing was sent.
    /// </summary>
    public static Error CancelledWhileQueued() =>
        new(TelegramErrorCodes.Cancelled, "等待發送額度時被取消,訊息未送出。Cancelled while waiting for a send slot; the message was not sent.", ErrorCategory.Cancelled);

    /// <summary>
    /// 先前的 429 要求暫停、而剩餘時間超過願意等待的上限時的錯誤。訊息沒有送出。
    /// The error for when an earlier 429's pause still has longer to run than the wait limit allows. Nothing was
    /// sent.
    /// </summary>
    public static Error CoolingDown(TimeSpan remaining) =>
        new Error(
            TelegramErrorCodes.RateLimited,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Telegram 先前要求暫停,還剩 {remaining.TotalSeconds:0} 秒,超過願意等待的上限,訊息未送出。Telegram asked for a pause that still has {remaining.TotalSeconds:0} seconds to run, beyond the wait limit; the message was not sent."),
            ErrorCategory.RateLimited)
            .WithData(TelegramErrorDataKeys.RetryAfterSeconds, (decimal)remaining.TotalSeconds);

    public static Error Cancelled(OperationCanceledException exception) =>
        new(TelegramErrorCodes.Cancelled, "送出 Telegram 訊息已取消。Sending the Telegram message was cancelled.", ErrorCategory.Cancelled)
        {
            Exception = exception,
        };

    /// <summary>
    /// 把「值得重試但機會已用盡」的錯誤改標為 <see cref="ErrorCategory.Exhausted"/>。
    /// Re-labels an error that was worth retrying but ran out of attempts as <see cref="ErrorCategory.Exhausted"/>.
    /// </summary>
    /// <remarks>
    /// 原本的分類(逾時、5xx)是暫時性的,原樣交回去就等於說「再試一次可能會過」—— 但我們已經替它試過了。
    /// 呼叫端的重試層看到暫時性就會再跑一輪,同一個故障被乘上兩層次數;而告警情境下每多一輪重送,
    /// 就多一次重複送達的可能。代碼、訊息、例外與資料一律保留,只改分類。
    /// The original category (a timeout, a 5xx) is transient, and handing it back unchanged says "another try
    /// might work" — but the tries have been made. A caller's retry layer that sees transient runs another round,
    /// multiplying one fault by two layers of attempts, and every extra round of resending is one more chance of
    /// a duplicate alert. The code, message, exception, and data are kept; only the category changes.
    /// </remarks>
    public static Error Exhaust(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return Error.Exhausted(
            error.Code,
            $"{error.Message}(重試機會已用盡,不再重送。Retries are spent; not resending.)") with
        {
            Exception = error.Exception,
            Data = error.Data,
        };
    }

    private static (string Code, ErrorCategory Category, string Summary) Classify(int effective, int httpStatus, TelegramApiResponse? response) =>
        effective switch
        {
            401 => (TelegramErrorCodes.Unauthorized, ErrorCategory.Unauthorized,
                "Telegram 拒絕了 bot 權杖(401):權杖無效或已被撤銷,請向 @BotFather 重新取得。Telegram rejected the bot token (401): it is invalid or revoked; obtain a new one from @BotFather."),
            403 => (TelegramErrorCodes.Forbidden, ErrorCategory.Forbidden,
                "Telegram 拒絕送到這個聊天(403):bot 可能已被踢出群組、被使用者封鎖,或沒有發言權限。Telegram refused delivery to this chat (403): the bot may have been removed from the group, blocked by the user, or denied permission to post."),
            404 => (TelegramErrorCodes.NotFound, ErrorCategory.NotFound,
                "Telegram 回應 404:通常是 API 位址設定錯誤,或權杖格式不被接受。Telegram answered 404: usually a misconfigured API base address or a token format it does not accept."),
            429 => (TelegramErrorCodes.RateLimited, ErrorCategory.RateLimited,
                "Telegram 限流(429)。Telegram is rate limiting (429)."),
            400 when response?.MigrateToChatId is not null => (TelegramErrorCodes.ChatMigrated, ErrorCategory.NotFound,
                "群組已升級為超級群組,chat id 已改變;新的 id 見錯誤資料 migrateToChatId。The group was upgraded to a supergroup and its chat id changed; the new id is in the migrateToChatId data entry."),
            400 when response?.Description?.Contains("chat not found", StringComparison.OrdinalIgnoreCase) == true => (TelegramErrorCodes.ChatNotFound, ErrorCategory.NotFound,
                "找不到設定的聊天(400):chat id 錯誤,或 bot 從未被加進該聊天。The configured chat was not found (400): the chat id is wrong or the bot was never added to it."),
            400 => (TelegramErrorCodes.BadRequest, ErrorCategory.Validation,
                "Telegram 拒絕了這則訊息(400)。Telegram rejected the message (400)."),
            >= 500 and <= 599 => (TelegramErrorCodes.Unavailable, ErrorCategory.Unavailable,
                string.Create(CultureInfo.InvariantCulture, $"Telegram 服務暫時無法使用({effective})。Telegram is temporarily unavailable ({effective}).")),
            _ => (TelegramErrorCodes.UnexpectedResponse, ErrorCategory.Unexpected,
                string.Create(CultureInfo.InvariantCulture, $"Telegram 的回應無法確認已送達(HTTP {httpStatus})。The Telegram response does not confirm delivery (HTTP {httpStatus}).")),
        };
}
