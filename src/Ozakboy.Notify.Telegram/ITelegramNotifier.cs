using Ozakboy.Core.Abstractions;

namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 送出 Telegram 通知的契約。
/// The contract for sending Telegram notifications.
/// </summary>
/// <remarks>
/// 以介面公開是為了讓呼叫端的測試能換成假實作:告警邏輯本身需要被測試(該不該發、發哪一級),
/// 但那些測試不該真的連到 Telegram,也不該需要一把權杖。
/// It is exposed as an interface so that callers' tests can substitute a fake: alerting logic itself needs
/// testing — whether to alert, at which level — but those tests should neither reach Telegram nor need a token.
/// </remarks>
public interface ITelegramNotifier
{
    /// <summary>
    /// 送出一則通知。內容過長時會切成多則依序送出。
    /// Sends one notification, split into several messages sent in order when the content is too long.
    /// </summary>
    /// <param name="notification">通知。The notification.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 全部送達時為成功;否則為失敗,錯誤代碼見 <see cref="TelegramErrorCodes"/>。這個方法不會因預期中的失敗擲出例外。
    /// Success when every part was delivered; otherwise a failure whose code is one of
    /// <see cref="TelegramErrorCodes"/>. Expected failures never throw.
    /// </returns>
    Task<Result> SendAsync(TelegramNotification notification, CancellationToken cancellationToken = default);
}
