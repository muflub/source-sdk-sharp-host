namespace SourceSharp.Host.Abstractions;

/// <summary>One line of /healthz: the DB, the Kubernetes API, the pool, the gateway link (H1f).</summary>
public sealed record HealthItem(string Name, bool Ok, string Detail);

public interface IHealthReporter
{
    Task<HealthItem> CheckAsync(CancellationToken ct);
}
