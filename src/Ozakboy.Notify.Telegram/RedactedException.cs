namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 取代原始例外、已遮罩權杖的替身。本套件回傳的錯誤裡,<c>Error.Exception</c> 只會是這個型別或 <see langword="null"/>。
/// A redacted stand-in for an original exception. In errors this package returns, <c>Error.Exception</c> is
/// either this type or <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// 原始例外不能原封不動交出去。Bot 權杖就在請求位址的路徑裡,而連線層的例外訊息、內層例外、
/// 甚至呼叫端日誌框架對例外物件的結構化輸出,都可能帶著那個位址。字串可以遮罩,例外物件卻不行 ——
/// <see cref="Exception.Message"/> 是唯讀的,內層例外鏈也改不動。
/// The original exception cannot be handed over as it is. The bot token sits in the request URI's path, and a
/// transport exception's message, its inner exceptions, or a logging framework's structured rendering of the
/// exception object may all carry that URI. Strings can be masked; an exception object cannot —
/// <see cref="Exception.Message"/> is read-only and the inner-exception chain cannot be rewritten.
/// </para>
/// <para>
/// 因此一律換成這個替身:原始型別名稱、遮罩後的訊息,以及遮罩後的完整 <c>ToString()</c>(含內層例外與堆疊)
/// 都保留下來供診斷,但它沒有內層例外,也不持有原始物件的任何參照。代價是堆疊追蹤變成文字而不是物件,
/// 對「連不上 Telegram」這類問題,這點損失可以接受。
/// So it is always swapped for this stand-in: the original type name, the masked message, and the masked full
/// <c>ToString()</c> (inner exceptions and stack included) are kept for diagnosis, but it has no inner
/// exception and holds no reference to the original object. The cost is a stack trace that is text rather than
/// an object, which is an acceptable loss for problems like "cannot reach Telegram".
/// </para>
/// </remarks>
public sealed class RedactedException : Exception
{
    /// <summary>
    /// 建立替身。
    /// Creates the stand-in.
    /// </summary>
    public RedactedException()
    {
    }

    /// <summary>
    /// 以訊息建立替身。
    /// Creates the stand-in with a message.
    /// </summary>
    /// <param name="message">訊息。The message.</param>
    public RedactedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 以訊息與內層例外建立替身。本套件自己從不使用這個建構式 —— 帶內層例外就違背了這個型別存在的理由。
    /// Creates the stand-in with a message and an inner exception. This package never uses this constructor
    /// itself: carrying an inner exception defeats the purpose of the type.
    /// </summary>
    /// <param name="message">訊息。The message.</param>
    /// <param name="innerException">內層例外。The inner exception.</param>
    public RedactedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal RedactedException(string originalExceptionType, string redactedMessage, string redactedDetails)
        : base(redactedMessage)
    {
        OriginalExceptionType = originalExceptionType;
        RedactedDetails = redactedDetails;
    }

    /// <summary>
    /// 原始例外的完整型別名稱,例如 <c>System.Net.Http.HttpRequestException</c>。
    /// The original exception's full type name, such as <c>System.Net.Http.HttpRequestException</c>.
    /// </summary>
    public string? OriginalExceptionType { get; }

    /// <summary>
    /// 原始例外遮罩後的完整文字(含內層例外與堆疊追蹤)。
    /// The original exception's full text, redacted, inner exceptions and stack trace included.
    /// </summary>
    public string? RedactedDetails { get; }

    /// <summary>
    /// 回傳包含遮罩後原始細節的文字,讓日誌框架照常呼叫 <c>ToString()</c> 時仍拿得到診斷資訊。
    /// Returns text that includes the redacted original details, so a logging framework calling
    /// <c>ToString()</c> as usual still gets the diagnostics.
    /// </summary>
    /// <returns>例外的文字表示。The exception's text representation.</returns>
    public override string ToString() =>
        RedactedDetails is null
            ? base.ToString()
            : $"{GetType().FullName}: {Message}{Environment.NewLine}--- 原始例外(已遮罩) Original exception (redacted): {OriginalExceptionType} ---{Environment.NewLine}{RedactedDetails}";
}
