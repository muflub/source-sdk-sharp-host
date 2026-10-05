using System.Runtime.InteropServices;
using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

/// <summary>
/// Pump-thread callback decoding, call-result resolution and timeout sweeping.
/// All state here is touched on the pump thread only.
/// </summary>
public sealed partial class SteamGameServerHost
{
    // Callback ids: steam_api_internal.h bases + isteamuser.h / isteamgameserver*.h offsets.
    private const int CallbackValidateAuthTicket = 143; // 100 + 43
    private const int CallbackPolicyResponse = 115;     // 100 + 15
    private const int CallbackApicallCompleted = 703;   // 700 + 3
    private const int CallbackClientApprove = 201;
    private const int CallbackClientDeny = 202;
    private const int CallbackClientKick = 203;
    private const int CallbackAchievementStatus = 206;
    private const int CallbackGameplayStats = 207;
    private const int CallbackClientGroupStatus = 208;
    private const int CallbackStatsUnloaded = 1108; // 1100 + 8
    private const int CallbackStatsReceived = 1800;
    private const int CallbackStatsStored = 1801;
    private const int CallbackReputation = 209;
    private const int CallbackAssociateWithClan = 210;
    private const int CallbackPlayerCompatibility = 211;

    // Pump-thread-only tracking (see Session/Queries partials for the pending types).
    private readonly Dictionary<ulong, PendingCall> _pendingCalls = new();
    private readonly Dictionary<int, PendingCall> _pendingEventCalls = new();
    private readonly Dictionary<SteamId, PendingAuth> _pendingAuths = new();
    private readonly Dictionary<SteamId, AuthValidation> _lastValidation = new();
    private long _restartCheckDueMs;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WarningHookNative(int msgType, IntPtr message);
    private WarningHookNative? _warningHook; // rooted: native keeps only the raw pointer

    private void DispatchCallback(SteamNativeApi.CallbackMsg msg)
    {
        var api = _api!;
        var p = msg.PubParam;
        switch (msg.ICallback)
        {
            case CallbackApicallCompleted:
            {
                var (call, callbackId, size) = CallbackDecoder.ReadApicallCompleted(p);
                if (!_pendingCalls.Remove(call, out var pending))
                    return; // nobody waiting on this handle
                ResolveCallResult(api, pending, size);
                return;
            }

            case CallbackValidateAuthTicket:
            {
                var validation = CallbackDecoder.ReadValidateAuthTicket(p);
                _lastValidation[validation.Player] = validation;
                if (_pendingAuths.Remove(validation.Player, out var auth))
                    auth.Tcs.TrySetResult(new PlayerAuthResult(validation.Player, BeginAuthSessionResult.Ok, validation));
                SafeInvoke(() => _events?.OnAuthValidation(validation));
                return;
            }

            case CallbackClientApprove:
            {
                var (player, owner) = CallbackDecoder.ReadClientApprove(p);
                if (_pendingAuths.Remove(player, out var auth))
                {
                    var validation = _lastValidation.TryGetValue(player, out var cached)
                        ? cached
                        : new AuthValidation(player, AuthSessionResponse.Ok, owner);
                    auth.Tcs.TrySetResult(new PlayerAuthResult(player, BeginAuthSessionResult.Ok, validation));
                }
                SafeInvoke(() => _events?.OnClientApproved(player, owner));
                return;
            }

            case CallbackClientDeny:
            {
                var (player, reason, text) = CallbackDecoder.ReadClientDeny(p);
                if (_pendingAuths.Remove(player, out var auth))
                    auth.Tcs.TrySetResult(new PlayerAuthResult(player, BeginAuthSessionResult.Ok,
                        new AuthValidation(player, MapDenyToAuthFailure(reason), SteamId.Invalid)));
                SafeInvoke(() => _events?.OnClientDenied(player, reason, text));
                return;
            }

            case CallbackClientKick:
            {
                var (player, reason) = CallbackDecoder.ReadClientKick(p);
                SafeInvoke(() => _events?.OnClientKickRequested(player, reason));
                return;
            }

            case CallbackAchievementStatus:
            {
                var (player, achievement, unlocked) = CallbackDecoder.ReadAchievementStatus(p);
                SafeInvoke(() => _events?.OnAchievementStatus(player, achievement, unlocked));
                return;
            }

            case CallbackPolicyResponse:
            {
                _secure = CallbackDecoder.ReadPolicyResponse(p);
                SafeInvoke(() => _events?.OnPolicyResponse(_secure));
                return;
            }

            case CallbackGameplayStats:
            {
                var stats = CallbackDecoder.ReadGameplayStats(p);
                if (_pendingEventCalls.Remove(CallbackGameplayStats, out var waiter))
                    waiter.Tcs.TrySetResult(stats!);
                SafeInvoke(() => _events?.OnGameplayStats(stats));
                return;
            }

            case CallbackClientGroupStatus:
            {
                var (user, group, member, officer) = CallbackDecoder.ReadClientGroupStatus(p);
                SafeInvoke(() => _events?.OnGroupStatus(user, group, member, officer));
                return;
            }

            case CallbackStatsUnloaded:
            {
                var player = CallbackDecoder.ReadStatsUnloaded(p);
                SafeInvoke(() => _events?.OnStatsUnloaded(player));
                return;
            }

            // 1800/1801 are consumed via SteamAPICallCompleted_t → GetAPICallResult (authoritative
            // path, keyed by call handle); raw copies carry no handle, so ignore them.
            case CallbackStatsReceived:
            case CallbackStatsStored:
                return;
        }
    }

    private void ResolveCallResult(SteamNativeApi api, PendingCall pending, int size)
    {
        var buffer = Marshal.AllocHGlobal(Math.Max(size, 1));
        var failedPtr = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(failedPtr, 0);
            var ipcOk = api.ManualDispatchGetApicallResult(_pipe, pending.Call, buffer, size, pending.ExpectedCallback, failedPtr);
            var failed = Marshal.ReadByte(failedPtr) != 0;
            if (!ipcOk || failed)
            {
                pending.Tcs.TrySetException(new SteamApiException(SteamResult.Fail,
                    $"Call result {pending.ExpectedCallback} (handle {pending.Call}) failed (ipcOk={ipcOk}, failed={failed}) — Steam could not deliver it."));
                return;
            }
            pending.Tcs.TrySetResult(pending.Parse!(buffer));
        }
        catch (Exception ex)
        {
            pending.Tcs.TrySetException(ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(failedPtr);
        }
    }

    private void PollState()
    {
        var api = _api!;
        bool loggedOn;
        try
        {
            loggedOn = api.BLoggedOn(_gs);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return;
        }

        if (loggedOn != _loggedOn)
        {
            _loggedOn = loggedOn;
            if (loggedOn)
            {
                _serverSteamId = new SteamId(api.GetSteamId(_gs));
                _secure = api.BSecure(_gs);
                _publicIp = ReadPublicIp(api);
                SetState(GameServerState.Running, $"logged on as {_serverSteamId}");
                _logonTcs?.TrySetResult(true);
                SafeInvoke(() => _events?.OnLoggedOn(_serverSteamId, _secure));
            }
            else
            {
                SafeInvoke(() => _events?.OnLoggedOff());
                _logoffTcs?.TrySetResult(true);
            }
        }

        var now = _clock.ElapsedMilliseconds;
        if (loggedOn && now >= _restartCheckDueMs)
        {
            _restartCheckDueMs = now + 2000;
            try
            {
                if (api.WasRestartRequested(_gs))
                    SafeInvoke(() => _events?.OnRestartRequested());
            }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    private SteamIpAddress ReadPublicIp(SteamNativeApi api)
    {
        // SteamIPAddress_t (pack 1): 16-byte union + ESteamIPType. m_unIPv4 is documented host order.
        var raw = api.GetPublicIP(_gs);
        if (raw.Type == (int)SteamIpType.IPv4)
        {
            uint ipv4;
            unsafe { ipv4 = (uint)(raw.Addr[0] | (raw.Addr[1] << 8) | (raw.Addr[2] << 16) | (raw.Addr[3] << 24)); }
            return new SteamIpAddress(SteamIpType.IPv4, ipv4, null);
        }
        var ipv6 = new byte[16];
        unsafe { for (int i = 0; i < 16; i++) ipv6[i] = raw.Addr[i]; }
        return new SteamIpAddress(SteamIpType.IPv6, 0, ipv6);
    }

    private void SweepTimeouts()
    {
        var now = _clock.ElapsedMilliseconds;
        List<ulong>? expired = null;
        foreach (var (call, pending) in _pendingCalls)
        {
            if (now < pending.DeadlineMs)
                continue;
            expired ??= [];
            expired.Add(call);
        }
        if (expired is not null)
        {
            foreach (var call in expired)
            {
                var pending = _pendingCalls[call];
                _pendingCalls.Remove(call);
                pending.Tcs.TrySetException(new SteamApiException(SteamResult.Timeout,
                    $"Steam call result {pending.ExpectedCallback} (handle {call}) timed out."));
            }
        }

        List<SteamId>? expiredAuths = null;
        foreach (var (player, auth) in _pendingAuths)
        {
            if (now < auth.DeadlineMs)
                continue;
            expiredAuths ??= [];
            expiredAuths.Add(player);
        }
        if (expiredAuths is not null)
        {
            foreach (var player in expiredAuths)
            {
                var auth = _pendingAuths[player];
                _pendingAuths.Remove(player);
                auth.Tcs.TrySetException(new SteamApiException(SteamResult.Timeout,
                    $"Auth validation for {player} timed out (ticket expired, client offline, or Steam unreachable)."));
            }
        }

        List<int>? expiredEvents = null;
        foreach (var (cb, pending) in _pendingEventCalls)
        {
            if (now < pending.DeadlineMs)
                continue;
            expiredEvents ??= [];
            expiredEvents.Add(cb);
        }
        if (expiredEvents is not null)
        {
            foreach (var cb in expiredEvents)
            {
                var pending = _pendingEventCalls[cb];
                _pendingEventCalls.Remove(cb);
                pending.Tcs.TrySetException(new SteamApiException(SteamResult.Timeout, $"Callback {cb} timed out."));
            }
        }
    }

    private static AuthSessionResponse MapDenyToAuthFailure(DenyReason reason) => reason switch
    {
        DenyReason.NoLicense => AuthSessionResponse.NoLicenseOrExpired,
        DenyReason.Cheater => AuthSessionResponse.VacBanned,
        DenyReason.LoggedInElseWhere => AuthSessionResponse.LoggedInElseWhere,
        DenyReason.SteamValidationStalled => AuthSessionResponse.VacCheckTimedOut,
        _ => AuthSessionResponse.AuthTicketInvalid,
    };

    private sealed class PendingCall
    {
        public ulong Call { get; }
        public int ExpectedCallback { get; }
        public long DeadlineMs { get; }
        public Func<IntPtr, object>? Parse { get; }
        public TaskCompletionSource<object> Tcs { get; }
        public PendingCall(ulong call, int expectedCallback, long deadlineMs, Func<IntPtr, object>? parse, TaskCompletionSource<object> tcs)
            => (Call, ExpectedCallback, DeadlineMs, Parse, Tcs) = (call, expectedCallback, deadlineMs, parse, tcs);
    }

    private sealed class PendingAuth
    {
        public TaskCompletionSource<PlayerAuthResult> Tcs { get; }
        public long DeadlineMs { get; }
        public PendingAuth(TaskCompletionSource<PlayerAuthResult> tcs, long deadlineMs) => (Tcs, DeadlineMs) = (tcs, deadlineMs);
    }
}
