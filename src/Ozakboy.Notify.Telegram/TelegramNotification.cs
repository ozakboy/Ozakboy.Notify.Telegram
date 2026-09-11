namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 一則要送出的通知:等級加上純文字內容。
/// One notification to send: a level plus plain-text content.
/// </summary>
/// <remarks>
/// 內容一律以純文字送出,任何字元(底線、星號、角括號)都原樣呈現,不需要也不應該自行跳脫。
/// 過長的內容會自動依行切成多則,見 <see cref="TelegramNotifier"/>。
/// Content is always sent as plain text: every character — underscores, asterisks, angle brackets — shows up
/// as written, and there is no need to escape anything (doing so would show the escapes). Over-long content is
/// split into several messages along line breaks; see <see cref="TelegramNotifier"/>.
/// </remarks>
public sealed record TelegramNotification
{
    /// <summary>
    /// 建立通知。
    /// Creates a notification.
    /// </summary>
    /// <param name="level">等級。The level.</param>
    /// <param name="text">內容。可為空字串,但不可為 <see langword="null"/>。The content; may be empty but not <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> 不是已定義的等級時擲出。
    /// Thrown when <paramref name="level"/> is not a defined level.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public TelegramNotification(NotificationLevel level, string text)
    {
        // 未定義的等級(例如從整數硬轉來的值)沒有對應的標記,與其猜一個,不如在建立時就擋下 ——
        // 猜成 Info 會讓一則真正的告警看起來無關緊要。
        // An undefined level (say, an integer cast) has no marker. Rejecting it here beats guessing: guessing
        // Info would make a real alert look harmless.
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "通知等級未定義。The notification level is not defined.");
        }

        ArgumentNullException.ThrowIfNull(text);

        Level = level;
        Text = text;
    }

    /// <summary>
    /// 等級。
    /// The level.
    /// </summary>
    public NotificationLevel Level { get; }

    /// <summary>
    /// 純文字內容。
    /// The plain-text content.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// 建立 <see cref="NotificationLevel.Info"/> 等級的通知。
    /// Creates an <see cref="NotificationLevel.Info"/> notification.
    /// </summary>
    /// <param name="text">內容。The content.</param>
    /// <returns>通知。The notification.</returns>
    public static TelegramNotification Info(string text) => new(NotificationLevel.Info, text);

    /// <summary>
    /// 建立 <see cref="NotificationLevel.Warning"/> 等級的通知。
    /// Creates a <see cref="NotificationLevel.Warning"/> notification.
    /// </summary>
    /// <param name="text">內容。The content.</param>
    /// <returns>通知。The notification.</returns>
    public static TelegramNotification Warning(string text) => new(NotificationLevel.Warning, text);

    /// <summary>
    /// 建立 <see cref="NotificationLevel.Critical"/> 等級的通知。
    /// Creates a <see cref="NotificationLevel.Critical"/> notification.
    /// </summary>
    /// <param name="text">內容。The content.</param>
    /// <returns>通知。The notification.</returns>
    public static TelegramNotification Critical(string text) => new(NotificationLevel.Critical, text);
}
