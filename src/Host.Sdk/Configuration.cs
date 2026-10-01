using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceSharp.Host.Sdk;

/// <summary>How this process found its host (plan §6.6, D-H13).</summary>
public enum HostMode
{
    /// <summary>A game pod: <c>DESCENT_SERVICE</c>, <c>DESCENT_INSTANCE_ID</c>, <c>DESCENT_INSTANCE_TOKEN</c>, <c>DESCENT_SIDECAR_INFO</c>.</summary>
    Pod,
    /// <summary><c>-hostlocal &lt;file&gt;</c> or <c>DESCENT_LOCAL=&lt;file&gt;</c>: a local service wrote <see cref="HostLocalFile"/>.</summary>
    LocalFile,
    /// <summary><c>-hostlocal inproc[:dir]</c> or <c>DESCENT_LOCAL=inproc[:dir]</c>: the dev-only SourceSharp.Host.Local runs the service in this process.</summary>
    LocalInProc,
}

/// <summary>Where the host is and who this instance is. After resolution every mode connects the same way.</summary>
public sealed record HostEndpoint(HostMode Mode, Uri Service, string InstanceId, string Token, Uri? Sidecar);

/// <summary>
/// The local-mode contract (D-H13): the JSON a local service writes (<c>make local</c> writes
/// <c>bin/local/local-host.json</c>) and the string <c>SourceSharp.Host.Local.LocalHost.StartAsync</c>
/// returns. Exactly:
/// <code>{ "service": "http://127.0.0.1:5001", "instanceId": "local-hub", "token": "…" }</code>
/// <c>service</c> is the game API's base address (h2c), <c>instanceId</c> an instance row the
/// service created with <c>token</c> as its token. An optional <c>"sidecar"</c> names a PeerInfo
/// endpoint; without it IPeers asks the service only. Unknown fields are ignored.
/// </summary>
public sealed record HostLocalFile(
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("sidecar")] string? Sidecar = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    /// <summary>Parses and checks the record; a missing field is a <see cref="HostConfigException"/>, never a half-configured SDK.</summary>
    public static HostLocalFile Parse(string json, string origin)
    {
        HostLocalFile? f;
        try { f = JsonSerializer.Deserialize<HostLocalFile>(json); }
        catch (JsonException e) { throw new HostConfigException(HostConfigError.LocalFileInvalid, $"{origin} is not a local-host record: {e.Message}"); }
        if (f is null || string.IsNullOrWhiteSpace(f.Service) || string.IsNullOrWhiteSpace(f.InstanceId) || string.IsNullOrWhiteSpace(f.Token))
            throw new HostConfigException(HostConfigError.LocalFileInvalid, $"{origin} lacks service, instanceId or token");
        if (!Uri.TryCreate(f.Service, UriKind.Absolute, out _))
            throw new HostConfigException(HostConfigError.LocalFileInvalid, $"{origin}: service '{f.Service}' is not an absolute URI");
        return f;
    }
}

public enum HostConfigError { MissingEnvironment, LocalFileMissing, LocalFileInvalid, LocalHostMissing, LocalHostInvalid }

/// <summary>A configuration the SDK cannot start from, named. Thrown once, at start, never from a game call.</summary>
public sealed class HostConfigException(HostConfigError error, string message) : Exception(message)
{
    public HostConfigError Error { get; } = error;
}

/// <summary>
/// The in-process local host (D-H13, dev only): the SDK loads <see cref="AssemblyName"/> by name and
/// calls <c>public static Task&lt;string&gt; StartAsync(string dataDir, CancellationToken ct)</c> on
/// <see cref="TypeName"/> by reflection. It returns the <see cref="HostLocalFile"/> JSON. Host.Sdk
/// never references that assembly; a release build of the game simply does not ship it.
/// </summary>
public static class LocalHostContract
{
    public const string AssemblyName = "SourceSharp.Host.Local";
    public const string TypeName = "SourceSharp.Host.Local.LocalHost";
    public const string MethodName = "StartAsync";
    public const string DefaultDataDir = "./descent-local/";
    public const string MissingMessage = "the dev build does not include SourceSharp.Host.Local";
}

/// <summary>Starts a host inside this process and returns its <see cref="HostLocalFile"/> JSON.</summary>
public interface ILocalHostStarter
{
    Task<string> StartAsync(string dataDir, CancellationToken ct);
}

/// <summary>The default starter: <see cref="LocalHostContract"/> by reflection. No native code, no compile-time reference.</summary>
public sealed class ReflectionLocalHostStarter : ILocalHostStarter
{
    readonly Func<string, Assembly> _load;

    public ReflectionLocalHostStarter() : this(n => Assembly.Load(n)) { }
    /// <param name="load">Loads an assembly by name (a fact substitutes one that fails).</param>
    public ReflectionLocalHostStarter(Func<string, Assembly> load) => _load = load;

    public async Task<string> StartAsync(string dataDir, CancellationToken ct)
    {
        Assembly asm;
        try { asm = _load(LocalHostContract.AssemblyName); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new HostConfigException(HostConfigError.LocalHostMissing, $"{LocalHostContract.MissingMessage} ({e.GetType().Name})");
        }
        var type = asm.GetType(LocalHostContract.TypeName)
                   ?? throw new HostConfigException(HostConfigError.LocalHostInvalid, $"{asm.GetName().Name} has no {LocalHostContract.TypeName}");
        var method = type.GetMethod(LocalHostContract.MethodName, BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(CancellationToken)]);
        if (method is null || method.ReturnType != typeof(Task<string>))
            throw new HostConfigException(HostConfigError.LocalHostInvalid,
                $"{LocalHostContract.TypeName} lacks public static Task<string> {LocalHostContract.MethodName}(string, CancellationToken)");
        var task = (Task<string>)method.Invoke(null, [dataDir, ct])!;
        return await task.ConfigureAwait(false);
    }
}

/// <summary>Chooses the mode and reads its inputs (plan §6.6 pod mode, D-H13 local modes).</summary>
public static class HostSdkConfig
{
    public const string ServiceVar = "DESCENT_SERVICE";
    public const string InstanceIdVar = "DESCENT_INSTANCE_ID";
    public const string TokenVar = "DESCENT_INSTANCE_TOKEN";
    public const string SidecarVar = "DESCENT_SIDECAR_INFO";
    public const string LocalVar = "DESCENT_LOCAL";
    public const string LocalArg = "-hostlocal";
    public const string DefaultSidecar = "127.0.0.1:5011";
    public const string InProc = "inproc";

    /// <summary>
    /// The local selector, if any: <c>-hostlocal &lt;value&gt;</c> in the process arguments wins over
    /// <c>DESCENT_LOCAL</c>; neither means pod mode.
    /// </summary>
    public static string? LocalSelector(IReadOnlyList<string> args, Func<string, string?> env)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], LocalArg, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-') || args[i + 1].StartsWith('+'))
                throw new HostConfigException(HostConfigError.LocalFileMissing, $"{LocalArg} needs a file or '{InProc}[:dir]'");
            return args[i + 1];
        }
        var v = env(LocalVar);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>Which mode a selector means, and its argument (the file, or the in-process data directory).</summary>
    public static (HostMode Mode, string? Argument) Select(string? selector)
    {
        if (selector is null) return (HostMode.Pod, null);
        if (string.Equals(selector, InProc, StringComparison.OrdinalIgnoreCase)) return (HostMode.LocalInProc, LocalHostContract.DefaultDataDir);
        if (selector.StartsWith(InProc + ":", StringComparison.OrdinalIgnoreCase))
        {
            var dir = selector[(InProc.Length + 1)..];
            return (HostMode.LocalInProc, dir.Length > 0 ? dir : LocalHostContract.DefaultDataDir);
        }
        return (HostMode.LocalFile, selector);
    }

    /// <summary>Resolves the endpoint from this process's arguments and environment.</summary>
    public static Task<HostEndpoint> Resolve(CancellationToken ct = default) =>
        Resolve(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable, new ReflectionLocalHostStarter(), ct);

    public static async Task<HostEndpoint> Resolve(IReadOnlyList<string> args, Func<string, string?> env, ILocalHostStarter starter, CancellationToken ct = default)
    {
        var (mode, argument) = Select(LocalSelector(args, env));
        switch (mode)
        {
            case HostMode.LocalFile:
            {
                var path = argument!;
                if (!File.Exists(path))
                    throw new HostConfigException(HostConfigError.LocalFileMissing, $"local-host file {path} does not exist (is the local service running?)");
                return FromLocal(HostMode.LocalFile, HostLocalFile.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), path));
            }
            case HostMode.LocalInProc:
            {
                var json = await starter.StartAsync(argument!, ct).ConfigureAwait(false);
                return FromLocal(HostMode.LocalInProc, HostLocalFile.Parse(json, $"{LocalHostContract.TypeName}.{LocalHostContract.MethodName}"));
            }
            default:
            {
                var missing = new[] { ServiceVar, InstanceIdVar, TokenVar }.Where(v => string.IsNullOrWhiteSpace(env(v))).ToList();
                if (missing.Count > 0)
                    throw new HostConfigException(HostConfigError.MissingEnvironment,
                        $"pod mode needs {string.Join(", ", missing)} (or {LocalArg} <file|{InProc}> / {LocalVar} for local mode)");
                var sidecar = env(SidecarVar);
                return new HostEndpoint(HostMode.Pod, Http(env(ServiceVar)!), env(InstanceIdVar)!, env(TokenVar)!,
                    Http(string.IsNullOrWhiteSpace(sidecar) ? DefaultSidecar : sidecar));
            }
        }
    }

    static HostEndpoint FromLocal(HostMode mode, HostLocalFile f) =>
        new(mode, Http(f.Service), f.InstanceId, f.Token, string.IsNullOrWhiteSpace(f.Sidecar) ? null : Http(f.Sidecar));

    /// <summary><c>host:port</c> or a URI, as an h2c base address.</summary>
    public static Uri Http(string address) =>
        address.Contains("://", StringComparison.Ordinal) ? new Uri(address) : new Uri($"http://{address}");
}
