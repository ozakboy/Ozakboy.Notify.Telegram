using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ozakboy.Notify.Telegram.Tests.TestSupport;

namespace Ozakboy.Notify.Telegram.Tests;

/// <summary>
/// 憑證外洩測試:以一把可辨識的假權杖跑完整的注入流程(包括 <c>IHttpClientFactory</c> 的預設日誌管線),
/// 收集所有日誌表面與所有回傳錯誤的每個欄位,斷言權杖一次都沒出現。
/// Credential-leak tests: a recognisable fake token runs through the full injected pipeline —
/// <c>IHttpClientFactory</c>'s default logging included — and every logging surface plus every field of every
/// returned error is collected and checked for the token.
/// </summary>
/// <remarks>
/// <para>
/// 每個情境都故意把最壞的狀況做出來:連線例外的訊息與內層例外直接帶著完整位址、錯誤回應的說明文字回聲位址。
/// 真實世界的例外很少這麼做,但「很少」不是保證,測試要驗的是那個保證。
/// Each scenario stages the worst case on purpose: a transport exception whose message and inner exception carry
/// the full URI, and an error description that echoes it back. Real exceptions rarely do this, but "rarely" is
/// not a guarantee, and the guarantee is what these tests check.
/// </para>
/// <para>
/// 已做過的故意失敗驗證(見回報):拿掉 <c>RemoveAllLoggers()</c> 後所有情境變紅(factory 預設日誌寫出位址);
/// 讓遮罩器保留原始例外後,帶位址的連線例外情境變紅。
/// Deliberate-failure checks carried out (see the report): removing <c>RemoveAllLoggers()</c> turns every
/// scenario red (the factory's default logging writes the URI); letting the redactor keep the original exception
/// turns the URI-bearing transport-exception scenarios red.
/// </para>
/// </remarks>
[TestClass]
public sealed class CredentialLeakTests
{
    [TestMethod]
    [DataRow("success", null)]
    [DataRow("unauthorized401", TelegramErrorCodes.Unauthorized)]
    [DataRow("chatNotFound400", TelegramErrorCodes.ChatNotFound)]
    [DataRow("forbidden403", TelegramErrorCodes.Forbidden)]
    [DataRow("errorDescriptionEchoesUri", TelegramErrorCodes.BadRequest)]
    [DataRow("rateLimited429ThenOk", null)]
    [DataRow("rateLimited429TooLong", TelegramErrorCodes.RateLimited)]
    [DataRow("serverError5xx", TelegramErrorCodes.Unavailable)]
    [DataRow("timeout", TelegramErrorCodes.Timeout)]
    [DataRow("networkExceptionEchoesUri", TelegramErrorCodes.Network)]
    [DataRow("longMessageSplit", null)]
    public async Task FullPipeline_Scenario_NeverExposesToken(string scenario, string? expectedCode)
    {
        var capture = new LogCapture();
        var clock = new FakeTimeProvider();
        var handler = new ScriptedTelegramHandler(Responder(scenario), clock);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(new CapturingLoggerProvider(capture)));
        services.AddTelegramNotifier(options =>
        {
            options.BotToken = TestValues.BotToken;
            options.ChatId = TestValues.ChatId;
            options.RequestTimeout = scenario == "timeout" ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(10);
            options.ThrottleWaitLimit = TimeSpan.FromMinutes(10);
            options.TransientRetry = new RetryPolicy
            {
                MaxAttempts = 2,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                JitterRatio = 0d,
            };
        });

        // 只替換最內層的處理器,factory 自己的日誌處理器(若沒被移除)仍然在它外面。
        // Only the innermost handler is replaced; the factory's own logging handlers, if not removed, still wrap it.
        services.AddHttpClient(TelegramNotifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<ITelegramNotifier>();

        var text = scenario == "longMessageSplit" ? new string('x', 9000) : "leak probe";
        var result = await FakeClockRunner.RunAsync(clock, notifier.SendAsync(TelegramNotification.Critical(text)));

        // 前提:假處理器確實收到了帶權杖的位址。若這裡不成立,底下的斷言什麼也沒證明。
        // Precondition: the stub really did receive the token-bearing URI; otherwise the assertions below prove
        // nothing.
        Assert.IsTrue(handler.CallCount > 0, "假處理器沒有收到任何請求。The stub received no request.");
        Assert.IsTrue(
            handler.Requests.All(request => request.Uri.Contains(TestValues.TokenSecretPart, StringComparison.Ordinal)),
            "請求位址應帶權杖。The request URI should carry the token.");

        if (expectedCode is null)
        {
            Assert.IsTrue(result.IsSuccess, result.ToString());
        }
        else
        {
            Assert.AreEqual(expectedCode, result.Error!.Code, "情境沒有照預期發生。The scenario did not play out as intended.");
        }

        var surfaces = capture.Surfaces.ToList();
        if (result.IsFailure)
        {
            surfaces.AddRange(ErrorSurfaces(result.Error!));
            AssertNoRawException(result.Error!);
        }

        Assert.IsTrue(capture.Entries.Count > 0, "至少應有一筆日誌,否則沒驗到日誌路徑。At least one log entry is expected.");
        for (var i = 0; i < surfaces.Count; i++)
        {
            var surface = surfaces[i];

            // 失敗訊息裡不印出原文(即使是假權杖也照規矩):只說是第幾個表面、權杖出現在哪個位置。
            // The failure message does not print the text itself (even a fake token gets the discipline): it names
            // the surface and the offset only.
            var at = surface.IndexOf(TestValues.TokenSecretPart, StringComparison.Ordinal);
            Assert.AreEqual(-1, at, $"權杖出現在第 {i} 個表面的位置 {at}。The token appeared in surface {i} at offset {at}: {Describe(surface)}");
            Assert.IsFalse(surface.Contains(TestValues.ChatId, StringComparison.Ordinal) && !IsErrorSurface(i, capture), $"chat id 不應出現在日誌第 {i} 個表面。The chat id should not appear in log surface {i}.");
        }
    }

    [TestMethod]
    public async Task DirectClientConstructor_NetworkExceptionEchoingUri_NeverExposesToken()
    {
        using var harness = new NotifierHarness(
            (request, _, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"send failed for {request.RequestUri}", new IOException($"socket error at {request.RequestUri!.AbsolutePath}"))));

        var result = await harness.SendAsync(NotificationLevel.Critical, "x");

        Assert.IsTrue(result.IsFailure);
        foreach (var surface in ErrorSurfaces(result.Error!).Concat(harness.Logs.Surfaces))
        {
            Assert.IsFalse(surface.Contains(TestValues.TokenSecretPart, StringComparison.Ordinal), Describe(surface));
        }

        AssertNoRawException(result.Error!);
    }

    private static Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Responder(string scenario) => scenario switch
    {
        "success" or "longMessageSplit" => ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody),
        "unauthorized401" => ScriptedTelegramHandler.Always(HttpStatusCode.Unauthorized, ScriptedTelegramHandler.ErrorBody(401, "Unauthorized")),
        "chatNotFound400" => ScriptedTelegramHandler.Always(HttpStatusCode.BadRequest, ScriptedTelegramHandler.ErrorBody(400, "Bad Request: chat not found")),
        "forbidden403" => ScriptedTelegramHandler.Always(HttpStatusCode.Forbidden, ScriptedTelegramHandler.ErrorBody(403, "Forbidden: bot was kicked from the group chat")),
        "errorDescriptionEchoesUri" => (request, _, _) => Task.FromResult(ScriptedTelegramHandler.Json(
            HttpStatusCode.BadRequest,
            ScriptedTelegramHandler.ErrorBody(400, $"Bad Request: cannot handle {request.RequestUri}"))),
        "rateLimited429ThenOk" => ScriptedTelegramHandler.Sequence(
            (HttpStatusCode.TooManyRequests, ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests: retry after 3", "\"retry_after\":3")),
            (HttpStatusCode.OK, ScriptedTelegramHandler.OkBody)),
        "rateLimited429TooLong" => ScriptedTelegramHandler.Always(
            HttpStatusCode.TooManyRequests,
            ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests: retry after 3600", "\"retry_after\":3600")),
        "serverError5xx" => (request, _, _) => Task.FromResult(ScriptedTelegramHandler.Json(
            HttpStatusCode.BadGateway,
            $"<html>upstream {request.RequestUri} failed</html>")),
        "timeout" => async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ScriptedTelegramHandler.Json(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody);
        },
        "networkExceptionEchoesUri" => (request, _, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(
                $"An error occurred while sending the request to {request.RequestUri}.",
                new IOException($"Unable to write data to the transport connection for {request.RequestUri}."))),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    /// <summary>
    /// 錯誤的每一個欄位,連同例外的完整文字與替身保存的原始細節。
    /// Every field of an error, along with the exception's full text and the details the stand-in keeps.
    /// </summary>
    private static IEnumerable<string> ErrorSurfaces(Error error)
    {
        yield return error.Code;
        yield return error.Message;
        yield return error.ToString();

        if (error.Data is not null)
        {
            foreach (var entry in error.Data)
            {
                yield return entry.Key;
                yield return entry.Value;
            }
        }

        if (error.Exception is not null)
        {
            yield return error.Exception.ToString();
            yield return error.Exception.Message;
            if (error.Exception is RedactedException redacted)
            {
                yield return redacted.RedactedDetails ?? string.Empty;
                yield return redacted.OriginalExceptionType ?? string.Empty;
            }
        }
    }

    private static void AssertNoRawException(Error error)
    {
        if (error.Exception is null)
        {
            return;
        }

        Assert.IsInstanceOfType<RedactedException>(error.Exception, "錯誤裡只能有遮罩後的替身例外。Only the redacted stand-in may appear in an error.");
        Assert.IsNull(error.Exception.InnerException, "替身不可帶內層例外。The stand-in must not carry an inner exception.");
    }

    // chat id 出現在錯誤資料是允許的(例如新的 chat id),只有日誌不允許。日誌表面排在前面。
    // A chat id in error data is allowed (a new chat id, say); only logs must not carry it. Log surfaces come first.
    private static bool IsErrorSurface(int index, LogCapture capture) => index >= capture.Surfaces.Count();

    private static string Describe(string surface)
    {
        var shown = surface.Replace(TestValues.TokenSecretPart, "<TOKEN-SECRET>", StringComparison.Ordinal);
        return shown.Length > 300 ? new StringBuilder(shown, 0, 300, 300).Append('…').ToString() : shown;
    }
}
