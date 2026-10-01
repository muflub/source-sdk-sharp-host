using System.Reflection;
using System.Security.Cryptography;
using Google.Protobuf;
using SourceSharp.Host.Contracts;
using P = SourceSharp.Host.Proto;

namespace SourceSharp.Host.Sdk;

/// <summary>A private dependency of the rules module, shipped beside it (never the framework, never Host.Contracts).</summary>
public sealed record RulesModuleDependency(string Assembly, string Path);

/// <summary>The rules module on disk (D-H9): what Booting announces and UploadModule streams on SEND.</summary>
public sealed record RulesModuleFile(string Assembly, string Version, string Path, string ContractVersion, IReadOnlyList<RulesModuleDependency> Deps);

/// <summary>Finds the rules module this process runs.</summary>
public interface IRulesModuleSource
{
    /// <summary>The module, or null when the process loaded none (Booting then announces an empty module and the host decides).</summary>
    RulesModuleFile? Find();
}

/// <summary>A fixed module (a fact, or a game that knows its path).</summary>
public sealed class FixedModuleSource(RulesModuleFile? module) : IRulesModuleSource
{
    public RulesModuleFile? Find() => module;
}

/// <summary>
/// The default: the loaded assembly marked <c>[assembly: HostRulesModule(...)]</c>. Its private
/// dependencies are the assemblies it references that sit beside it on disk, except Host.Contracts
/// (the host's own copy is the one that must match) and the SDK's own assemblies.
/// </summary>
public sealed class LoadedAssemblyModuleSource : IRulesModuleSource
{
    static readonly HashSet<string> NeverShipped = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(HostContract).Assembly.GetName().Name!, typeof(HostSdk).Assembly.GetName().Name!, typeof(P.Blob).Assembly.GetName().Name!,
    };

    public RulesModuleFile? Find()
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => !a.IsDynamic && a.GetCustomAttribute<HostRulesModuleAttribute>() is not null && a.Location.Length > 0);
        return asm is null ? null : Describe(asm);
    }

    public static RulesModuleFile Describe(Assembly asm)
    {
        var dir = System.IO.Path.GetDirectoryName(asm.Location)!;
        var deps = new List<RulesModuleDependency>();
        foreach (var r in asm.GetReferencedAssemblies())
        {
            if (r.Name is null || NeverShipped.Contains(r.Name)) continue;
            var path = System.IO.Path.Combine(dir, r.Name + ".dll");
            if (File.Exists(path)) deps.Add(new RulesModuleDependency(r.Name, path));
        }
        var name = asm.GetName();
        // The contract this SDK was built with: the module and the SDK load the same Host.Contracts in the game.
        return new RulesModuleFile(name.Name!, name.Version?.ToString() ?? "0.0.0", asm.Location, HostContract.Version, deps);
    }
}

/// <summary>The handshake's wire half: hashes, the announcement, and the chunked upload.</summary>
internal static class ModuleHandshake
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static (P.RulesModule Announce, IReadOnlyList<(string Sha, string FileName, byte[] Bytes)> Files) Prepare(RulesModuleFile? module)
    {
        if (module is null) return (new P.RulesModule { ContractVersion = HostContract.Version }, []);
        var main = File.ReadAllBytes(module.Path);
        var files = new List<(string, string, byte[])> { (Sha256Hex(main), Path.GetFileName(module.Path), main) };
        var announce = new P.RulesModule
        {
            Assembly = module.Assembly, Version = module.Version, Sha256 = files[0].Item1, ContractVersion = module.ContractVersion,
        };
        foreach (var d in module.Deps)
        {
            var bytes = File.ReadAllBytes(d.Path);
            var sha = Sha256Hex(bytes);
            files.Add((sha, Path.GetFileName(d.Path), bytes));
            announce.Deps.Add(new P.ModuleDependency { Assembly = d.Assembly, Sha256 = sha });
        }
        return (announce, files);
    }

    /// <summary>Streams every file in chunks; the last chunk of each file says so.</summary>
    public static async Task<P.UploadModuleResponse> Upload(SdkCore core, IReadOnlyList<(string Sha, string FileName, byte[] Bytes)> files)
    {
        using var call = core.Instance.UploadModule(core.Rpc.Auth(), DateTime.UtcNow + core.Options.RpcTimeout * 6, core.Stopping);
        var size = Math.Max(1, core.Options.ModuleChunkBytes);
        foreach (var (sha, name, bytes) in files)
        {
            var offset = 0;
            do
            {
                var n = Math.Min(size, bytes.Length - offset);
                await call.RequestStream.WriteAsync(new P.ModuleChunk
                {
                    Sha256 = sha, FileName = name, Data = ByteString.CopyFrom(bytes, offset, n), Last = offset + n >= bytes.Length,
                }).ConfigureAwait(false);
                offset += n;
            } while (offset < bytes.Length);
        }
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        return await call.ResponseAsync.ConfigureAwait(false);
    }
}
