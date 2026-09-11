namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 通知等級。顯示在每則訊息的最開頭,讓收件人在手機通知列上就知道要不要立刻處理。
/// The notification level. It opens every message, so the recipient can tell from the phone's notification
/// shade alone whether to act now.
/// </summary>
/// <remarks>
/// <para>
/// 只分三級是刻意的。等級一多,收件人就得記住每一級代表什麼,半夜被叫醒時沒人有那個餘裕;
/// 三級對應的是三種反應:看看就好、今天之內處理、立刻處理。
/// Three levels is deliberate. More levels mean the recipient has to remember what each one means, and nobody
/// woken at night has the capacity for that; three levels map onto three reactions: glance at it, deal with
/// it today, deal with it now.
/// </para>
/// <para>
/// 等級只影響訊息開頭的標記,不影響送出方式:<see cref="Critical"/> 不會插隊、也不會繞過節流 ——
/// 繞過節流只會換來 Telegram 的 429,讓它比排隊更晚送達。
/// The level only changes the marker at the start of the message, not how it is sent: a
/// <see cref="Critical"/> message does not jump the queue or bypass throttling, because bypassing throttling
/// only earns a 429 from Telegram and arrives later than queueing would have.
/// </para>
/// </remarks>
public enum NotificationLevel
{
    /// <summary>
    /// 一般資訊,例如開平倉回報、每日摘要。訊息開頭為 <c>[INFO]</c>。
    /// Routine information, such as fills or a daily summary. Messages start with <c>[INFO]</c>.
    /// </summary>
    Info = 0,

    /// <summary>
    /// 需要留意但不必立刻處理,例如重連次數偏多。訊息開頭為 <c>[WARNING]</c>。
    /// Worth attention but not urgent, such as an elevated reconnect count. Messages start with
    /// <c>[WARNING]</c>.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// 必須立刻處理,例如進入安全模式、金鑰被拒。訊息開頭為 <c>[CRITICAL]</c>。
    /// Needs action now, such as entering safe mode or a rejected API key. Messages start with
    /// <c>[CRITICAL]</c>.
    /// </summary>
    Critical = 2,
}
