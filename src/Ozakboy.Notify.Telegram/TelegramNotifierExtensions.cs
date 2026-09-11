using Ozakboy.Core.Abstractions;

namespace Ozakboy.Notify.Telegram;

/// <summary>
/// <see cref="ITelegramNotifier"/> 的便利多載。
/// Convenience overloads for <see cref="ITelegramNotifier"/>.
/// </summary>
/// <remarks>
/// 寫成擴充方法而不是放進介面:介面只留一個方法,呼叫端的假實作就只需要實作一個方法。
/// These are extension methods rather than interface members so the interface keeps a single method, and a
/// caller's fake implementation only has one method to implement.
/// </remarks>
public static class TelegramNotifierExtensions
{
    /// <summary>
    /// 以等級與內容送出一則通知。
    /// Sends a notification from a level and its content.
    /// </summary>
    /// <param name="notifier">通知器。The notifier.</param>
    /// <param name="level">等級。The level.</param>
    /// <param name="text">內容。The content.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>與 <see cref="ITelegramNotifier.SendAsync"/> 相同。The same as <see cref="ITelegramNotifier.SendAsync"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="notifier"/> 或 <paramref name="text"/> 為 <see langword="null"/> 時擲出。
    /// Thrown when <paramref name="notifier"/> or <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    public static Task<Result> SendAsync(
        this ITelegramNotifier notifier,
        NotificationLevel level,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notifier);

        return notifier.SendAsync(new TelegramNotification(level, text), cancellationToken);
    }
}
