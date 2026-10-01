using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using SourceSharp.Host.Abstractions;
using P = SourceSharp.Host.Proto;

namespace Descent.Service.Tests.Api;

/// <summary>D-H9 through the real API and Host.Modules: hash first, bytes on request.</summary>
public class ModuleHandshakeFacts
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "module-fixtures", "ModuleFixture.NeverDrop.dll");

    static P.RulesModule Announce(byte[] bytes) => new()
    {
        Assembly = "ModuleFixture.NeverDrop", Version = "1.0.0", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ContractVersion = SourceSharp.Host.Contracts.HostContract.Version,
    };

    [Fact]
    public async Task The_first_pod_is_asked_to_send_and_the_second_uploads_nothing()
    {
        Assert.True(File.Exists(Fixture), $"fixture missing: {Fixture}");
        await using var h = await ApiHarness.Start(realModules: true);
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1, InstanceState.Booting);
        var (_, b) = await h.Instance("lvl-b", InstanceKind.Level, 1, InstanceState.Booting);
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var module = Announce(bytes);

        var first = await h.Instances.BootingAsync(new P.BootingRequest { RequestId = h.Req(), SdkVersion = "1.0.0", Module = module }, a);
        Assert.Equal(P.ModuleAnswer.Send, first.Module);
        using (var upload = h.Instances.UploadModule(a))
        {
            await upload.RequestStream.WriteAsync(new P.ModuleChunk { Sha256 = module.Sha256, FileName = "ModuleFixture.NeverDrop.dll", Data = ByteString.CopyFrom(bytes), Last = true });
            await upload.RequestStream.CompleteAsync();
            var r = await upload;
            Assert.True(r.Accepted, r.Reason);
        }

        var second = await h.Instances.BootingAsync(new P.BootingRequest { RequestId = h.Req(), SdkVersion = "1.0.0", Module = module }, b);
        Assert.Equal(P.ModuleAnswer.Known, second.Module);
    }

    [Fact]
    public async Task An_upload_whose_bytes_differ_from_the_announced_hash_is_refused()
    {
        await using var h = await ApiHarness.Start(realModules: true);
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1, InstanceState.Booting);
        var bytes = await File.ReadAllBytesAsync(Fixture);
        var module = Announce(bytes);
        await h.Instances.BootingAsync(new P.BootingRequest { RequestId = h.Req(), SdkVersion = "1.0.0", Module = module }, a);
        using var upload = h.Instances.UploadModule(a);
        var tampered = bytes.ToArray();
        tampered[^1] ^= 0xFF;
        await upload.RequestStream.WriteAsync(new P.ModuleChunk { Sha256 = module.Sha256, FileName = "ModuleFixture.NeverDrop.dll", Data = ByteString.CopyFrom(tampered), Last = true });
        await upload.RequestStream.CompleteAsync();
        var r = await upload;
        Assert.Equal((false, "hash_mismatch"), (r.Accepted, r.Reason));
    }

    [Fact]
    public async Task A_module_built_for_another_contract_major_is_refused_at_booting()
    {
        await using var h = await ApiHarness.Start(realModules: true);
        var (_, a) = await h.Instance("lvl-a", InstanceKind.Level, 1, InstanceState.Booting);
        var module = Announce(await File.ReadAllBytesAsync(Fixture));
        module.ContractVersion = "2.0.0";
        var e = await Assert.ThrowsAsync<RpcException>(() => h.Instances.BootingAsync(new P.BootingRequest { RequestId = h.Req(), SdkVersion = "1.0.0", Module = module }, a).ResponseAsync);
        Assert.Equal((StatusCode.Unimplemented, "contract_unsupported"), (e.StatusCode, ApiHarness.Reason(e)));
    }
}
