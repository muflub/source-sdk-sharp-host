using System.Collections;
using System.Net;
using Grpc.Net.Client;

namespace SourceSharp.Host.FakeGame;

/// <summary>
/// The fake game server hosted in-process (plan §7.6): the same code as the exe, started from a
/// pod's arguments and environment, with a control client. Facts create one per "pod".
/// </summary>
public sealed class FakeGameHost : IAsyncDisposable
{
    readonly GrpcChannel _channel;
    public FakeGameServer Server { get; }
    public Control.FakeGameControl.FakeGameControlClient Control { get; }
    public string InstanceId => Server.Config.InstanceId;
    public IPEndPoint ControlEndPoint => Server.ControlEndPoint;
    public Task<int> Exited => Server.Exited;

    FakeGameHost(FakeGameServer server)
    {
        Server = server;
        _channel = GrpcChannel.ForAddress($"http://{server.ControlEndPoint}");
        Control = new Control.FakeGameControl.FakeGameControlClient(_channel);
    }

    public static async Task<FakeGameHost> StartAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env, CancellationToken ct = default)
    {
        var server = new FakeGameServer(FakeGameConfig.From(args, env));
        try { await server.StartAsync(ct); }
        catch { await server.DisposeAsync(); throw; }
        return new FakeGameHost(server);
    }

    /// <summary>The process environment as the dictionary the fake reads.</summary>
    public static IReadOnlyDictionary<string, string?> ProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value);

    public Task StopAsync(int exitCode = 0) => Server.StopAsync(exitCode);

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await Server.DisposeAsync();
    }
}
