using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ozakboy.Notify.Telegram.Tests.TestSupport;

namespace Ozakboy.Notify.Telegram.Tests;

/// <summary>
/// 送出內容與錯誤對映。
/// What goes out, and how failures are mapped.
/// </summary>
[TestClass]
public sealed class TelegramNotifierSendTests
{
    [TestMethod]
    public async Task SendAsync_Success_PostsPlainTextJsonToSendMessage()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));

        var result = await harness.SendAsync(NotificationLevel.Warning, "margin *low* on BTC_USDT");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(1, harness.Handler.CallCount);

        var request = harness.Handler.Requests[0];
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(TestValues.SendMessageUri, request.Uri);
        StringAssert.StartsWith(request.ContentType!, "application/json");

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.AreEqual(TestValues.ChatId, root.GetProperty("chat_id").GetString());
        Assert.AreEqual("[WARNING] margin *low* on BTC_USDT", root.GetProperty("text").GetString());
        Assert.IsFalse(root.TryGetProperty("parse_mode", out _), "純文字:不可設定 parse_mode。Plain text: parse_mode must not be set.");
        Assert.IsTrue(root.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [TestMethod]
    public async Task SendAsync_WithBaseAddressWithoutTrailingSlash_StillTargetsSendMessage()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody),
            options => options.ApiBaseAddress = new Uri("http://localhost:8081/telegram"));

        var result = await harness.SendAsync(NotificationLevel.Info, "hi");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual($"http://localhost:8081/telegram/bot{TestValues.BotToken}/sendMessage", harness.Handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task SendAsync_LongText_SendsEveryPartInOrder()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));
        var text = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"row {i} " + new string('z', 40)));

        var result = await harness.SendAsync(NotificationLevel.Critical, text);

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.IsTrue(harness.Handler.CallCount > 1);
        for (var i = 0; i < harness.Handler.CallCount; i++)
        {
            using var body = JsonDocument.Parse(harness.Handler.Requests[i].Body!);
            StringAssert.StartsWith(body.RootElement.GetProperty("text").GetString()!, $"[CRITICAL] ({i + 1}/{harness.Handler.CallCount})");
        }
    }

    [TestMethod]
    [DataRow(401, "Unauthorized", TelegramErrorCodes.Unauthorized, ErrorCategory.Unauthorized)]
    [DataRow(403, "Forbidden: bot was kicked from the group chat", TelegramErrorCodes.Forbidden, ErrorCategory.Forbidden)]
    [DataRow(403, "Forbidden: bot was blocked by the user", TelegramErrorCodes.Forbidden, ErrorCategory.Forbidden)]
    [DataRow(400, "Bad Request: chat not found", TelegramErrorCodes.ChatNotFound, ErrorCategory.NotFound)]
    [DataRow(400, "Bad Request: message text is empty", TelegramErrorCodes.BadRequest, ErrorCategory.Validation)]
    [DataRow(404, "Not Found", TelegramErrorCodes.NotFound, ErrorCategory.NotFound)]
    public async Task SendAsync_PermanentFailure_IsMappedAndNeverResent(int status, string description, string expectedCode, ErrorCategory expectedCategory)
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always((HttpStatusCode)status, ScriptedTelegramHandler.ErrorBody(status, description)));

        var result = await harness.SendAsync(NotificationLevel.Critical, "x");

        Assert.IsTrue(result.IsFailure);
        var error = result.Error!;
        Assert.AreEqual(expectedCode, error.Code);
        Assert.AreEqual(expectedCategory, error.Category);
        Assert.IsFalse(error.IsTransient, "401/403/400/404 不是暫時性的。These are not transient.");
        Assert.AreEqual(1, harness.Handler.CallCount, "永久性失敗不可重送。A permanent failure must not be resent.");
        Assert.IsTrue(error.TryGetInt64(TelegramErrorDataKeys.StatusCode, out var recorded));
        Assert.AreEqual(status, recorded);
        Assert.IsTrue(error.TryGetData(TelegramErrorDataKeys.Description, out var recordedDescription));
        Assert.AreEqual(description, recordedDescription);
    }

    [TestMethod]
    public async Task SendAsync_ChatMigrated_ReportsTheNewChatId()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(
            HttpStatusCode.BadRequest,
            ScriptedTelegramHandler.ErrorBody(400, "Bad Request: group chat was upgraded to a supergroup chat", "\"migrate_to_chat_id\":-1009876543210")));

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.AreEqual(TelegramErrorCodes.ChatMigrated, result.Error!.Code);
        Assert.IsTrue(result.Error.TryGetInt64(TelegramErrorDataKeys.MigrateToChatId, out var newChatId));
        Assert.AreEqual(-1009876543210, newChatId);
    }

    [TestMethod]
    public async Task SendAsync_SuccessStatusWithoutOk_IsNotTakenAsDeliveredAndNotResent()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, "<html>captive portal</html>"));

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.AreEqual(TelegramErrorCodes.UnexpectedResponse, result.Error!.Code);
        Assert.AreEqual(1, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_PartFails_StopsAndReportsHowManyWereDelivered()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Sequence(
            (HttpStatusCode.OK, ScriptedTelegramHandler.OkBody),
            (HttpStatusCode.Forbidden, ScriptedTelegramHandler.ErrorBody(403, "Forbidden: bot was kicked"))));

        var result = await harness.SendAsync(NotificationLevel.Critical, new string('q', 12_000));

        Assert.AreEqual(TelegramErrorCodes.Forbidden, result.Error!.Code);
        Assert.AreEqual(2, harness.Handler.CallCount, "失敗之後不可再送後面的分段。No part may follow a failed one.");
        Assert.IsTrue(result.Error.TryGetInt64(TelegramErrorDataKeys.PartsSent, out var sent));
        Assert.IsTrue(result.Error.TryGetInt64(TelegramErrorDataKeys.PartsTotal, out var total));
        Assert.AreEqual(1, sent);
        Assert.AreEqual(3, total);
        Assert.IsTrue(result.Error.TryGetData(TelegramErrorDataKeys.Level, out var level));
        Assert.AreEqual("Critical", level);
    }

    [TestMethod]
    public async Task SendAsync_Failure_LogsCodeButNeverChatIdOrText()
    {
        const string Text = "secret trading plan detail";
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.Unauthorized, ScriptedTelegramHandler.ErrorBody(401, "Unauthorized")));

        await harness.SendAsync(NotificationLevel.Critical, Text);

        var warning = harness.Logs.Entries.Single(entry => entry.Level == LogLevel.Warning);
        StringAssert.Contains(warning.Message, TelegramErrorCodes.Unauthorized);
        foreach (var surface in harness.Logs.Surfaces)
        {
            Assert.IsFalse(surface.Contains(TestValues.ChatId, StringComparison.Ordinal), "日誌不可寫入 chat id。The chat id must not be logged.");
            Assert.IsFalse(surface.Contains(Text, StringComparison.Ordinal), "日誌不可寫入訊息內容。The message text must not be logged.");
        }
    }

    [TestMethod]
    public async Task SendAsync_AfterDispose_Throws()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));
        harness.Notifier.Dispose();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => harness.Notifier.SendAsync(TelegramNotification.Info("x")));
    }

    [TestMethod]
    public async Task SendAsync_ExtensionOverload_BuildsTheNotification()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));

        ITelegramNotifier notifier = harness.Notifier;
        var result = await FakeClockRunner.RunAsync(harness.Clock, notifier.SendAsync(NotificationLevel.Info, "via extension"));

        Assert.IsTrue(result.IsSuccess, result.ToString());
        StringAssert.Contains(harness.Handler.Requests[0].Body!, "[INFO] via extension");
    }
}
