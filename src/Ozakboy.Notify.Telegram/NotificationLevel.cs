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
/// <see cref="Critical"/> 走優先通道:在節流佇列裡永遠排在所有 <see cref="Info"/> / <see cref="Warning"/> 前面,
/// 下一個可用額度就是它的(需求 FR-12:Critical 5 秒內送達)。但它<b>不會繞過節流</b>,也要等 429 暫停結束 ——
/// 繞過節流只會換來 Telegram 的 429,讓它比排隊更晚送達。Info 與 Warning 之間沒有先後,依抵達順序。
/// A <see cref="Critical"/> message takes the priority lane: it is always ahead of every queued
/// <see cref="Info"/> and <see cref="Warning"/> and gets the next free slot (FR-12: Critical within 5 seconds).
/// It does <b>not</b> bypass throttling or a 429 pause, though: bypassing throttling only earns a 429 from
/// Telegram and arrives later than queueing would. Info and Warning have no precedence over each other; they go
/// in arrival order.
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
