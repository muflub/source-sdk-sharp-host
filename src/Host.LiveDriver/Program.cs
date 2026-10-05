using System.Net;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Net.Client;
using SourceSharp.Host.FakeClient;
using SourceSharp.Host.FakeGame.Control;

namespace SourceSharp.Host.LiveDriver;

/// <summary>
/// live/*.sh's hands (TIER=fake):
///   clients &lt;gateway ip:port&gt; &lt;steamid[:tail],…&gt; &lt;seconds&gt; &lt;status.json&gt;
///       connect each fake client through the gateway, follow retries, rewrite status.json every second;
///       ":tail" goes after the SteamID in the toy ticket (plan_steam_auth: "deny:&lt;why&gt;" makes the fake Steam refuse it)
///   attempts &lt;gateway ip:port&gt; &lt;steamid[:tail]&gt; &lt;count&gt; &lt;out.json&gt;
///       one client handshakes &lt;count&gt; times in a row, each to its answer; out.json lists them
///   ctl &lt;fake ip:port&gt; &lt;Method&gt; ['&lt;request json&gt;']
///       one FakeGameControl call; the reply as JSON on stdout
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "clients" when args.Length == 5 => await Clients(IPEndPoint.Parse(args[1]), args[2].Split(','), int.Parse(args[3]), args[4]),
                "ctl" when args.Length is 3 or 4 => await Ctl(args[1], args[2], args.Length == 4 ? args[3] : "{}"),
                "attempts" when args.Length == 5 => await Attempts(IPEndPoint.Parse(args[1]), args[2], int.Parse(args[3]), args[4]),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"livedriver: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: livedriver clients <gateway ip:port> <steamid,...> <seconds> <status.json> | ctl <ip:port> <Method> ['<json>']");
        return 2;
    }

    static async Task<int> Clients(IPEndPoint gateway, string[] steamIds, int seconds, string statusFile)
    {
        var clients = steamIds.Select(id => Make(id, TimeSpan.FromSeconds(1))(gateway)).ToList();
        foreach (var c in clients)
        {
            try { await c.ConnectAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { Console.Error.WriteLine($"livedriver: {c.SteamId} did not connect"); }
        }
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            foreach (var c in clients)
                if (c.Connected) { try { await c.KeepaliveAsync(TimeSpan.FromMilliseconds(500)); } catch (TimeoutException) { } }
            var status = clients.Select(c => new
            {
                steamid = c.SteamId.ToString(), connected = c.Connected, instance = c.InstanceId, retries = c.Retries,
                local = c.LocalEndPoint.ToString(), reject = c.RejectReason, steps = c.Steps.TakeLast(6), reauths = c.ReauthsAnswered,
            });
            var tmp = statusFile + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(status));
            File.Move(tmp, statusFile, overwrite: true);
            await Task.Delay(1000);
        }
        foreach (var c in clients) c.Dispose();
        return 0;
    }

    /// <summary>"steamid[:tail]": the client, with the tail after the SteamID in its toy ticket.</summary>
    static Func<IPEndPoint, SourceSharp.Host.FakeClient.FakeClient> Make(string spec, TimeSpan resend)
    {
        var colon = spec.IndexOf(':');
        var id = colon < 0 ? spec : spec[..colon];
        var tail = colon < 0 ? [] : System.Text.Encoding.ASCII.GetBytes(spec[(colon + 1)..]);
        return gw => new SourceSharp.Host.FakeClient.FakeClient(gw, ulong.Parse(id), $"live-{id[^4..]}", IPAddress.Any) { TicketTail = tail, ResendInterval = resend };
    }

    static async Task<int> Attempts(IPEndPoint gateway, string spec, int count, string outFile)
    {
        using var c = Make(spec, TimeSpan.FromSeconds(1))(gateway);
        var results = new List<object>();
        for (var i = 0; i < count; i++)
        {
            string outcome;
            try { outcome = "connected " + await c.ConnectAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException e) { outcome = e.Message; }  // "rejected: <reason>"
            catch (TimeoutException) { outcome = "no answer"; }
            results.Add(new { attempt = i + 1, outcome });
        }
        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(results));
        return 0;
    }

    static async Task<int> Ctl(string address, string method, string json)
    {
        using var channel = GrpcChannel.ForAddress($"http://{address}");
        var client = new FakeGameControl.FakeGameControlClient(channel);
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        IMessage reply = method switch
        {
            "Status" => await client.StatusAsync(new Empty()),
            "Join" => await client.JoinAsync(parser.Parse<JoinRequest>(json)),
            "Leave" => await client.LeaveAsync(parser.Parse<PlayerRequest>(json)),
            "Player" => await client.PlayerAsync(parser.Parse<PlayerRequest>(json)),
            "Kill" => await client.KillAsync(parser.Parse<KillRequest>(json)),
            "Pickup" => await client.PickupAsync(parser.Parse<ItemRequest>(json)),
            "Die" => await client.DieAsync(parser.Parse<PlayerRequest>(json)),
            "Backpack" => await client.BackpackAsync(parser.Parse<PlayerRequest>(json)),
            "Party" => await client.PartyAsync(parser.Parse<PartyRequest>(json)),
            "Descend" => await client.DescendAsync(parser.Parse<TravelRequest>(json)),
            "TownPortal" => await client.TownPortalAsync(parser.Parse<TravelRequest>(json)),
            "ReturnThroughPortal" => await client.ReturnThroughPortalAsync(parser.Parse<TravelRequest>(json)),
            "LostAndFound" => await client.LostAndFoundAsync(parser.Parse<PlayerRequest>(json)),
            "Reclaim" => await client.ReclaimAsync(parser.Parse<ItemRequest>(json)),
            "Crash" => await client.CrashAsync(parser.Parse<CrashRequest>(json)),
            "Hang" => await client.HangAsync(new Empty()),
            _ => throw new ArgumentException($"unknown method {method}"),
        };
        Console.WriteLine(JsonFormatter.Default.Format(reply));
        return 0;
    }
}
