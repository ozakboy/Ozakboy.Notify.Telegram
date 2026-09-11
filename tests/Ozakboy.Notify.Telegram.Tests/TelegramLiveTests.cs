using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ozakboy.Notify.Telegram.Tests.TestSupport;

namespace Ozakboy.Notify.Telegram.Tests;

/// <summary>
/// 對真實 Telegram 的整合測試。只有在環境變數 <c>TELEGRAM_BOT_TOKEN</c> 與 <c>TELEGRAM_CHAT_ID</c> 都存在時才真的送出。
/// Integration tests against the real Telegram. They only send when both <c>TELEGRAM_BOT_TOKEN</c> and
/// <c>TELEGRAM_CHAT_ID</c> are set in the environment.
/// </summary>
/// <remarks>
/// 權杖只從環境變數取得,不讀任何設定檔:測試專案的設定檔最容易被順手提交。沒有設定時以 Inconclusive 收場,
/// 讓 CI(本來就不該持有真的權杖)照常全綠,同時在報告裡看得出這些測試沒有實際執行。
/// The token comes from the environment only, never from a settings file: test-project settings files are the
/// ones most easily committed in passing. Without it the tests end Inconclusive, so CI — which should never hold a
/// real token — stays green while the report still shows these tests did not actually run.
/// </remarks>
[TestClass]
public sealed class TelegramLiveTests
{
    private const string TokenVariable = "TELEGRAM_BOT_TOKEN";
    private const string ChatIdVariable = "TELEGRAM_CHAT_ID";

    [TestMethod]
    [TestCategory("TelegramLive")]
    public async Task SendAsync_AgainstRealTelegram_DeliversAndLogsNoToken()
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var chatId = Environment.GetEnvironmentVariable(ChatIdVariable);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
        {
            Assert.Inconclusive($"未設定環境變數 {TokenVariable} 與 {ChatIdVariable},略過實際送出。Set both {TokenVariable} and {ChatIdVariable} to run this test against the real Telegram.");
        }

        var capture = new LogCapture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingLoggerProvider(capture)));
        services.AddTelegramNotifier(options =>
        {
            options.BotToken = token;
            options.ChatId = chatId;
        });

        await using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ITelegramNotifier>();

        var stamp = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture);
        var result = await notifier.SendAsync(TelegramNotification.Info($"Ozakboy.Notify.Telegram 整合測試 integration test {stamp}\n底線_星號*角括號<ok> 皆為純文字。"));

        Assert.IsTrue(result.IsSuccess, result.ToString());
        foreach (var surface in capture.Surfaces)
        {
            Assert.IsFalse(surface.Contains(token!, StringComparison.Ordinal), "日誌不可含權杖。Logs must not contain the token.");
        }
    }
}
