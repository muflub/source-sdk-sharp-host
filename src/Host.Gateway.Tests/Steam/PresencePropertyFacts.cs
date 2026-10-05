using SourceSharp.Host.Gateway.Tests.Support;
using Client = SourceSharp.Host.FakeClient.FakeClient;

namespace SourceSharp.Host.Gateway.Tests.Steam;

/// <summary>
/// One connection per SteamID (plan_steam_auth S-17) as a property: over random sequences of connects (some at
/// the same moment, with Steam's verdict delayed so admissions overlap), keepalives, silences, closes and
/// revocations, no two live sessions ever hold one SteamID.
/// </summary>
public class PresencePropertyFacts
{
    const ulong A = 76561198000000101UL, B = 76561198000000102UL;

    static void AssertOneHolderPerSteamId(Rig rig, int step, string op)
    {
        var holders = rig.Relay.Snapshot()
            .Where(s => s.Steamid.Length > 0 && s.Auth is "valid" or "reauthing")
            .GroupBy(s => s.Steamid)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(s => s.SessionId))}")
            .ToList();
        Assert.True(holders.Count == 0, $"step {step} ({op}): two live sessions hold one SteamID: {string.Join("; ", holders)}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task No_two_live_sessions_ever_hold_one_steamid(int seed)
    {
        var random = new Random(seed);
        await using var rig = Rig.WithSteam();
        rig.SetDefault(rig.Backend("hub"));
        var clients = new List<Client>();
        for (var i = 0; i < 4; i++) clients.Add(rig.Client(i % 2 == 0 ? A : B, $"p{i}"));
        var admitted = 0;

        for (var step = 0; step < 120; step++)
        {
            var c = clients[random.Next(clients.Count)];
            string op;
            switch (random.Next(7))
            {
                case 0 or 1:
                    op = $"connect {c.Name}";
                    c.TicketTail = BitConverter.GetBytes(step);
                    c.StartHandshake();
                    break;
                case 2:
                    // Two clients of one SteamID at the same moment, verdicts delayed so both admissions overlap.
                    op = "simultaneous connects";
                    rig.SteamBackend!.VerdictDelay = TimeSpan.FromSeconds(1);
                    foreach (var same in clients.Where(x => x.SteamId == c.SteamId))
                    {
                        same.TicketTail = BitConverter.GetBytes(step * 10 + same.Name[1]);
                        same.StartHandshake();
                    }
                    await Task.Delay(30);
                    rig.SteamBackend.VerdictDelay = TimeSpan.Zero;
                    rig.Time.Advance(TimeSpan.FromSeconds(1));
                    break;
                case 3:
                    op = $"keepalive {c.Name}";
                    if (c.Connected) c.SendKeepalive();
                    break;
                case 4:
                    var silence = TimeSpan.FromSeconds(random.Next(0, 20));
                    op = $"silence {silence.TotalSeconds}s";
                    rig.Time.Advance(silence);
                    break;
                case 5:
                    op = $"close {c.Name}";
                    rig.Relay.CloseSession(rig.Next(), c.LocalEndPoint.ToString(), "test");
                    break;
                default:
                    op = $"revoke {c.SteamId}";
                    rig.SteamBackend!.Revoke(c.SteamId, "test");
                    break;
            }
            await Task.Delay(20);
            AssertOneHolderPerSteamId(rig, step, op);
            if (rig.Relay.Snapshot().Any(s => s.Auth == "valid")) admitted++;
        }
        Assert.True(admitted > 0, "no session was ever admitted: the sequence never exercised presence");
    }
}
