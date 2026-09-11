using Ozakboy.Core.Abstractions;
using Ozakboy.Http.RateLimiting;

namespace Ozakboy.Notify.Telegram.Internal;

/// <summary>
/// 送出前的排隊閘門:同一時間只放行一則,Critical 永遠排在 Info / Warning 前面,但每一則都照樣付速率限制的額度。
/// The queue in front of every send: it admits one message at a time, always puts Critical ahead of Info and
/// Warning, and still makes every message pay for a rate-limit slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要優先通道。</b>告警的時效要求(需求 FR-12)是「Critical 事件發生後 5 秒內收到」。
/// 節流若只依抵達順序放行,一串 Info / Warning 之後的 Critical 要排到它們全部送完 ——
/// 每秒 1 則的節奏下,排在 10 則 Info 後面就是 10 秒,最壞要等到節流等待上限(預設 60 秒)才被放棄。
/// 優先通道讓 Critical 在下一個可用的節流額度就出去,也就是最多一個節流間隔(預設 1 秒)。
/// <b>Why a priority lane.</b> The timeliness requirement (FR-12) is that a Critical event reaches the recipient
/// within 5 seconds. With throttling admitting in arrival order only, a Critical queued behind a run of Info and
/// Warning waits until they have all gone — ten seconds behind ten Info messages at one per second, and in the
/// worst case until the throttling wait limit (60 seconds by default) gives up on it. The priority lane sends a
/// Critical on the next available slot, which is at most one throttling interval (one second by default) away.
/// </para>
/// <para>
/// <b>優先只影響排隊順序,不繞過速率限制。</b>Critical 仍要等節流額度(每聊天間隔與每分鐘上限),
/// 也要等 429 的暫停期結束 —— 搶快只會換來 429,結果比排隊更晚送達。已經拿到額度、正在送的那一則不會被中斷。
/// 因此 5 秒的保證有兩個前提:這一分鐘的配額還沒用完,而且 Telegram 沒有要求暫停。
/// <b>Priority changes queue order only; it does not bypass the rate limits.</b> A Critical still waits for a
/// throttling slot (per-chat spacing and the per-minute cap) and for any 429 pause to end: rushing only earns a
/// 429 and arrives later than queueing would. A message that already holds a slot and is being sent is never
/// interrupted. The five-second guarantee therefore rests on two conditions: the minute's quota is not spent, and
/// Telegram has not asked for a pause.
/// </para>
/// <para>
/// <b>先拿額度,再決定給誰。</b>一個幫浦負責「等暫停結束 → 取得一個節流額度 → 把額度交給佇列最前面的人」。
/// 挑人發生在拿到額度之後,所以在等待額度期間才抵達的 Critical 仍然拿得到那一個額度;
/// 同理,429 暫停結束後的第一個額度一定給 Critical。兩條佇列各自依抵達順序,Info 與 Warning 之間互不插隊。
/// <b>Take the slot first, then decide who gets it.</b> A single pump waits for any pause to end, obtains one
/// throttling slot, and hands it to whoever is at the front. Choosing happens after the slot is in hand, so a
/// Critical that arrives while the pump is still waiting for the slot gets that very slot; likewise the first
/// slot after a 429 pause always goes to a Critical. Each queue is first-in first-out, so Info and Warning
/// messages never overtake one another.
/// </para>
/// </remarks>
internal sealed class PrioritySendGate : IDisposable
{
    /// <summary>
    /// 幫浦向限流器要一個額度時的單次等待上限。只要還有人在排隊,逾時後就再要一次;
    /// 比一分鐘的配額時間窗長,正常情況不會觸發。
    /// How long the pump waits for one slot per request to the limiter. While anyone is queued it simply asks
    /// again after a timeout; it is longer than the one-minute quota window, so it does not fire in practice.
    /// </summary>
    private static readonly TimeSpan SlotAcquisitionTimeout = TimeSpan.FromMinutes(2);

    private readonly WeightedRateLimiter _limiter;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _waitLimit;
    private readonly Lock _lock = new();
    private readonly Queue<TaskCompletionSource<TurnOutcome>> _critical = new();
    private readonly Queue<TaskCompletionSource<TurnOutcome>> _normal = new();
    private readonly CancellationTokenSource _shutdown = new();
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private bool _pumpRunning;
    private bool _disposed;

    // 只有幫浦會碰這個欄位;幫浦同一時間只有一個,前後兩個幫浦之間由 _lock 建立先後關係。
    // Only the pump touches this field; there is never more than one pump, and _lock orders successive pumps.
    private bool _slotInHand;

    public PrioritySendGate(TimeSpan minimumInterval, int maxMessagesPerMinute, TimeSpan waitLimit, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _waitLimit = waitLimit;
        _limiter = new WeightedRateLimiter(
            new RateLimitOptions
            {
                AcquisitionTimeout = SlotAcquisitionTimeout,
                Buckets =
                {
                    new RateLimitBucket("per-chat-interval", 1, minimumInterval),
                    new RateLimitBucket("per-minute", maxMessagesPerMinute, TimeSpan.FromMinutes(1)),
                },
            },
            timeProvider);
    }

    private enum TurnOutcome
    {
        Granted = 0,
        TimedOut = 1,
        Cancelled = 2,
    }

    /// <summary>
    /// 仍在排隊(尚未輪到、也還沒放棄)的人數。供測試確認排隊狀態。
    /// How many are still queued — not yet admitted and not given up. Lets tests confirm the queue state.
    /// </summary>
    public int QueuedCount
    {
        get
        {
            lock (_lock)
            {
                return _critical.Count(waiter => !waiter.Task.IsCompleted) + _normal.Count(waiter => !waiter.Task.IsCompleted);
            }
        }
    }

    /// <summary>
    /// 429 暫停期還剩多久。
    /// How much of the 429 pause remains.
    /// </summary>
    public TimeSpan CooldownRemaining
    {
        get
        {
            lock (_lock)
            {
                var remaining = _cooldownUntil - _timeProvider.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// 延長 429 暫停期。暫停期間幫浦不放行任何人,Critical 也一樣。
    /// Extends the 429 pause. During it the pump admits no one, Critical included.
    /// </summary>
    public void ExtendCooldown(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            var until = _timeProvider.GetUtcNow() + retryAfter;
            if (until > _cooldownUntil)
            {
                _cooldownUntil = until;
            }
        }
    }

    /// <summary>
    /// 排隊等待送出一次的許可。
    /// Queues for permission to send once.
    /// </summary>
    /// <param name="critical">是否走優先通道。Whether to use the priority lane.</param>
    /// <param name="cancellationToken">取消權杖。The cancellation token.</param>
    /// <returns>
    /// 輪到時為成功;等超過上限為 <see cref="TelegramErrorCodes.Throttled"/>;被取消為 <see cref="TelegramErrorCodes.Cancelled"/>。
    /// Success when admitted; <see cref="TelegramErrorCodes.Throttled"/> past the wait limit;
    /// <see cref="TelegramErrorCodes.Cancelled"/> when cancelled.
    /// </returns>
    public async Task<Result> WaitForTurnAsync(bool critical, CancellationToken cancellationToken)
    {
        // 已取消就不排隊:排進去可能在取消回呼執行前就被放行,白白用掉一個額度。
        // Already cancelled means no queueing: once queued it might be admitted before the cancellation callback
        // runs, wasting a slot.
        if (cancellationToken.IsCancellationRequested)
        {
            return TelegramErrorMapper.CancelledWhileQueued();
        }

        // 接續工作一律非同步執行:放行發生在幫浦裡,不能讓送出的程式碼在幫浦的執行緒上跑。
        // Continuations always run asynchronously: admission happens inside the pump, and the sending code must
        // not run on the pump's thread.
        var waiter = new TaskCompletionSource<TurnOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool startPump;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (critical ? _critical : _normal).Enqueue(waiter);
            startPump = !_pumpRunning;
            _pumpRunning = true;
        }

        if (startPump)
        {
            _ = Task.Run(PumpAsync, CancellationToken.None);
        }

        TurnOutcome outcome;
        using (_timeProvider.CreateTimer(static state => Complete(state, TurnOutcome.TimedOut), waiter, _waitLimit, Timeout.InfiniteTimeSpan))
        using (cancellationToken.Register(static state => Complete(state, TurnOutcome.Cancelled), waiter))
        {
            outcome = await waiter.Task.ConfigureAwait(false);
        }

        return outcome switch
        {
            TurnOutcome.Granted => Result.Success(),
            TurnOutcome.TimedOut => TelegramErrorMapper.ThrottleTimedOut(_waitLimit),
            _ => TelegramErrorMapper.CancelledWhileQueued(),
        };
    }

    public void Dispose()
    {
        List<TaskCompletionSource<TurnOutcome>> pending;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _critical, .. _normal];
            _critical.Clear();
            _normal.Clear();
        }

        _shutdown.Cancel();
        foreach (var waiter in pending)
        {
            waiter.TrySetResult(TurnOutcome.Cancelled);
        }

        _limiter.Dispose();
        _shutdown.Dispose();
    }

    private static void Complete(object? state, TurnOutcome outcome) =>
        ((TaskCompletionSource<TurnOutcome>)state!).TrySetResult(outcome);

    /// <summary>
    /// 從佇列前端取出第一個還在等的人並放行。已逾時或已取消的人留在佇列裡,輪到時直接跳過。
    /// Admits the first waiter at the front who is still waiting. Those who timed out or cancelled stay queued
    /// and are skipped when their turn comes.
    /// </summary>
    private static bool TryAdmit(Queue<TaskCompletionSource<TurnOutcome>> queue)
    {
        while (queue.Count > 0)
        {
            if (queue.Dequeue().TrySetResult(TurnOutcome.Granted))
            {
                return true;
            }
        }

        return false;
    }

    private static void DropAbandoned(Queue<TaskCompletionSource<TurnOutcome>> queue)
    {
        while (queue.Count > 0 && queue.Peek().Task.IsCompleted)
        {
            queue.Dequeue();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            var shutdown = _shutdown.Token;
            while (true)
            {
                lock (_lock)
                {
                    DropAbandoned(_critical);
                    DropAbandoned(_normal);
                    if (_critical.Count == 0 && _normal.Count == 0)
                    {
                        // 沒人在等就收工;手上若還有額度就留著,下一個人來時可以立刻用(時間只會更晚,不會超速)。
                        // Nobody waiting, so stop; a slot still in hand is kept for the next arrival, who can use
                        // it at once (it can only be later than its window, never faster).
                        _pumpRunning = false;
                        return;
                    }
                }

                var cooldown = CooldownRemaining;
                if (cooldown > TimeSpan.Zero)
                {
                    await Task.Delay(cooldown, _timeProvider, shutdown).ConfigureAwait(false);
                    continue;
                }

                if (!_slotInHand)
                {
                    var slot = await _limiter.AcquireAsync(1, shutdown).ConfigureAwait(false);
                    if (slot.IsFailure && shutdown.IsCancellationRequested)
                    {
                        return;
                    }

                    _slotInHand = slot.IsSuccess;

                    // 拿到額度後回到迴圈開頭重新檢查:等待期間可能有人放棄、也可能有 429 延長了暫停期。
                    // Back to the top once the slot is in hand: meanwhile someone may have given up, or a 429 may
                    // have extended the pause.
                    continue;
                }

                lock (_lock)
                {
                    if (TryAdmit(_critical) || TryAdmit(_normal))
                    {
                        _slotInHand = false;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 通知器正在釋放,排隊中的人已由 Dispose 通知。
            // The notifier is being disposed; queued waiters have been told by Dispose.
        }
        catch (ObjectDisposedException)
        {
            // 同上:釋放與幫浦最後一輪同時發生。
            // As above: disposal raced the pump's last round.
        }
    }
}
