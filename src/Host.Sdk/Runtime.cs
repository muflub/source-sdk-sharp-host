using System.Collections.Concurrent;

namespace SourceSharp.Host.Sdk;

/// <summary>The SDK's own time: heartbeats, backoff, lease TTLs and log flushes all run on it, so a fact can drive them.</summary>
public interface IHostClock
{
    DateTimeOffset UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

/// <summary>A clock over a <see cref="TimeProvider"/>: <see cref="TimeProvider.System"/> by default, a fake one in facts.</summary>
public sealed class HostClock(TimeProvider time) : IHostClock
{
    public static HostClock System { get; } = new(TimeProvider.System);
    public DateTimeOffset UtcNow => time.GetUtcNow();
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay, time, ct);
}

/// <summary>The SDK's counters, readable from any thread, for the game's <c>descent_status</c>.</summary>
public interface IHostMetrics
{
    /// <summary>Kills whose tier had no reserve item and went to the synchronous MintDrops (the signal to raise the size, §5.2a).</summary>
    long ReserveFallbacks { get; }
    /// <summary>NextForTier calls that found the tier empty.</summary>
    long ReserveMisses { get; }
    /// <summary>Items handed out by NextForTier (the kill path).</summary>
    long ReserveHits { get; }
    /// <summary>TakeReserve calls made to refill (initial fill included).</summary>
    long ReserveRefills { get; }
    /// <summary>Boss / champion MintDrops (tier -1).</summary>
    long BossMints { get; }
    long RevealRetries { get; }
    /// <summary>Connect streams opened after the first.</summary>
    long Reconnects { get; }
    long ConnectFailures { get; }
    long HeartbeatsSent { get; }
    long HeartbeatsAcked { get; }
    /// <summary>Heartbeat ticks skipped because the main thread stopped pumping.</summary>
    long HeartbeatsWithheld { get; }
    long DroppedLogLines { get; }
    long RpcCalls { get; }
    long RpcRetries { get; }
    long RpcFailures { get; }
    /// <summary>The last and the mean successful unary round trip, ms.</summary>
    double RpcLatencyLastMs { get; }
    double RpcLatencyMeanMs { get; }
    double RpcLatencyMaxMs { get; }
}

internal sealed class HostMetrics : IHostMetrics
{
    long _withheld;
    public long HeartbeatsWithheld => Interlocked.Read(ref _withheld);
    public void HeartbeatWithheld() => Interlocked.Increment(ref _withheld);
    long _fallbacks, _misses, _hits, _refills, _boss, _revealRetries, _reconnects, _connectFailures, _hbSent, _hbAcked, _dropped,
        _calls, _retries, _failures, _latencyCount;
    double _latencySum, _latencyLast, _latencyMax;
    readonly Lock _latency = new();

    public long ReserveFallbacks => Interlocked.Read(ref _fallbacks);
    public long ReserveMisses => Interlocked.Read(ref _misses);
    public long ReserveHits => Interlocked.Read(ref _hits);
    public long ReserveRefills => Interlocked.Read(ref _refills);
    public long BossMints => Interlocked.Read(ref _boss);
    public long RevealRetries => Interlocked.Read(ref _revealRetries);
    public long Reconnects => Interlocked.Read(ref _reconnects);
    public long ConnectFailures => Interlocked.Read(ref _connectFailures);
    public long HeartbeatsSent => Interlocked.Read(ref _hbSent);
    public long HeartbeatsAcked => Interlocked.Read(ref _hbAcked);
    public long DroppedLogLines => Interlocked.Read(ref _dropped);
    public long RpcCalls => Interlocked.Read(ref _calls);
    public long RpcRetries => Interlocked.Read(ref _retries);
    public long RpcFailures => Interlocked.Read(ref _failures);
    public double RpcLatencyLastMs { get { lock (_latency) return _latencyLast; } }
    public double RpcLatencyMeanMs { get { lock (_latency) return _latencyCount == 0 ? 0 : _latencySum / _latencyCount; } }
    public double RpcLatencyMaxMs { get { lock (_latency) return _latencyMax; } }

    public void Fallback() => Interlocked.Increment(ref _fallbacks);
    public void Miss() => Interlocked.Increment(ref _misses);
    public void Hit() => Interlocked.Increment(ref _hits);
    public void Refill() => Interlocked.Increment(ref _refills);
    public void Boss() => Interlocked.Increment(ref _boss);
    public void RevealRetry() => Interlocked.Increment(ref _revealRetries);
    public void Reconnect() => Interlocked.Increment(ref _reconnects);
    public void ConnectFailure() => Interlocked.Increment(ref _connectFailures);
    public void HeartbeatSent() => Interlocked.Increment(ref _hbSent);
    public void HeartbeatAcked() => Interlocked.Increment(ref _hbAcked);
    public void Dropped(long n) => Interlocked.Add(ref _dropped, n);
    public void Call() => Interlocked.Increment(ref _calls);
    public void Retry() => Interlocked.Increment(ref _retries);
    public void Failure() => Interlocked.Increment(ref _failures);

    public void Latency(TimeSpan t)
    {
        lock (_latency)
        {
            _latencyLast = t.TotalMilliseconds;
            _latencySum += _latencyLast;
            _latencyCount++;
            if (_latencyLast > _latencyMax) _latencyMax = _latencyLast;
        }
    }
}

/// <summary>
/// The main-thread discipline (§6.6): network threads post here; only <see cref="Pump"/>, called by
/// the game on its main thread, runs what was posted. No SDK callback, event or task completion
/// ever runs on a gRPC or pool thread.
/// </summary>
internal sealed class MainThreadQueue
{
    readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action work) => _queue.Enqueue(work);
    public int Pending => _queue.Count;

    /// <summary>A task that completes only inside <see cref="Pump"/>: continuations of an <c>await</c> without a synchronization context run inline there.</summary>
    public Task<T> Deliver<T>(Task<T> background, Action<T>? onMain = null)
    {
        var tcs = new TaskCompletionSource<T>();
        background.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Post(() => { onMain?.Invoke(t.Result); tcs.TrySetResult(t.Result); });
            else if (t.IsCanceled) Post(() => tcs.TrySetCanceled());
            else Post(() => tcs.TrySetException(t.Exception!.InnerExceptions));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>Runs everything posted so far (not what those items post in turn: that waits for the next frame).</summary>
    public int Pump()
    {
        var n = _queue.Count;
        var ran = 0;
        while (ran < n && _queue.TryDequeue(out var work))
        {
            ran++;
            work();
        }
        return ran;
    }
}
