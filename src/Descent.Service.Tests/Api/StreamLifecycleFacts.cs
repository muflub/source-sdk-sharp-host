using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Tests.Api;

/// <summary>
/// A stream's end is the crash signal (§6.1) only when it is the instance's current stream and the
/// service is not the one going away: a graceful stop must leave pods to be adopted (§7.4), and a
/// reconnect must not crash the instance when its superseded stream finally ends.
/// </summary>
public class StreamLifecycleFacts
{
    static async Task<InstanceState> StateOf(ApiHarness h, string id) =>
        (await h.Data.ReadAsync((tx, _) => tx.Instances.Get(id)))!.State;

    static async Task Beat(AsyncDuplexStreamingCall<P.Heartbeat, P.ServerCommand> call, ulong seq)
    {
        await call.RequestStream.WriteAsync(new P.Heartbeat { Seq = seq, Players = 1 });
        Assert.True(await call.ResponseStream.MoveNext()); // the ack: the stream is registered and live
    }

    [Fact]
    public async Task The_instances_stream_ending_while_it_is_current_crashes_it()
    {
        await using var h = await ApiHarness.Start();
        var (row, auth) = await h.Instance("lvl-1", InstanceKind.Level, 1);
        using (var call = h.Instances.Connect(auth))
        {
            await Beat(call, 1);
            await call.RequestStream.CompleteAsync();
            while (await call.ResponseStream.MoveNext()) { }
        }
        Assert.True(await Until(async () => await StateOf(h, row.Id) == InstanceState.Crashed));
    }

    [Fact]
    public async Task A_superseded_stream_ending_does_not_crash_the_instance()
    {
        await using var h = await ApiHarness.Start();
        var (row, auth) = await h.Instance("lvl-1", InstanceKind.Level, 1);
        var old = h.Instances.Connect(auth);
        await Beat(old, 1);
        using var fresh = h.Instances.Connect(auth);
        await Beat(fresh, 2);
        await old.RequestStream.CompleteAsync();
        try { while (await old.ResponseStream.MoveNext()) { } } catch (RpcException) { }
        old.Dispose();
        await Task.Delay(300);
        Assert.Equal(InstanceState.Live, await StateOf(h, row.Id));
    }

    [Fact]
    public async Task A_graceful_service_stop_does_not_crash_connected_instances()
    {
        var h = await ApiHarness.Start();
        var (row, auth) = await h.Instance("lvl-1", InstanceKind.Level, 1);
        var call = h.Instances.Connect(auth);
        await Beat(call, 1);
        await h.Service.App.StopAsync();
        Assert.Equal(InstanceState.Live, await StateOf(h, row.Id));
        call.Dispose();
        await h.DisposeAsync();
    }

    static async Task<bool> Until(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++) { if (await condition()) return true; await Task.Delay(50); }
        return false;
    }
}
