using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RetryProxy.Core.Internal;

/// <summary>
/// 基于单调时钟的截止时刻，对应 tokio 的 <c>Instant + Duration</c> 与 <c>sleep_until</c>。
/// </summary>
internal readonly struct Deadline
{
    private static readonly TimeSpan MaxSingleWait = TimeSpan.FromDays(1);

    private readonly long _timestamp;

    private Deadline(long timestamp)
    {
        _timestamp = timestamp;
    }

    public static Deadline After(TimeSpan duration)
    {
        var ticks = duration <= TimeSpan.Zero
            ? 0
            : (long)Math.Min(duration.TotalSeconds * Stopwatch.Frequency, long.MaxValue / 2.0);
        return new Deadline(Stopwatch.GetTimestamp() + ticks);
    }

    public static Deadline AfterSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return After(TimeSpan.Zero);
        }

        if (double.IsInfinity(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds / 2)
        {
            return new Deadline(long.MaxValue);
        }

        return After(TimeSpan.FromSeconds(seconds));
    }

    public bool HasPassed => Stopwatch.GetTimestamp() >= _timestamp;

    public TimeSpan Remaining
    {
        get
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= _timestamp)
            {
                return TimeSpan.Zero;
            }

            var seconds = (_timestamp - now) / (double)Stopwatch.Frequency;
            return seconds > TimeSpan.MaxValue.TotalSeconds / 2 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>等待到截止时刻；令牌取消时抛出 <see cref="OperationCanceledException"/>。</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = Remaining;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining > MaxSingleWait ? MaxSingleWait : remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>与截止时刻绑定的取消源：到期后自动取消。</summary>
    public CancellationTokenSource CreateCancellation()
    {
        var source = new CancellationTokenSource();
        var remaining = Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            source.Cancel();
        }
        else if (remaining < MaxSingleWait)
        {
            source.CancelAfter(remaining);
        }

        return source;
    }
}

/// <summary>对应 <c>Instant::now()</c> / <c>elapsed()</c>。</summary>
internal readonly struct MonotonicInstant
{
    private readonly long _timestamp;

    private MonotonicInstant(long timestamp)
    {
        _timestamp = timestamp;
    }

    public static MonotonicInstant Now => new(Stopwatch.GetTimestamp());

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_timestamp);

    public double ElapsedSeconds => Elapsed.TotalSeconds;
}
