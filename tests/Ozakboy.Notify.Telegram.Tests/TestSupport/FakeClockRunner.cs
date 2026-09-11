using System.Diagnostics;

namespace Ozakboy.Notify.Telegram.Tests.TestSupport;

/// <summary>
/// 推進假時鐘直到工作完成。取代 <c>Thread.Sleep</c>,讓時間相關的測試不必真的等待。
/// Advances a fake clock until a task completes. It replaces <c>Thread.Sleep</c>, so time-dependent tests never
/// actually wait.
/// </summary>
/// <remarks>
/// <para>
/// 每一步之間以真實時間的一個短節拍隔開,並以真實時間(而非步數)為上限。兩者都是踩過才加的:
/// 只用 <c>Task.Yield()</c> 分隔、以步數為上限時,冷啟動(JIT、第一次建立 <c>HttpClient</c>、涵蓋率插樁)
/// 期間幾千步在幾毫秒內就用完了 —— 工作還沒掛上它的計時器,時鐘就已經停止推進,計時器永遠不會觸發,
/// 測試卡到真實時間上限才失敗;反過來,假時間衝得太快,也會讓一個仍在進行中的請求被誤判逾時。
/// Each step is separated by a short real-time beat, and the loop is bounded by real time rather than a step
/// count. Both were added after being bitten: separated only by <c>Task.Yield()</c> and bounded by steps, a
/// cold start (JIT, the first <c>HttpClient</c>, coverage instrumentation) burned through thousands of steps in a
/// few milliseconds — the clock stopped before the work had even registered its timer, the timer never fired,
/// and the test hung until the real-time bound. Conversely, fake time racing ahead can make a request that is
/// still in flight look timed out.
/// </para>
/// <para>
/// 以 100 ms 的步長與約 1–16 ms 的節拍,假時間最多比真實時間快幾十倍:夠快,讓 5 秒的等待在幾百毫秒內跑完;
/// 又不至於快到讓預設 10 分鐘的請求逾時在正常工作期間被觸發。
/// With 100 ms steps and a beat of roughly 1–16 ms, fake time runs at most a few dozen times faster than real
/// time: fast enough that a 5-second wait finishes in a few hundred milliseconds, and not so fast that the
/// default 10-minute request timeout can fire during ordinary work.
/// </para>
/// </remarks>
internal static class FakeClockRunner
{
    public static readonly TimeSpan DefaultStep = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan RealTimeBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(1);

    public static async Task<T> RunAsync<T>(FakeTimeProvider clock, Task<T> task, TimeSpan step)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < RealTimeBound)
        {
            // 先讓工作跑一個節拍再推進:不需要計時器的工作(大多數成功路徑)在這裡就完成了,時鐘根本不動。
            // Let the work run for a beat before advancing: work that needs no timer (most success paths)
            // completes here without the clock moving at all.
            await Task.Delay(Beat);
            if (!task.IsCompleted)
            {
                clock.Advance(step);
            }
        }

        if (!task.IsCompleted)
        {
            Assert.Fail($"工作在真實時間 {RealTimeBound} 內沒有完成(假時鐘已推進到 {clock.GetUtcNow():O})。The work did not complete within {RealTimeBound} of real time.");
        }

        return await task;
    }

    public static Task<Result> RunAsync(FakeTimeProvider clock, Task<Result> task) => RunAsync(clock, task, DefaultStep);
}
