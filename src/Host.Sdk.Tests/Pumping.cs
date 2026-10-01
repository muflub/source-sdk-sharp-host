namespace SourceSharp.Host.Sdk.Tests;

/// <summary>The game's frame loop, for facts: Pump() until the awaited completion has been delivered.</summary>
public static class Pumping
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<T> Pumped<T>(this HostSdk sdk, Task<T> task, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? Timeout);
        while (!task.IsCompleted)
        {
            sdk.Pump();
            if (DateTime.UtcNow > until) throw new TimeoutException("the SDK never delivered the completion");
            if (!task.IsCompleted) await Task.Delay(5);
        }
        return await task;
    }

    public static async Task PumpUntil(this HostSdk sdk, Func<bool> condition, TimeSpan? timeout = null, string what = "condition")
    {
        var until = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            sdk.Pump();
            if (condition()) return;
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"{what} never held (connected {sdk.Session.Connected}, last stream error {sdk.Session.LastStreamError ?? "none"}, "
                    + $"reconnects {sdk.Metrics.Reconnects}, connect failures {sdk.Metrics.ConnectFailures}, heartbeats {sdk.Metrics.HeartbeatsSent}/{sdk.Metrics.HeartbeatsAcked} acked)");
            await Task.Delay(5);
        }
    }

    /// <summary>Waits without pumping (for "nothing is delivered until Pump" facts).</summary>
    public static async Task Until(Func<bool> condition, TimeSpan? timeout = null, string what = "condition")
    {
        var until = DateTime.UtcNow + (timeout ?? Timeout);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"{what} never held");
            await Task.Delay(5);
        }
    }
}
