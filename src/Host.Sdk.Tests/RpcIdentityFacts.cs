using Grpc.Core;

namespace SourceSharp.Host.Sdk.Tests;

/// <summary>
/// A call's instance identity is fixed when the call starts. A same-process hop rebinds the
/// SDK to another instance; a call still retrying across that rebind must keep the identity it
/// started with, or its write lands on the new instance under a fresh idempotency key (a kill
/// drop on the next level's floor, plan_sdk_2_7 §3a).
/// </summary>
public class RpcIdentityFacts
{
    static HostEndpoint At(string instance) =>
        new(HostMode.Pod, new Uri("http://127.0.0.1:1"), instance, "token-" + instance, null);

    static AsyncUnaryCall<string> Answer(Task<string> response) =>
        new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    /// <summary>Calls once failing with Unavailable; the endpoint switches to lvl-9 after that first attempt.</summary>
    static async Task<(HostResult<string> Result, List<string> SentAs)> RetryAcrossRebind(bool pinned)
    {
        var endpoint = At("hub-1");
        using var rpc = new Rpc(() => endpoint, SdkHarness.FastOptions(), new HostMetrics(), CancellationToken.None);
        var sentAs = new List<string>();
        Metadata? auth = pinned ? rpc.Auth() : null;
        var result = await rpc.Call((m, _, _) =>
        {
            sentAs.Add(m.GetValue("x-instance-id")!);
            if (sentAs.Count == 1)
            {
                endpoint = At("lvl-9"); // the hop's Rebind, while the first attempt is out
                return Answer(Task.FromException<string>(new RpcException(new Status(StatusCode.Unavailable, "down"))));
            }
            return Answer(Task.FromResult("ok"));
        }, auth: auth);
        return (result, sentAs);
    }

    [Fact]
    public async Task A_retry_after_a_rebind_goes_out_as_the_instance_the_call_started_as()
    {
        var (result, sentAs) = await RetryAcrossRebind(pinned: false);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(["hub-1", "hub-1"], sentAs);
    }

    [Fact]
    public async Task A_call_given_an_identity_keeps_it_across_a_rebind()
    {
        var (result, sentAs) = await RetryAcrossRebind(pinned: true);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(["hub-1", "hub-1"], sentAs);
    }

    [Fact]
    public async Task A_call_started_after_the_rebind_goes_out_as_the_new_instance()
    {
        var endpoint = At("hub-1");
        using var rpc = new Rpc(() => endpoint, SdkHarness.FastOptions(), new HostMetrics(), CancellationToken.None);
        endpoint = At("lvl-9");
        string? sentAs = null;
        await rpc.Call((m, _, _) => { sentAs = m.GetValue("x-instance-id"); return Answer(Task.FromResult("ok")); });
        Assert.Equal("lvl-9", sentAs);
    }
}
