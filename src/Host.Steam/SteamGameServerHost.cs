using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

/// <summary>Thrown for Steam-side failures: init errors, call-result failures and timeouts.</summary>
public sealed class SteamApiException : Exception
{
    public SteamResult Result { get; }
    public SteamApiException(SteamResult result, string message) : base(message) => Result = result;
    public SteamApiException(string message, Exception inner) : base(message, inner) => Result = SteamResult.Fail;
}

/// <summary>
/// A dedicated game server registered with Steam, driven through the flat C API.
///
/// Threading model: a single dedicated pump thread owns every native call for this instance —
/// init, settings, logon, callback dispatch and all queued work items run there back-to-back.
/// Public async methods enqueue onto that thread, so they are safe to call from anywhere.
/// Event handlers (<see cref="ISteamGameServerEvents"/>) fire on the pump thread.
///
/// Implementation is split by area across partial-class files:
/// Lifecycle (init/logon/shutdown), Dispatch (pump callbacks + timeouts),
/// Session (auth), Stats, Browser (advertised metadata), Queries (call-result APIs),
/// Packets (shared query socket).
/// </summary>
public sealed partial class SteamGameServerHost : IAsyncDisposable, IDisposable
{
    private readonly ISteamGameServerEvents? _events;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly SemaphoreSlim _workSignal = new(0);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private GameServerConfiguration? _config;
    private SteamNativeApi? _api;
    private Thread? _pumpThread;
    private volatile bool _stopRequested;
    private bool _nativeInitialized;

    private int _stateField = (int)GameServerState.Created;
    public GameServerState State => (GameServerState)Volatile.Read(ref _stateField);

    // Native handles, established on the pump thread in Bootstrap().
    private int _pipe;
    private IntPtr _gs, _stats, _utils;
    private volatile bool _loggedOn;
    private volatile bool _secure;
    private SteamId _serverSteamId;
    private SteamIpAddress _publicIp;
    private TaskCompletionSource<bool>? _initTcs, _logonTcs, _logoffTcs;

    public SteamId ServerSteamId => _serverSteamId;
    public SteamIpAddress PublicIp => _publicIp;
    public bool IsLoggedOn => _loggedOn;
    public bool IsSecure => _secure;

    public SteamGameServerHost(ISteamGameServerEvents? events = null) => _events = events;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    // ---------------------------------------------------------------- pump thread

    private void PumpLoop()
    {
        var api = _api!;
        try
        {
            Bootstrap();
        }
        catch (Exception ex)
        {
            SetState(GameServerState.Faulted, ex.Message);
            _initTcs!.TrySetException(ex);
            _logonTcs!.TrySetException(ex);
            FailAllPending(new SteamApiException("host failed to start.", ex));
            api.ReleaseCurrentThreadMemory();
            return;
        }

        try
        {
            while (!_stopRequested)
            {
                DrainWork();

                api.ManualDispatchRunFrame(_pipe);
                while (api.ManualDispatchGetNextCallback(_pipe, out var msg))
                {
                    try
                    {
                        DispatchCallback(msg);
                    }
                    catch (Exception ex)
                    {
                        ReportError(ex);
                    }
                    finally
                    {
                        api.ManualDispatchFreeLastCallback(_pipe);
                    }
                }

                PollState();
                SweepTimeouts();

                // Idle wait ends the moment Enqueue releases the signal; otherwise it expires
                // after PumpIdleDelay so Steam polling continues.
                _workSignal.Wait(_stopRequested ? 0 : (int)_config!.PumpIdleDelay.TotalMilliseconds);
            }
        }
        catch (ThreadInterruptedException)
        {
            // best-effort exit
        }
        catch (Exception ex)
        {
            SetState(GameServerState.Faulted, ex.Message);
            ReportError(ex);
        }
        finally
        {
            // Drain work queued after the stop flag (the queued SteamGameServer_Shutdown included),
            // then tear down on this thread — Steam tracks per-thread memory.
            DrainWork();
            FailAllPending(new SteamApiException(SteamResult.Fail, "host stopped before the call completed."));
            if (_nativeInitialized)
                api.GameServerShutdown();
            api.ReleaseCurrentThreadMemory();
            _logoffTcs?.TrySetResult(true);
            _logonTcs?.TrySetResult(false);
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var item))
            item();
    }

    // ---------------------------------------------------------------- plumbing

    private Task<T> CallResultAsync<T>(string name, Func<SteamNativeApi, ulong> start, int expectedCallback, Func<IntPtr, T> parse)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(name, () =>
        {
            var api = RequireApi();
            var handle = start(api);
            if (handle == 0)
            {
                tcs.TrySetException(new SteamApiException(SteamResult.Fail, $"{name}: null call handle (not logged on?)."));
                return;
            }
            _pendingCalls[handle] = new PendingCall(handle, expectedCallback, Deadline(_config!.CallTimeout), p => parse(p)!, tcs);
        });
        return Unwrap<object, T>(tcs.Task);
    }

    private static async Task<TTo> Unwrap<TFrom, TTo>(Task<TFrom> task)
    {
        var value = await task.ConfigureAwait(false);
        return (TTo)(object)value!;
    }

    private long Deadline(TimeSpan timeout) => _clock.ElapsedMilliseconds + (long)timeout.TotalMilliseconds;

    private long DeadlineOrDefault() => Deadline(_config?.CallTimeout ?? TimeSpan.FromSeconds(30));

    private Task Enqueue(string name, Action onPump) =>
        Enqueue<object?>(name, () => { onPump(); return null; });

    private Task<T> Enqueue<T>(string name, Func<T> onPump)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(onPump());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                ReportError(new SteamApiException($"Work item '{name}' failed.", ex));
            }
        });
        _workSignal.Release();
        return tcs.Task;
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var (_, pending) in _pendingCalls)
            pending.Tcs.TrySetException(ex);
        _pendingCalls.Clear();
        foreach (var (_, pending) in _pendingEventCalls)
            pending.Tcs.TrySetException(ex);
        _pendingEventCalls.Clear();
        foreach (var (_, auth) in _pendingAuths)
            auth.Tcs.TrySetException(ex);
        _pendingAuths.Clear();
    }

    private SteamNativeApi RequireApi() => _api ?? throw new InvalidOperationException("Host not started.");

    private void SetState(GameServerState state, string detail)
    {
        var previous = (GameServerState)Interlocked.Exchange(ref _stateField, (int)state);
        if (previous != state)
            SafeInvoke(() => _events?.OnStateChanged(state, detail));
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void ReportError(Exception ex)
    {
        try
        {
            _events?.OnHostError(ex);
        }
        catch
        {
            // sink is failing; nothing left to do but survive
        }
    }
}
