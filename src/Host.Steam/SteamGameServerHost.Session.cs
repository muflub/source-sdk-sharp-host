using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

/// <summary>
/// Auth-session area: server-side ticket validation (BeginAuthSession), the server's own
/// auth ticket, license checks, and fake-connection/bot slots.
/// </summary>
public sealed partial class SteamGameServerHost
{
    /// <summary>
    /// Server-side auth of a client holding an auth session ticket for this server.
    /// Calls BeginAuthSession on the pump and resolves with the authoritative
    /// ValidateAuthTicketResponse_t — or the first GSClientApprove/Deny, whichever lands first.
    /// The server's own ticket (GetOwnAuthSessionTicketAsync) must reach the client via your game protocol first.
    /// The cancellation token owns the deadline (e.g. <c>CancellationTokenSource.CancelAfter</c>):
    /// firing it aborts the pending auth and ends the session. With no token, the host falls back
    /// to <see cref="GameServerConfiguration.CallTimeout"/> so a silent Steam can't leak the session.
    /// </summary>
    public Task<PlayerAuthResult> AuthPlayerAsync(SteamId player, byte[] ticket, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<PlayerAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineMs = cancellationToken.CanBeCanceled
            ? long.MaxValue // token owns the deadline; the sweep must not race it
            : DeadlineOrDefault();

        Enqueue("BeginAuthSession", () =>
        {
            // Cancelled before the pump got to us — don't open a session nobody waits on.
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            var api = RequireApi();
            BeginAuthSessionResult immediate;
            unsafe
            {
                fixed (byte* pData = ticket)
                    immediate = (BeginAuthSessionResult)api.BeginAuthSession(_gs, (IntPtr)pData, ticket.Length, player.Value);
            }
            if (immediate != BeginAuthSessionResult.Ok)
            {
                tcs.TrySetResult(new PlayerAuthResult(player, immediate, null));
                return;
            }

            // Re-auth of the same user: Steam would reject the duplicate; fail the old waiter explicitly.
            if (_pendingAuths.Remove(player, out var stale))
                stale.Tcs.TrySetResult(new PlayerAuthResult(player, BeginAuthSessionResult.Ok,
                    new AuthValidation(player, AuthSessionResponse.AuthTicketInvalidAlreadyUsed, SteamId.Invalid)));

            _pendingAuths[player] = new PendingAuth(tcs, deadlineMs);
            // If the token fired during the call above, its queued callback (enqueued on this same
            // FIFO pump, so it runs next) tears the session down via EndAuthSession.
        });

        var registration = cancellationToken.UnsafeRegister(static state =>
        {
            var (self, player, tcs, token) = ((SteamGameServerHost, SteamId, TaskCompletionSource<PlayerAuthResult>, CancellationToken))state!;
            self.Enqueue("EndAuthSession(cancel)", () =>
            {
                if (self._pendingAuths.Remove(player, out var pending))
                {
                    pending.Tcs.TrySetCanceled(token);
                    self._api?.EndAuthSession(self._gs, player.Value);
                }
                tcs.TrySetCanceled(token);
            });
        }, (this, player, tcs, cancellationToken));

        // Stop listening once settled: a late token fire must not EndAuthSession a validated player.
        _ = tcs.Task.ContinueWith(
            static (_, reg) => ((CancellationTokenRegistration)reg!).Dispose(), registration,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return tcs.Task;
    }

    /// <summary>
    /// Ends an authenticated session (player left). Fire-and-forget on the pump.
    /// Also cancels any in-flight <see cref="AuthPlayerAsync"/> for that player.
    /// </summary>
    public Task EndAuthSessionAsync(SteamId player) =>
        Enqueue("EndAuthSession", () =>
        {
            RequireApi().EndAuthSession(_gs, player.Value);
            if (_pendingAuths.Remove(player, out var pending))
                pending.Tcs.TrySetCanceled();
        });

    /// <summary>Whether an auth'd player owns <paramref name="appId"/>; NoAuth when never authenticated on this server.</summary>
    public Task<UserHasLicenseForAppResult> UserHasLicenseForAppAsync(SteamId player, uint appId) =>
        Enqueue("UserHasLicenseForApp", () => (UserHasLicenseForAppResult)RequireApi().UserHasLicenseForApp(_gs, player.Value, appId));

    /// <summary>
    /// The server's own auth session ticket — deliver it to connecting clients over your game protocol;
    /// they exchange it for a client ticket, which you feed to <see cref="AuthPlayerAsync"/>.
    /// null → native failure. IP binding (pSnid) is intentionally not supported: pass-through tickets are portable.
    /// </summary>
    public Task<byte[]?> GetOwnAuthSessionTicketAsync() => Enqueue<byte[]?>("GetAuthSessionTicket", () =>
    {
        var api = RequireApi();
        int capacity = 1024;
        unsafe
        {
            uint* pWritten = stackalloc uint[1];
            while (true)
            {
                var buffer = new byte[capacity];
                uint written, handle;
                fixed (byte* pBuf = buffer)
                {
                    *pWritten = 0;
                    handle = api.GetAuthSessionTicket(_gs, (IntPtr)pBuf, capacity, (IntPtr)pWritten, IntPtr.Zero);
                    written = *pWritten;
                }

                if (handle == 0)
                    return null;
                if (written < capacity || capacity >= 8192)
                    return written == capacity ? buffer : buffer[..(int)written];
                capacity *= 2; // would have truncated → grow and retry
            }
        }
    });

    public Task CancelAuthTicketAsync(uint authTicketHandle) =>
        Enqueue("CancelAuthTicket", () => RequireApi().CancelAuthTicket(_gs, authTicketHandle));

    /// <summary>Allocates a fake-STEAMID connection slot for bots.</summary>
    public Task<SteamId> CreateUnauthenticatedUserConnectionAsync() =>
        Enqueue("CreateUnauthenticatedUserConnection", () => new SteamId(RequireApi().CreateUnauthenticatedUserConnection(_gs)));

    /// <summary>Updates a player's advertised name/score in the server browser.</summary>
    public Task<bool> UpdateUserDataAsync(SteamId player, string playerName, uint score) =>
        Enqueue("BUpdateUserData", () => Utf8.Eval(playerName, (Api: RequireApi(), Gs: _gs, Player: player, Score: score),
            static (s, name) => s.Api.BUpdateUserData(s.Gs, s.Player.Value, name, s.Score)));

    /// <summary>Requests clan/group membership; answer arrives via <see cref="ISteamGameServerClientEvents.OnGroupStatus"/>.</summary>
    public Task<bool> RequestUserGroupStatusAsync(SteamId user, SteamId group) =>
        Enqueue("RequestUserGroupStatus", () => RequireApi().RequestUserGroupStatus(_gs, user.Value, group.Value));
}
