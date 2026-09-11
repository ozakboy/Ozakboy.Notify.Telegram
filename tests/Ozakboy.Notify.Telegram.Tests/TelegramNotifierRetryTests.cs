using System.Net;
using Ozakboy.Notify.Telegram.Tests.TestSupport;

namespace Ozakboy.Notify.Telegram.Tests;

/// <summary>
/// 429、暫時性失敗、逾時、取消與節流。所有時間都走假時鐘。
/// 429s, transient failures, timeouts, cancellation, and throttling, all on a fake clock.
/// </summary>
[TestClass]
public sealed class TelegramNotifierRetryTests
{
    private static readonly string TooManyRequests5s =
        ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests: retry after 5", "\"retry_after\":5");

    [TestMethod]
    public async Task RateLimited_WaitsForRetryAfterFromBodyThenResends()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Sequence(
            (HttpStatusCode.TooManyRequests, TooManyRequests5s),
            (HttpStatusCode.OK, ScriptedTelegramHandler.OkBody)));

        var result = await harness.SendAsync(NotificationLevel.Critical, "x");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(2, harness.Handler.CallCount);
        var waited = harness.Handler.Requests[1].At - harness.Handler.Requests[0].At;
        Assert.IsTrue(waited >= TimeSpan.FromSeconds(5), $"必須照 retry_after 等滿 5 秒,實際 {waited}。Must wait the full 5 s; waited {waited}.");
    }

    [TestMethod]
    public async Task RateLimited_FallsBackToRetryAfterHeader()
    {
        using var harness = new NotifierHarness((_, attempt, _) =>
        {
            if (attempt == 1)
            {
                var limited = ScriptedTelegramHandler.Json(HttpStatusCode.TooManyRequests, ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests"));
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return Task.FromResult(limited);
            }

            return Task.FromResult(ScriptedTelegramHandler.Json(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));
        });

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.IsTrue(harness.Handler.Requests[1].At - harness.Handler.Requests[0].At >= TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task RateLimited_LongerThanAllowed_ReturnsAtOnceAndBlocksLaterSendsWithoutHittingTelegram()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(
            HttpStatusCode.TooManyRequests,
            ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests: retry after 3600", "\"retry_after\":3600")));

        var first = await harness.SendAsync(NotificationLevel.Critical, "x");
        var second = await harness.SendAsync(NotificationLevel.Critical, "y");

        Assert.AreEqual(TelegramErrorCodes.RateLimited, first.Error!.Code);
        Assert.AreEqual(ErrorCategory.RateLimited, first.Error.Category);
        Assert.IsTrue(first.Error.TryGetDecimal(TelegramErrorDataKeys.RetryAfterSeconds, out var seconds));
        Assert.AreEqual(3600m, seconds);

        // 暫停期間的下一則直接失敗、不去撞 Telegram:提早回去只會延長封鎖。
        // The next send during the pause fails without touching Telegram: coming back early only extends the
        // block.
        Assert.AreEqual(TelegramErrorCodes.RateLimited, second.Error!.Code);
        Assert.AreEqual(1, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task RateLimited_Repeatedly_GivesUpAfterConfiguredRetriesAsExhausted()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Always(HttpStatusCode.TooManyRequests, ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests: retry after 1", "\"retry_after\":1")),
            options => options.MaxRateLimitRetries = 2);

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.AreEqual(TelegramErrorCodes.RateLimited, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Exhausted, result.Error.Category);
        Assert.AreEqual(3, harness.Handler.CallCount);
        Assert.IsTrue(result.Error.TryGetInt64(TelegramErrorDataKeys.Attempts, out var attempts));
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ServerError_ThenSuccess_IsResent()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Sequence(
            (HttpStatusCode.BadGateway, "<html>502</html>"),
            (HttpStatusCode.OK, ScriptedTelegramHandler.OkBody)));

        var result = await harness.SendAsync(NotificationLevel.Critical, "x");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(2, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task ServerError_Persistent_IsExhaustedAfterPolicyAttempts()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.InternalServerError, ScriptedTelegramHandler.ErrorBody(500, "Internal Server Error")));

        var result = await harness.SendAsync(NotificationLevel.Critical, "x");

        Assert.AreEqual(TelegramErrorCodes.Unavailable, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Exhausted, result.Error.Category);
        Assert.IsFalse(result.Error.IsTransient, "已替呼叫端重試過,不可再標成暫時性。Already retried; must not read as transient.");
        Assert.AreEqual(3, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task ServerError_WithNoRetryPolicy_IsAtMostOnce()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Always(HttpStatusCode.ServiceUnavailable, string.Empty),
            options => options.TransientRetry = RetryPolicy.NoRetry);

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.AreEqual(TelegramErrorCodes.Unavailable, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Unavailable, result.Error.Category);
        Assert.AreEqual(1, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task Timeout_IsRetriedThenExhausted()
    {
        using var harness = new NotifierHarness(
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ScriptedTelegramHandler.Json(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody);
            },
            options => options.RequestTimeout = TimeSpan.FromSeconds(5));

        var result = await harness.SendAsync(NotificationLevel.Critical, "x");

        Assert.AreEqual(TelegramErrorCodes.Timeout, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Exhausted, result.Error.Category);
        Assert.AreEqual(3, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task NetworkFailure_IsRetriedAndMappedToNetwork()
    {
        using var harness = new NotifierHarness(
            (_, attempt, _) => attempt < 3
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known."))
                : Task.FromResult(ScriptedTelegramHandler.Json(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody)));

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(3, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task NetworkFailure_Persistent_CarriesARedactedExceptionOnly()
    {
        using var harness = new NotifierHarness(
            (_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        Assert.AreEqual(TelegramErrorCodes.Network, result.Error!.Code);
        Assert.IsInstanceOfType<RedactedException>(result.Error.Exception);
        var redacted = (RedactedException)result.Error.Exception!;
        Assert.IsNull(redacted.InnerException, "替身不可帶原始例外。The stand-in must not carry the original.");
        Assert.AreEqual(typeof(HttpRequestException).FullName, redacted.OriginalExceptionType);
        StringAssert.Contains(redacted.ToString(), "connection refused");
        Assert.IsTrue(result.Error.TryGetData(TelegramErrorDataKeys.TransportErrorCode, out var transportCode));
        Assert.AreEqual("http.network", transportCode);
    }

    [TestMethod]
    public async Task CallerCancellation_IsNotRetried()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new NotifierHarness(
            async (_, _, cancellationToken) =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ScriptedTelegramHandler.Json(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody);
            });

        var result = await harness.SendAsync(NotificationLevel.Info, "x", cancellation.Token);

        Assert.AreEqual(TelegramErrorCodes.Cancelled, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Cancelled, result.Error.Category);
        Assert.AreEqual(1, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task AlreadyCancelled_SendsNothing()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));

        var result = await harness.SendAsync(NotificationLevel.Info, "x", new CancellationToken(canceled: true));

        Assert.AreEqual(TelegramErrorCodes.Cancelled, result.Error!.Code);
        Assert.AreEqual(0, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task Throttling_SpacesMessagesToTheSameChat()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));

        await harness.SendAsync(NotificationLevel.Info, "one");
        await harness.SendAsync(NotificationLevel.Info, "two");

        var gap = harness.Handler.Requests[1].At - harness.Handler.Requests[0].At;
        Assert.IsTrue(gap >= TimeSpan.FromSeconds(1), $"同一聊天兩則之間至少 1 秒,實際 {gap}。At least 1 s apart; got {gap}.");
    }

    [TestMethod]
    public async Task Throttling_CapsMessagesPerMinute()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody),
            options => options.MaxMessagesPerMinute = 3);

        for (var i = 0; i < 4; i++)
        {
            var result = await harness.SendAsync(NotificationLevel.Info, $"burst {i}", TimeSpan.FromSeconds(1));
            Assert.IsTrue(result.IsSuccess, result.ToString());
        }

        var span = harness.Handler.Requests[3].At - harness.Handler.Requests[0].At;
        Assert.IsTrue(span >= TimeSpan.FromMinutes(1), $"第 4 則必須等到下一分鐘,實際 {span}。The 4th must wait for the next minute; got {span}.");
    }

    [TestMethod]
    public async Task Throttling_BeyondWaitLimit_FailsWithoutSending()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody),
            options =>
            {
                options.MaxMessagesPerMinute = 1;
                options.ThrottleWaitLimit = TimeSpan.FromSeconds(5);
            });

        var first = await harness.SendAsync(NotificationLevel.Info, "one");
        var second = await harness.SendAsync(NotificationLevel.Critical, "two");

        Assert.IsTrue(first.IsSuccess, first.ToString());
        Assert.AreEqual(TelegramErrorCodes.Throttled, second.Error!.Code);
        Assert.AreEqual(1, harness.Handler.CallCount);
    }

    [TestMethod]
    public async Task Throttling_AppliesToResendsToo()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Sequence(
                (HttpStatusCode.TooManyRequests, ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests")),
                (HttpStatusCode.OK, ScriptedTelegramHandler.OkBody)));

        var result = await harness.SendAsync(NotificationLevel.Info, "x");

        // 沒有 retry_after 的 429 立即重送,但仍要付節流額度:它不能和前一則落在同一個節流時間窗裡。
        // 斷言「落在後一個時間窗」而不是「相隔至少一秒」:節流用的是固定時間窗,邊界從通知器建立的那一刻起算,
        // 第一則若沒有剛好落在邊界上,下一個邊界本來就不滿一秒 —— 那是 README 寫明的已知限制,
        // 不是重送沒有付額度。寫成「相隔至少一秒」時,只有第一則剛好落在起始邊界才成立,
        // 於是會隨著冷啟動多花的時間偶發變紅(假時鐘在第一則送出前就被推進了)。
        // A 429 without retry_after is resent at once, but still pays for a throttling slot: it cannot fall in the
        // same throttling window as the message before it. The assertion is "in a later window" rather than "at
        // least a second apart" because throttling uses fixed windows anchored at the notifier's construction:
        // unless the first message lands exactly on a boundary, the next boundary is less than a second away — the
        // known limitation stated in the README, not a resend that skipped its slot. Phrased as "at least a second
        // apart", it held only when the first message landed on the opening boundary, and went red at random as
        // cold-start time pushed the fake clock on before the first message went out.
        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(2, harness.Handler.CallCount);

        var firstWindow = WindowIndex(harness, harness.Handler.Requests[0].At);
        var resendWindow = WindowIndex(harness, harness.Handler.Requests[1].At);
        Assert.IsTrue(
            resendWindow > firstWindow,
            $"重送必須落在後一個節流時間窗:第一則在第 {firstWindow} 窗,重送在第 {resendWindow} 窗。The resend must fall in a later throttling window; got {firstWindow} and {resendWindow}.");
    }

    /// <summary>
    /// 這個時間點落在第幾個節流時間窗。窗從通知器建立的那一刻起算,每個 <c>MinimumInterval</c>(測試預設 1 秒)一個。
    /// Which throttling window an instant falls in. Windows start when the notifier is created, one per
    /// <c>MinimumInterval</c> (one second in these tests).
    /// </summary>
    private static long WindowIndex(NotifierHarness harness, DateTimeOffset at) =>
        (at - harness.StartedAt).Ticks / TimeSpan.FromSeconds(1).Ticks;
}
