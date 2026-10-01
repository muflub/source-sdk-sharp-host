using System.Net;
using System.Net.Sockets;
using Descent.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace SourceSharp.Host.Local;

/// <summary>
/// D-H13 in-process: the real service started inside the game process on loopback. The SDK
/// calls <see cref="StartAsync"/> by reflection (`-hostlocal inproc`) and connects to the
/// returned connection record exactly as it would to a pod.
/// </summary>
public static class LocalHost
{
    static WebApplication? _app;
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Starts the host once per process; returns the JSON connection record (service, instanceId, token).</summary>
    public static async Task<string> StartAsync(string dataDir, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(Path.GetFullPath(dataDir), "local-host.json");
            if (_app is null)
            {
                if (File.Exists(file)) File.Delete(file); // a stale token from an earlier run must not be read
                var settings = LocalProfile.Settings(dataDir, gameApiPort: FreeLoopbackPort(), adminPort: FreeLoopbackPort());
                _app = ServiceApp.Build(["--environment", "Development"], b => b.Configuration.AddInMemoryCollection(settings));
                await _app.StartAsync(ct);
            }
            // The instance manager creates the local hub asynchronously; its "pod" writes the file.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(file))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"the local host did not write {file} within 30 s");
                await Task.Delay(50, ct);
            }
            return await File.ReadAllTextAsync(file, ct);
        }
        finally { Gate.Release(); }
    }

    public static async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    static int FreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
