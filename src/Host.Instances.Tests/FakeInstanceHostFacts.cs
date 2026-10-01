using Microsoft.Extensions.Time.Testing;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Instances;
using SourceSharp.Host.Testing;

namespace SourceSharp.Host.Instances.Tests;

public class FakeInstanceHostFacts
{
    static PodSpec Spec(string id = "01J9ZABCDEFGHJKMNPQRSTVWXY") =>
        GamePodBuilder.Build(new InstanceOptions(), new ModOptions(), PodSpecFacts.Row(InstanceKind.Hub, id), "tok");

    [Fact]
    public async Task Create_gives_a_fresh_uid_and_an_added_event()
    {
        var host = new FakeInstanceHost(new FakeTimeProvider());
        var r = await host.Create(Spec());
        Assert.False(string.IsNullOrEmpty(r.Uid));
        var e = host.TakePending().Single();
        Assert.Equal((PodEventType.Added, r.Uid, PodPhase.Pending), (e.Type, e.Pod.Uid, e.Pod.Phase));
    }

    [Fact]
    public async Task Delete_with_grace_removes_the_pod_only_at_the_deadline()
    {
        var clock = new FakeTimeProvider();
        var host = new FakeInstanceHost(clock);
        var r = await host.Create(Spec());
        Assert.True(await host.Delete(r.Name, r.Uid, TimeSpan.FromSeconds(60)));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.NotNull(host.Get(r.Name));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(host.Get(r.Name));
        Assert.Equal(PodEventType.Deleted, host.TakePending().Last().Type);
    }

    [Fact]
    public async Task Delete_with_another_uid_is_refused()
    {
        var host = new FakeInstanceHost(new FakeTimeProvider());
        var r = await host.Create(Spec());
        Assert.False(await host.Delete(r.Name, "other", TimeSpan.Zero));
        Assert.NotNull(host.Get(r.Name));
    }

    [Fact]
    public async Task Deleting_the_pod_deletes_its_secret()
    {
        var host = new FakeInstanceHost(new FakeTimeProvider());
        var r = await host.Create(Spec());
        Assert.Single(host.Secrets);
        await host.Delete(r.Name, r.Uid, TimeSpan.Zero);
        Assert.Empty(host.Secrets);
    }

    [Fact]
    public async Task Watch_starts_with_existing_pods_then_follows_changes()
    {
        var host = new FakeInstanceHost(new FakeTimeProvider());
        var r = await host.Create(Spec());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<PodEventType>();
        var watching = Task.Run(async () =>
        {
            await foreach (var e in host.Watch(cts.Token)) { seen.Add(e.Type); if (seen.Count == 2) break; }
        });
        while (seen.Count == 0) await Task.Delay(5);
        host.SetReady(r.Name);
        await watching;
        Assert.Equal([PodEventType.Added, PodEventType.Modified], seen);
    }

    [Fact]
    public async Task List_filters_by_label()
    {
        var host = new FakeInstanceHost(new FakeTimeProvider());
        host.Seed("a", new Dictionary<string, string> { ["app"] = "descent-game" });
        host.Seed("b", new Dictionary<string, string> { ["app"] = "other" });
        Assert.Equal(["a"], (await host.List("app=descent-game")).Select(p => p.Name));
    }
}
