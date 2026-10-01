using System.Reflection;
using System.Security.Cryptography;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Modules;

/// <summary>
/// One upload into a private temp folder under the module root (same filesystem, so the
/// final rename is atomic). Every refusal deletes the temp folder, is audited, and poisons
/// the session.
/// </summary>
internal sealed class RulesModuleUpload : IRulesModuleUpload
{
    sealed class Incoming(ModuleFile file)
    {
        public ModuleFile File { get; } = file;
        public FileStream? Stream;
        public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public bool Done;
    }

    readonly RulesModuleRegistry _registry;
    readonly ModuleAnnouncement _announcement;
    readonly string _instanceId;
    readonly string _temp;
    readonly long _maxBytes;
    readonly Dictionary<string, Incoming> _files;
    long _bytes;
    bool _finished;

    public RulesModuleUpload(RulesModuleRegistry registry, ModuleAnnouncement announcement, string instanceId, string tempDir, long maxBytes)
    {
        _registry = registry;
        _announcement = announcement;
        _instanceId = instanceId;
        _temp = tempDir;
        _maxBytes = maxBytes;
        _files = ModuleNames.Files(announcement.Assembly, announcement.Sha256, announcement.Deps)
            .ToDictionary(f => f.Sha256, f => new Incoming(f), StringComparer.Ordinal);
        Directory.CreateDirectory(_temp);
    }

    public string Sha256 => _announcement.Sha256;

    public async Task Write(string sha256, string fileName, ReadOnlyMemory<byte> data, bool last, CancellationToken ct = default)
    {
        if (_finished) throw HostRefusal.Precondition("upload_closed", $"the upload of {Sha256} is finished");
        var sha = ModuleNames.Normalize(sha256);
        if (!_files.TryGetValue(sha, out var f))
            throw await Refuse("undeclared_file", $"{fileName} ({sha}) is not a file module {Sha256} announced", ct);
        if (f.Done)
            throw await Refuse("file_repeated", $"{f.File.Name} was already complete", ct);
        if (!string.Equals(fileName, f.File.Name, StringComparison.Ordinal))
            throw await Refuse("file_name_mismatch", $"{sha} was announced as {f.File.Name}, the upload names it {fileName}", ct);
        _bytes += data.Length;
        if (_bytes > _maxBytes)
            throw await Refuse("too_large", $"module {Sha256} exceeds {_maxBytes} bytes", ct);

        f.Stream ??= new FileStream(Path.Combine(_temp, f.File.Name), FileMode.CreateNew, FileAccess.Write);
        await f.Stream.WriteAsync(data, ct);
        f.Hash.AppendData(data.Span);
        if (!last) return;

        await f.Stream.DisposeAsync();
        f.Stream = null;
        f.Done = true;
        var actual = Convert.ToHexStringLower(f.Hash.GetHashAndReset());
        if (actual != f.File.Sha256)
            throw await Refuse("hash_mismatch", $"{f.File.Name}: announced {f.File.Sha256}, received bytes hash to {actual}", ct);
    }

    public async Task<RulesModuleAnswer> Complete(CancellationToken ct = default)
    {
        if (_finished) throw HostRefusal.Precondition("upload_closed", $"the upload of {Sha256} is finished");
        if (_files.Values.FirstOrDefault(f => !f.Done) is { } missing)
            throw await Refuse("incomplete", $"{missing.File.Name} was never completed", ct);

        var main = Path.Combine(_temp, ModuleNames.FileOf(_announcement.Assembly));
        string? name;
        try { name = AssemblyName.GetAssemblyName(main).Name; }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException) { name = null; }
        if (!string.Equals(name, _announcement.Assembly, StringComparison.Ordinal))
            throw await Refuse("assembly_mismatch", $"the main file is {(name is null ? "not an assembly" : $"assembly {name}")}, announced {_announcement.Assembly}", ct);

        _finished = true;
        return await _registry.Commit(_announcement, _instanceId, _temp, _bytes, ct);
    }

    async Task<HostRefusal> Refuse(string reason, string message, CancellationToken ct)
    {
        Cleanup();
        return await _registry.RefusalAsync(Sha256, _instanceId,
            reason == "too_large" ? HostRefusal.Exhausted(reason, message) : HostRefusal.Precondition(reason, message), ct);
    }

    void Cleanup()
    {
        _finished = true;
        foreach (var f in _files.Values)
        {
            f.Stream?.Dispose();
            f.Stream = null;
            f.Hash.Dispose();
        }
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        // After Commit the temp folder is gone (moved or deleted); otherwise this removes it.
        Cleanup();
        return ValueTask.CompletedTask;
    }
}
