using Grpc.Core;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Modules;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Api;

/// <summary>The D-H9 handshake as the API sees it, over Host.Modules' registry: hash first, bytes on request.</summary>
public sealed class ModuleBoot(IRulesModuleRegistry registry, RulesProvider provider) : IModuleBoot
{
    public async Task<P.ModuleAnswer> Announce(InstanceRecord caller, P.RulesModule module, CancellationToken ct)
    {
        var announcement = new ModuleAnnouncement(module.Assembly, module.Version, module.Sha256, module.ContractVersion,
            module.Deps.Select(d => new ModuleDependencyInfo(d.Assembly, d.Sha256)).ToList());
        var answer = await registry.Announce(announcement, caller.Id, ct);
        return answer.Kind switch
        {
            RulesModuleAnswerKind.Known => P.ModuleAnswer.Known,
            RulesModuleAnswerKind.Send => P.ModuleAnswer.Send,
            RulesModuleAnswerKind.Pending => P.ModuleAnswer.Pending,
            _ => throw new HostRefusal(
                answer.Reason == "contract_unsupported" ? RefusalCode.Unimplemented
                : answer.Reason == "quarantined" ? RefusalCode.PermissionDenied : RefusalCode.FailedPrecondition,
                answer.Reason ?? "module_refused", answer.Message ?? "the rules module was refused"),
        };
    }

    /// <summary>The chunks of the files announced by this instance's Booting; a refusal comes back as accepted=false with its reason.</summary>
    public async Task<P.UploadModuleResponse> Upload(InstanceRecord caller, IAsyncStreamReader<P.ModuleChunk> chunks, CancellationToken ct)
    {
        if (caller.RulesSha256 is not { Length: > 0 } sha)
            return new P.UploadModuleResponse { Accepted = false, Reason = "not_announced" };
        try
        {
            await using var upload = await registry.BeginUpload(sha, caller.Id, ct);
            await foreach (var chunk in chunks.ReadAllAsync(ct))
                await upload.Write(chunk.Sha256, chunk.FileName, chunk.Data.Memory, chunk.Last, ct);
            var answer = await upload.Complete(ct);
            return new P.UploadModuleResponse { Accepted = true, Reason = answer.Kind == RulesModuleAnswerKind.Pending ? "pending_approval" : "" };
        }
        catch (HostRefusal e)
        {
            return new P.UploadModuleResponse { Accepted = false, Reason = e.Reason };
        }
    }

    public void NoteBooted(InstanceRecord instance, string sha256) => provider.NoteBooted(instance.Id, instance.ModImage, sha256);
}
