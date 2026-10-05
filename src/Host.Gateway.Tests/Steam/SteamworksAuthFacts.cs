using System.Collections.Concurrent;
using System.Net;
using SourceSharp.Host.Gateway.Steam;
using SourceSharp.Host.Steam;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>A scripted <see cref="IGameServerHost"/>: each auth waits on a completion the fact controls.</summary>
sealed class ScriptedHost(ISteamGameServerEvents events, Func<int, Exception?> startFails, int index) : IGameServerHost
{
    public ISteamGameServerEvents Events { get; } = events;
    public ConcurrentDictionary<ulong, TaskCompletionSource<PlayerAuthResult>> Pending { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<(byte[] Packet, uint Ip, ushort Port)> In { get; } = new();
    public ConcurrentQueue<(byte[] Packet, uint Ip, ushort Port)> Out { get; } = new();
    public bool Disposed { get; private set; }

    public Task StartAsync(GameServerConfiguration config, CancellationToken ct) =>
        startFails(index) is { } e ? Task.FromException(e) : Task.CompletedTask;

    public Task<PlayerAuthResult> AuthPlayerAsync(SteamId player, byte[] ticket, CancellationToken ct)
    {
        Calls.Enqueue($"begin:{player.Value}");
        var tcs = new TaskCompletionSource<PlayerAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[player.Value] = tcs;
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public void Answer(ulong player, AuthSessionResponse response, BeginAuthSessionResult immediate = BeginAuthSessionResult.Ok) =>
        Pending[player].SetResult(new PlayerAuthResult(new SteamId(player), immediate,
            immediate == BeginAuthSessionResult.Ok ? new AuthValidation(new SteamId(player), response, new SteamId(player)) : null));

    public Task EndAuthSessionAsync(SteamId player) { Calls.Enqueue($"end:{player.Value}"); return Task.CompletedTask; }

    public Task<bool> HandleIncomingPacketAsync(ReadOnlyMemory<byte> packet, uint srcIp, ushort srcPort)
    {
        In.Enqueue((packet.ToArray(), srcIp, srcPort));
        return Task.FromResult(true);
    }

    public Task<(byte[] Packet, uint Ip, ushort Port)?> TryGetNextOutgoingPacketAsync() =>
        Task.FromResult<(byte[], uint, ushort)?>(Out.TryDequeue(out var p) ? p : null);

    public ConcurrentQueue<(SteamListing Listing, bool Advertise)> Listings { get; } = new();
    public Task ApplyListingAsync(SteamListing listing, bool advertise) { Listings.Enqueue((listing, advertise)); return Task.CompletedTask; }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public class SteamworksAuthFacts
{
    const ulong Alice = 76561197960287930UL, Bob = 76561197960287931UL;
    static readonly byte[] Ticket = [1, 2, 3, 4];
    static readonly TimeSpan T = TimeSpan.FromSeconds(5);
    readonly List<ScriptedHost> _hosts = [];

    SteamworksAuthServer Server(Func<int, Exception?>? startFails = null) =>
        new(events =>
        {
            lock (_hosts)
            {
                var h = new ScriptedHost(events, startFails ?? (_ => null), _hosts.Count);
                _hosts.Add(h);
                return h;
            }
        }, () => new GameServerConfiguration { AppId = 243750, GamePort = 27015, QueryPort = GameServerConfiguration.SharedQueryPort },
           TimeSpan.FromMilliseconds(10));

    async Task<(SteamworksAuthServer, ScriptedHost)> LoggedOn()
    {
        var s = Server();
        await s.StartAsync(default);
        await Until(() => _hosts.Count == 1);
        _hosts[0].Events.OnLoggedOn(new SteamId(90071992547409921UL), false);
        await Until(() => s.Status.LoggedOn);
        return (s, _hosts[0]);
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + T;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never held");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Before_logon_every_begin_is_unavailable()
    {
        var s = Server();
        Assert.Equal(AuthOutcome.Unavailable, (await s.BeginAsync("s1", Alice, Ticket, default)).Outcome);
    }

    [Fact]
    public async Task Logon_reports_the_game_server_steamid_and_an_epoch()
    {
        var (s, _) = await LoggedOn();
        Assert.Equal(90071992547409921UL, s.Status.GameServerSteamId);
        Assert.Equal(12, s.Status.Epoch.Length);
    }

    [Fact]
    public async Task A_failed_logon_is_retried_with_a_new_host_and_a_new_epoch()
    {
        var s = Server(i => i == 0 ? new SteamApiException(SteamResult.Timeout, "no logon") : null);
        await s.StartAsync(default);
        await Until(() => _hosts.Count == 2);
        Assert.True(_hosts[0].Disposed);
        _hosts[1].Events.OnLoggedOn(new SteamId(90071992547409921UL), false);
        await Until(() => s.Status.LoggedOn);
    }

    [Fact]
    public async Task A_valid_ticket_is_ok_and_end_releases_it_once()
    {
        var (s, h) = await LoggedOn();
        var pending = s.BeginAsync("s1", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        h.Answer(Alice, AuthSessionResponse.Ok);
        Assert.True((await pending.WaitAsync(T)).Ok);

        s.End("s1");
        s.End("s1");
        Assert.Equal([$"begin:{Alice}", $"end:{Alice}"], h.Calls);
    }

    [Theory]
    [InlineData(AuthSessionResponse.AuthTicketInvalidAlreadyUsed, AuthOutcome.AlreadyClaimed)]
    [InlineData(AuthSessionResponse.VacBanned, AuthOutcome.Denied)]
    [InlineData(AuthSessionResponse.NoLicenseOrExpired, AuthOutcome.Denied)]
    public async Task Steams_answer_maps_to_the_outcome(AuthSessionResponse response, AuthOutcome outcome)
    {
        var (s, h) = await LoggedOn();
        var pending = s.BeginAsync("s1", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        h.Answer(Alice, response);
        Assert.Equal(outcome, (await pending.WaitAsync(T)).Outcome);
    }

    [Fact]
    public async Task An_immediate_refusal_is_denied_with_a_readable_reason()
    {
        var (s, h) = await LoggedOn();
        var pending = s.BeginAsync("s1", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        h.Answer(Alice, AuthSessionResponse.Ok, BeginAuthSessionResult.GameMismatch);
        Assert.Equal((AuthOutcome.Denied, "ticket for another game"), ((await pending.WaitAsync(T)).Outcome, (await pending).Reason));
    }

    [Fact]
    public async Task A_rejoin_ends_the_old_auth_first_and_a_late_end_for_the_old_session_ends_nothing()
    {
        var (s, h) = await LoggedOn();
        var first = s.BeginAsync("old", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        h.Answer(Alice, AuthSessionResponse.Ok);
        await first.WaitAsync(T);
        h.Pending.Clear();

        var second = s.BeginAsync("new", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        h.Answer(Alice, AuthSessionResponse.Ok);
        Assert.True((await second.WaitAsync(T)).Ok);

        s.End("old"); // the game's PlayerLeft for the old session, arriving late
        Assert.Equal([$"begin:{Alice}", $"end:{Alice}", $"begin:{Alice}"], h.Calls);
    }

    [Fact]
    public async Task Two_pending_begins_for_one_steamid_leave_one_auth_and_a_late_end_of_the_first_ends_nothing()
    {
        var (s, h) = await LoggedOn();
        _ = s.BeginAsync("first", Alice, Ticket, default);
        await Until(() => h.Calls.Count == 1);
        var second = s.BeginAsync("second", Alice, Ticket, default);
        await Until(() => h.Calls.Count == 3);

        s.End("first");

        Assert.Equal([$"begin:{Alice}", $"end:{Alice}", $"begin:{Alice}"], h.Calls);
        h.Answer(Alice, AuthSessionResponse.Ok);
        Assert.True((await second.WaitAsync(T)).Ok);
    }

    [Fact]
    public async Task A_verdict_for_a_session_ended_meanwhile_is_released_at_steam()
    {
        var (s, h) = await LoggedOn();
        var pending = s.BeginAsync("s1", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        s.End("s1");
        h.Answer(Alice, AuthSessionResponse.Ok);
        await pending.WaitAsync(T);
        await Until(() => h.Calls.Count(c => c == $"end:{Alice}") >= 1);
    }

    [Fact]
    public async Task A_cancelled_begin_holds_nothing()
    {
        var (s, h) = await LoggedOn();
        using var cts = new CancellationTokenSource();
        var pending = s.BeginAsync("s1", Alice, Ticket, cts.Token);
        await Until(() => h.Pending.ContainsKey(Alice));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(T));
        s.End("s1");
        Assert.DoesNotContain($"end:{Alice}", h.Calls); // the SDK ended it when the token fired
    }

    [Fact]
    public async Task A_later_failed_validation_revokes_the_valid_session_only()
    {
        var (s, h) = await LoggedOn();
        var revoked = new ConcurrentQueue<(string, string)>();
        s.Revoked += (session, reason) => revoked.Enqueue((session, reason));
        var a = s.BeginAsync("a", Alice, Ticket, default);
        var b = s.BeginAsync("b", Bob, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice) && h.Pending.ContainsKey(Bob));

        // Bob's verdict is still pending: Steam's own answer is the Begin's to report, not a revocation.
        h.Events.OnAuthValidation(new AuthValidation(new SteamId(Bob), AuthSessionResponse.VacBanned, SteamId.Invalid));
        h.Answer(Alice, AuthSessionResponse.Ok);
        await a.WaitAsync(T);

        h.Events.OnAuthValidation(new AuthValidation(new SteamId(Alice), AuthSessionResponse.AuthTicketCanceled, SteamId.Invalid));

        Assert.Equal([("a", "ticket canceled")], revoked);
        Assert.Contains($"end:{Alice}", h.Calls);
    }

    [Fact]
    public async Task A_kick_from_steam_revokes()
    {
        var (s, h) = await LoggedOn();
        var revoked = new ConcurrentQueue<string>();
        s.Revoked += (session, _) => revoked.Enqueue(session);
        var a = s.BeginAsync("a", Alice, Ticket, default);
        await Until(() => h.Pending.ContainsKey(Alice));
        h.Answer(Alice, AuthSessionResponse.Ok);
        await a.WaitAsync(T);

        h.Events.OnClientKickRequested(new SteamId(Alice), DenyReason.Cheater);

        Assert.Equal(["a"], revoked);
    }

    [Fact]
    public async Task Logged_off_makes_new_begins_unavailable()
    {
        var (s, h) = await LoggedOn();
        h.Events.OnLoggedOff();
        Assert.Equal(AuthOutcome.Unavailable, (await s.BeginAsync("s1", Alice, Ticket, default)).Outcome);
    }

    [Fact]
    public void Addresses_cross_to_the_sdk_in_host_byte_order()
    {
        Assert.Equal(0xCB007107u, SteamworksAuthServer.HostOrder(IPAddress.Parse("203.0.113.7")));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), SteamworksAuthServer.FromHostOrder(0xCB007107u));
    }

    [Fact]
    public async Task Packets_cross_with_the_senders_address_and_come_back_in_order()
    {
        var (s, h) = await LoggedOn();
        s.HandleIncoming([0xFF, 0xFF, 0xFF, 0xFF, 0x73], IPEndPoint.Parse("203.0.113.7:27005"));
        await Until(() => !h.In.IsEmpty);
        Assert.True(h.In.TryPeek(out var got));
        Assert.Equal((0xCB007107u, (ushort)27005), (got.Ip, got.Port));

        h.Out.Enqueue(([1], 0xCB007107u, 27011));
        h.Out.Enqueue(([2], 0xCB007108u, 27012));
        var sent = await s.DrainOutgoingAsync();
        Assert.Equal(["203.0.113.7:27011", "203.0.113.8:27012"], sent.Select(p => p.To.ToString()));
        Assert.Equal([1, 2], sent.Select(p => (int)p.Data[0]));
    }

    [Fact]
    public async Task A_listing_applied_before_logon_reaches_the_host_once_it_logs_on()
    {
        var s = Server();
        var listing = new SteamListing("Descent", "town", "TF2 Descent", 32, "rpg", "eu", new Dictionary<string, string> { ["k"] = "v" });
        s.ApplyListing(listing, advertise: true);
        await s.StartAsync(default);
        await Until(() => _hosts.Count == 1 && !_hosts[0].Listings.IsEmpty);
        Assert.Equal((listing, true), _hosts[0].Listings.Single());
    }
}
