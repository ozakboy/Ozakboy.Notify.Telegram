using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Ozakboy.Notify.Telegram.Tests.TestSupport;

namespace Ozakboy.Notify.Telegram.Tests;

/// <summary>
/// Critical 優先通道:排在 Info / Warning 前面、只影響順序、不繞過速率限制與 429 暫停。
/// The Critical priority lane: ahead of Info and Warning, affecting order only, never bypassing the rate limits
/// or a 429 pause.
/// </summary>
/// <remarks>
/// 排隊用的 <c>SendAsync</c> 呼叫在第一個 await 之前就已經同步進入閘門,所以「先呼叫的先排隊」是確定的;
/// 假時鐘在推進器開始跑之前完全不動,只有初始的那一個額度會被放行。
/// A <c>SendAsync</c> call enters the gate synchronously before its first await, so "called first, queued first"
/// is deterministic; the fake clock does not move until the runner starts, so only the initial slot is granted.
/// </remarks>
[TestClass]
public sealed class PriorityLaneTests
{
    private static readonly string[] ExpectedAfterTenInfo =
        ["[INFO] info 0", "[CRITICAL] critical", .. Enumerable.Range(1, 9).Select(i => $"[INFO] info {i}")];

    private static readonly string[] ExpectedMixedOrder = ["[INFO] i0", "[CRITICAL] c", "[WARNING] w1", "[INFO] i2", "[WARNING] w3"];

    private static readonly string[] ExpectedAfterPause = ["[INFO] a", "[CRITICAL] k", "[INFO] a", "[INFO] b", "[INFO] c"];

    [TestMethod]
    public async Task Critical_QueuedBehindTenInfo_IsSentNextWithinOneInterval()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));
        var sends = new List<Task<Result>>();
        for (var i = 0; i < 10; i++)
        {
            sends.Add(harness.Notifier.SendAsync(TelegramNotification.Info($"info {i}")));
        }

        // 第一則 Info 用掉初始額度、已經在送;其餘 9 則在排隊。
        // The first Info took the initial slot and is being sent; the other nine are queued.
        await WaitUntilAsync(() => harness.Handler.CallCount == 1 && harness.Notifier.QueuedCount == 9);

        var criticalQueuedAt = harness.Clock.GetUtcNow();
        sends.Add(harness.Notifier.SendAsync(TelegramNotification.Critical("critical")));

        var results = await FakeClockRunner.RunAsync(harness.Clock, Task.WhenAll(sends), FakeClockRunner.DefaultStep);

        Assert.IsTrue(results.All(result => result.IsSuccess));
        var texts = Texts(harness);
        CollectionAssert.AreEqual(
            ExpectedAfterTenInfo,
            texts,
            "Critical 必須是下一則,Info 之間的順序不變。The Critical must be next, with the Info order unchanged.");

        // 一次節流間隔內送出;容許多一個假時鐘步長,因為放行與假處理器記錄時間之間,推進器可能又推了一步。
        // Sent within one throttling interval, allowing one extra fake-clock step: the runner may advance once
        // between the admission and the stub recording the time.
        var waited = harness.Handler.Requests[1].At - criticalQueuedAt;
        Assert.IsTrue(
            waited <= TimeSpan.FromSeconds(1) + FakeClockRunner.DefaultStep,
            $"Critical 應在一個節流間隔內送出,實際等了 {waited}。The Critical should go within one interval; waited {waited}.");

        // 優先不繞過速率限制:任兩則之間仍至少間隔一秒。
        // Priority does not bypass the rate limit: any two messages are still at least one second apart.
        AssertSpacedByAtLeast(harness, TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task InfoAndWarning_KeepArrivalOrderAmongThemselves()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody));
        var sends = new List<Task<Result>>
        {
            harness.Notifier.SendAsync(TelegramNotification.Info("i0")),
            harness.Notifier.SendAsync(TelegramNotification.Warning("w1")),
            harness.Notifier.SendAsync(TelegramNotification.Info("i2")),
            harness.Notifier.SendAsync(TelegramNotification.Warning("w3")),
        };
        await WaitUntilAsync(() => harness.Handler.CallCount == 1 && harness.Notifier.QueuedCount == 3);
        sends.Add(harness.Notifier.SendAsync(TelegramNotification.Critical("c")));

        await FakeClockRunner.RunAsync(harness.Clock, Task.WhenAll(sends), FakeClockRunner.DefaultStep);

        CollectionAssert.AreEqual(ExpectedMixedOrder, Texts(harness));
    }

    [TestMethod]
    public async Task AfterA429Pause_TheFirstMessageOutIsTheCritical()
    {
        using var harness = new NotifierHarness(ScriptedTelegramHandler.Sequence(
            (HttpStatusCode.TooManyRequests, ScriptedTelegramHandler.ErrorBody(429, "Too Many Requests: retry after 5", "\"retry_after\":5")),
            (HttpStatusCode.OK, ScriptedTelegramHandler.OkBody)));

        var sends = new List<Task<Result>> { harness.Notifier.SendAsync(TelegramNotification.Info("a")) };

        // 第一則被 429 拒收,帶著原本的等級重新排隊;此時暫停期已經開始。
        // The first message was refused with a 429 and has queued again at its own level; the pause has begun.
        await WaitUntilAsync(() => harness.Handler.CallCount == 1 && harness.Notifier.QueuedCount == 1);
        var pausedAt = harness.Handler.Requests[0].At;

        sends.Add(harness.Notifier.SendAsync(TelegramNotification.Info("b")));
        sends.Add(harness.Notifier.SendAsync(TelegramNotification.Info("c")));
        sends.Add(harness.Notifier.SendAsync(TelegramNotification.Critical("k")));

        var results = await FakeClockRunner.RunAsync(harness.Clock, Task.WhenAll(sends), FakeClockRunner.DefaultStep);

        Assert.IsTrue(results.All(result => result.IsSuccess));
        CollectionAssert.AreEqual(
            ExpectedAfterPause,
            Texts(harness),
            "暫停結束後第一個送出的必須是 Critical,之後的 Info 依原順序。The Critical must be first after the pause, then the Info in order.");

        // Critical 也等滿了暫停期,沒有為了搶快去撞 Telegram。
        // The Critical waited out the pause too, rather than rushing Telegram.
        Assert.IsTrue(harness.Handler.Requests[1].At - pausedAt >= TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Critical_StillWaitsForThePerMinuteQuota()
    {
        using var harness = new NotifierHarness(
            ScriptedTelegramHandler.Always(HttpStatusCode.OK, ScriptedTelegramHandler.OkBody),
            options => options.MaxMessagesPerMinute = 2);

        await harness.SendAsync(NotificationLevel.Info, "one", TimeSpan.FromSeconds(1));
        await harness.SendAsync(NotificationLevel.Info, "two", TimeSpan.FromSeconds(1));
        var critical = await harness.SendAsync(NotificationLevel.Critical, "three", TimeSpan.FromSeconds(1));

        Assert.IsTrue(critical.IsSuccess, critical.ToString());
        var span = harness.Handler.Requests[2].At - harness.Handler.Requests[0].At;
        Assert.IsTrue(span >= TimeSpan.FromMinutes(1), $"配額用完時 Critical 也要等下一分鐘,實際 {span}。A Critical waits for the next minute once the quota is spent; got {span}.");
    }

    private static string[] Texts(NotifierHarness harness) =>
        [.. harness.Handler.Requests.Select(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            return body.RootElement.GetProperty("text").GetString()!;
        })];

    private static void AssertSpacedByAtLeast(NotifierHarness harness, TimeSpan interval)
    {
        var times = harness.Handler.Requests.Select(request => request.At).ToArray();
        for (var i = 1; i < times.Length; i++)
        {
            Assert.IsTrue(times[i] - times[i - 1] >= interval, $"第 {i} 則與前一則只隔 {times[i] - times[i - 1]}。Message {i} followed the previous one after only {times[i] - times[i - 1]}.");
        }
    }

    /// <summary>
    /// 以真實時間等待某個狀態成立,假時鐘不動。用來確認「誰已經送出、誰還在排隊」之後才進行下一步。
    /// Waits in real time, with the fake clock still, for a state to hold — used to pin down who has been sent and
    /// who is still queued before the next step.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("等待前置狀態逾時。Timed out waiting for the precondition.");
            }

            await Task.Delay(1);
        }
    }
}
