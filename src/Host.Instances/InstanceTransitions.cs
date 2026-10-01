using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>§7.3's state machine as a table: which transitions the manager may persist.</summary>
public static class InstanceTransitions
{
    static readonly Dictionary<InstanceState, InstanceState[]> Allowed = new()
    {
        [InstanceState.Requested] = [InstanceState.LevelReady, InstanceState.Creating, InstanceState.Draining, InstanceState.Failed],
        [InstanceState.LevelReady] = [InstanceState.Creating, InstanceState.Draining, InstanceState.Failed],
        [InstanceState.Creating] = [InstanceState.Booting, InstanceState.Draining, InstanceState.Failed, InstanceState.Crashed],
        [InstanceState.Booting] = [InstanceState.Live, InstanceState.Draining, InstanceState.Failed, InstanceState.Crashed],
        [InstanceState.Live] = [InstanceState.Suspect, InstanceState.Draining, InstanceState.Reaped, InstanceState.Crashed],
        [InstanceState.Suspect] = [InstanceState.Live, InstanceState.Draining, InstanceState.Reaped, InstanceState.Crashed],
        [InstanceState.Draining] = [InstanceState.Reaped, InstanceState.Crashed],
        [InstanceState.Reaped] = [],
        [InstanceState.Crashed] = [],
        [InstanceState.Failed] = [],
    };

    public static bool IsAllowed(InstanceState from, InstanceState to) => Allowed[from].Contains(to);

    public static string Action(InstanceState to) => "instance." + to.ToString().ToLowerInvariant();
}

/// <summary>A transition the table does not allow; a bug in the caller, never a business refusal.</summary>
public sealed class InvalidInstanceTransition(string id, InstanceState from, InstanceState to)
    : InvalidOperationException($"instance {id}: {from} → {to} is not a transition");
