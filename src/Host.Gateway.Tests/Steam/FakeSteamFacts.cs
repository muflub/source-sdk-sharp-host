using System.Text;
using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Gateway.Steam;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>The fake Steam behaves as the relay's facts assume the real one does (plan_steam_auth S-16).</summary>
public class FakeSteamFacts
{
    const ulong Alice = 76561197960287930UL, Bob = 76561197960287931UL;
    static readonly byte[] TicketA = Encoding.ASCII.GetBytes("ticket-a"), TicketB = Encoding.ASCII.GetBytes("ticket-b");
    readonly FakeTimeProvider _time = new();
    readonly FakeSteamBackend _backend = new();

    async Task<FakeSteamAuthServer> Started()
    {
        var s = new FakeSteamAuthServer(_backend, _time);
        await s.StartAsync(default);
        return s;
    }

    [Fact]
    public async Task A_plain_ticket_is_valid_for_its_steamid()
    {
        var s = await Started();
        var v = await s.BeginAsync("s1", Alice, TicketA, default);
        Assert.Equal((AuthOutcome.Ok, Alice), (v.Outcome, v.OwnerSteamId));
        Assert.Equal(Alice, s.Active["s1"]);
    }

    [Fact]
    public async Task A_deny_ticket_is_denied_with_its_reason_and_holds_no_auth()
    {
        var s = await Started();
        var v = await s.BeginAsync("s1", Alice, Encoding.ASCII.GetBytes("TOYTxxxxxxxxdeny:vac banned"), default);
        Assert.Equal((AuthOutcome.Denied, "vac banned"), (v.Outcome, v.Reason));
        Assert.Empty(s.Active);
    }

    [Fact]
    public async Task Logged_off_is_unavailable_and_asks_nothing()
    {
        var s = new FakeSteamAuthServer(_backend, _time); // never started
        var v = await s.BeginAsync("s1", Alice, TicketA, default);
        Assert.Equal(AuthOutcome.Unavailable, v.Outcome);
        Assert.Empty(s.Calls);
    }

    [Fact]
    public async Task A_ticket_replayed_after_a_restart_is_already_claimed()
    {
        var first = await Started();
        Assert.True((await first.BeginAsync("s1", Alice, TicketA, default)).Ok);
        await first.DisposeAsync();

        var restarted = await Started();
        Assert.NotEqual(first.Status.Epoch, restarted.Status.Epoch);
        Assert.Equal(AuthOutcome.AlreadyClaimed, (await restarted.BeginAsync("s1", Alice, TicketA, default)).Outcome);
        // A fresh ticket from the same player is fine: that is what in-game re-auth sends (S-20).
        Assert.True((await restarted.BeginAsync("s1", Alice, TicketB, default)).Ok);
    }

    [Fact]
    public async Task The_verdict_waits_for_the_delay_on_the_servers_clock()
    {
        _backend.VerdictDelay = TimeSpan.FromSeconds(2);
        var s = await Started();
        var pending = s.BeginAsync("s1", Alice, TicketA, default);
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.False(pending.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
    }

    [Fact]
    public async Task A_script_that_never_answers_ends_only_by_cancellation()
    {
        _backend.Script = (_, _) => null;
        var s = await Started();
        using var cts = new CancellationTokenSource();
        var pending = s.BeginAsync("s1", Alice, TicketA, cts.Token);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_second_session_for_one_steamid_ends_the_first_before_it_begins()
    {
        var s = await Started();
        await s.BeginAsync("s1", Alice, TicketA, default);
        await s.BeginAsync("s2", Alice, TicketB, default);
        Assert.Equal(["begin:s1", "end:s1", "begin:s2"], s.Calls);
        Assert.Equal(["s2"], s.Active.Keys);
    }

    [Fact]
    public async Task End_is_idempotent_and_logged_once()
    {
        var s = await Started();
        await s.BeginAsync("s1", Alice, TicketA, default);
        s.End("s1");
        s.End("s1");
        s.End("never");
        Assert.Equal(["begin:s1", "end:s1"], s.Calls);
    }

    [Fact]
    public async Task Revoke_reaches_the_session_holding_that_steamid_only()
    {
        var s = await Started();
        await s.BeginAsync("s1", Alice, TicketA, default);
        await s.BeginAsync("s2", Bob, TicketB, default);
        var revoked = new List<(string, string)>();
        s.Revoked += (session, reason) => revoked.Add((session, reason));

        _backend.Revoke(Alice, "ticket canceled");

        Assert.Equal([("s1", "ticket canceled")], revoked);
        Assert.Equal(["s2"], s.Active.Keys);
    }

    [Fact]
    public async Task Outgoing_packets_drain_once_in_order()
    {
        var s = await Started();
        var to = System.Net.IPEndPoint.Parse("203.0.113.9:27011");
        s.Send([1], to);
        s.Send([2], to);
        Assert.Equal([1, 2], (await s.DrainOutgoingAsync()).Select(p => (int)p.Data[0]));
        Assert.Empty(await s.DrainOutgoingAsync());
    }
}
