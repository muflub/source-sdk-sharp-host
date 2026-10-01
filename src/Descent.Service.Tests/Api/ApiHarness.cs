using Descent.Service.Api;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Testing;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Tests.Api;

/// <summary>
/// The real service in-process on free loopback ports (plan §6 gate: in-process gRPC facts):
/// :memory: database, FakeInstanceHost, FakeGameRules, a fake module handshake, and instance
/// rows whose tokens the harness knows, so every call goes through the real auth interceptor.
/// </summary>
public sealed class ApiHarness : IAsyncDisposable
{
    public required TestService Service { get; init; }
    public required string MapsDir { get; init; }
    public FakeGameRules Rules { get; } = new();
    public GrpcChannel Channel { get; private set; } = null!;
    public IHostData Data => Service.App.Services.GetRequiredService<IHostData>();
    int _requests;

    public static async Task<ApiHarness> Start(Action<WebApplicationBuilder>? configure = null, bool realModules = false)
    {
        var maps = Path.Combine(Path.GetTempPath(), $"descent-maps-{Guid.NewGuid():N}");
        var rules = new FakeGameRules();
        var svc = await TestService.StartAsync(b =>
        {
            b.Services.AddSingleton<IRulesProvider>(new SingleRulesProvider(rules));
            if (!realModules) b.Services.AddSingleton<IModuleBoot, KnownModules>();
            configure?.Invoke(b);
        }, new()
        {
            ["Data:Path"] = ":memory:",
            ["MapPool:MapsPath"] = maps,
            ["Instances:VerifyModLabel"] = "false",
        });
        var h = new ApiHarness { Service = svc, MapsDir = maps };
        // The instance manager adopts in the background after start and crashes any row it finds
        // without a pod; it creates its hub only after adoption. Rows go in after that.
        var data = svc.App.Services.GetRequiredService<IHostData>();
        for (var i = 0; i < 200 && !(await data.ReadAsync((tx, _) => tx.Instances.NonTerminal())).Any(x => x.Kind == InstanceKind.Hub); i++)
            await Task.Delay(25);
        h.Channel = GrpcChannel.ForAddress($"http://127.0.0.1:{svc.Bound.Port(ListenerRole.GameApi)}");
        return h;
    }

    public string Req() => $"req-{Interlocked.Increment(ref _requests)}";

    /// <summary>A live instance row with a known token, as the manager would have created it.</summary>
    public async Task<(InstanceRecord Instance, Metadata Auth)> Instance(string id, InstanceKind kind, int depth = 0, InstanceState state = InstanceState.Live)
    {
        var token = $"token-{id}";
        var now = DateTimeOffset.UtcNow;
        var row = await Data.WriteAsync((tx, _) => tx.Instances.Add(new InstanceRecord(id, kind, state, depth, null, null,
            $"descent-{id}", $"uid-{id}", "127.0.0.1", 27015, InstanceManager.TokenHash(token), null, null, "fake", 4321, now,
            now, now, null, null, null, null, now, 0, 27015)));
        return (row, new Metadata { { "x-instance-id", id }, { "x-instance-token", token } });
    }

    public P.CharacterService.CharacterServiceClient Characters => new(Channel);
    public P.ItemService.ItemServiceClient Items => new(Channel);
    public P.InstanceService.InstanceServiceClient Instances => new(Channel);
    public P.PartyService.PartyServiceClient Parties => new(Channel);
    public P.TradeService.TradeServiceClient Trades => new(Channel);

    /// <summary>Creates a character through the hub and leases it on <paramref name="auth"/>'s instance.</summary>
    public async Task<(string Id, Google.Protobuf.ByteString Token)> LeasedCharacter(Metadata hub, Metadata on, string account = "76561198000000001")
    {
        var ch = await Characters.CreateAsync(new P.CreateCharacterRequest { RequestId = Req(), Account = account, ClassName = "scout", Name = "Ann" }, hub);
        var lease = await Characters.LeaseAsync(new P.LeaseRequest { RequestId = Req(), CharacterId = ch.Id }, on);
        return (ch.Id, lease.Token);
    }

    public static string Reason(RpcException e) => e.Trailers.GetValue("x-reason") ?? e.Status.Detail.Split(':')[0];

    public async ValueTask DisposeAsync()
    {
        Channel.Dispose();
        await Service.DisposeAsync();
        try { Directory.Delete(MapsDir, true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>Every announced module is known: the unit tier has no module registry (Host.Modules has its own facts).</summary>
    sealed class KnownModules : IModuleBoot
    {
        public Task<P.ModuleAnswer> Announce(InstanceRecord caller, P.RulesModule module, CancellationToken ct) => Task.FromResult(P.ModuleAnswer.Known);
        public Task<P.UploadModuleResponse> Upload(InstanceRecord caller, IAsyncStreamReader<P.ModuleChunk> chunks, CancellationToken ct) =>
            Task.FromResult(new P.UploadModuleResponse { Accepted = true });
        public void NoteBooted(InstanceRecord instance, string sha256) { }
    }
}
