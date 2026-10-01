using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SourceSharp.Host.Abstractions;
using SourceSharp.Host.Contracts;

namespace SourceSharp.Host.Testing;

/// <summary>
/// The unit tier's <see cref="IMapCompiler"/> (plan §9.2): fixture bytes named after the
/// request, a log of every call (so a fact can require the stimulus reached it), and a
/// configurable failure.
/// </summary>
public sealed class FakeMapCompiler : IMapCompiler
{
    public string LinkerIdentity { get; init; } = "fake-linker-1";

    /// <summary>Fail a link when this says so; the default links everything.</summary>
    public Func<LinkRequest, bool> FailWhen { get; set; } = _ => false;

    /// <summary>Held open until released, to observe concurrency; null = never block.</summary>
    public SemaphoreSlim? Gate { get; set; }

    public ConcurrentQueue<LinkRequest> Links { get; } = new();
    public ConcurrentQueue<LibrarySource> Bakes { get; } = new();
    int _running, _maxRunning;
    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public async Task<LevelFiles> LinkAsync(LinkRequest request, CancellationToken ct = default)
    {
        Links.Enqueue(request);
        var now = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxRunning)) < now && Interlocked.CompareExchange(ref _maxRunning, now, seen) != seen) { }
        try
        {
            if (Gate is { } gate) await gate.WaitAsync(ct);
            var sw = Stopwatch.StartNew();
            if (FailWhen(request))
                throw new MapCompileFailure($"fake link refused {request.MapName} (seed {request.Seed})", "fake: refused");
            Directory.CreateDirectory(request.OutputDirectory);
            string F(string ext) => Path.Combine(request.OutputDirectory, request.MapName + ext);
            await File.WriteAllTextAsync(F(".bsp"), $"VBSP fake {request.MapName} seed={request.Seed}", ct);
            await File.WriteAllTextAsync(F(".nav3d"), $"NAV3 fake {request.MapName}", ct);
            await File.WriteAllTextAsync(F(".map2d"), $"MAP2 fake {request.MapName}", ct);
            await File.WriteAllTextAsync(F(".yaml"), $"library: {request.Library}.vmf\n# seed {request.Seed}\n", ct);
            return new LevelFiles(request.MapName, F(".bsp"), F(".nav3d"), F(".map2d"), F(".yaml"), sw.Elapsed, "fake: linked");
        }
        finally { Interlocked.Decrement(ref _running); }
    }

    public async Task<BakeResult> BakeAsync(LibrarySource library, BakeSettings settings, CancellationToken ct = default)
    {
        Bakes.Enqueue(library);
        var version = File.Exists(library.Vmf) ? MapIdentity.LibraryVersionOf(library) : "fake-version";
        var pack = FakePack.Write(settings.OutputPath, library.Library, ["hall", "stairs", "start"]);
        await Task.CompletedTask;
        return new BakeResult(settings.OutputPath, pack.PackId, version, pack.Rooms.Count, pack.Rooms.Count, 0, 0, TimeSpan.Zero, "fake: baked");
    }
}

/// <summary>A fake <c>.roompack</c>: JSON the <see cref="FakePackValidator"/> reads. Anything else is "corrupt".</summary>
public sealed record FakePack(string Namespace, string PackId, IReadOnlyList<string> Rooms, string VmfSha256 = "00")
{
    public static FakePack Write(string path, string ns, IReadOnlyList<string> rooms, string? packId = null)
    {
        var pack = new FakePack(ns, packId ?? Guid.NewGuid().ToString("D"), rooms, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(pack));
        return pack;
    }

    public static byte[] Bytes(string ns, IReadOnlyList<string> rooms, string? packId = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new FakePack(ns, packId ?? Guid.NewGuid().ToString("D"), rooms, Guid.NewGuid().ToString("N"))));
}

/// <summary>
/// The unit tier's <see cref="IPackValidator"/>: the same verdicts as the real one on the
/// fake format: unreadable, namespace ≠ library, mod ≠ the service's, and a lint rule
/// (a room called "broken"), each a problem.
/// </summary>
public sealed class FakePackValidator(string mod) : IPackValidator
{
    public int Calls;

    public Task<PackInspection> ValidateAsync(PackCandidate c, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        var problems = new List<string>();
        FakePack? pack = null;
        try { pack = JsonSerializer.Deserialize<FakePack>(File.ReadAllText(c.Path)); }
        catch (JsonException e) { problems.Add($"corrupt: {e.Message}"); }
        if (pack is null && problems.Count == 0) problems.Add("corrupt: empty");
        if (c.Mod != mod) problems.Add($"wrong_mod: '{c.Mod}' is not '{mod}'");
        if (pack is not null)
        {
            if (pack.Namespace != c.Library) problems.Add($"wrong_library: the pack's namespace is '{pack.Namespace}', not '{c.Library}'");
            if (pack.Rooms.Contains("broken")) problems.Add("lint: room 'broken' fails lint");
        }
        var rooms = pack?.Rooms.Select(r => new RoomInfo(r, r == "start" ? ["start"] : r == "stairs" ? ["stairs"] : ["corridor"], 1, 1, 99)).ToList()
                    ?? [];
        var report = JsonSerializer.Serialize(new { problems, rooms = pack?.Rooms });
        return Task.FromResult(new PackInspection(problems, pack?.PackId, pack is null ? null : MapIdentity.LibraryVersion(pack.VmfSha256, c.LibraryJson),
            rooms, report, TimeSpan.FromMilliseconds(1)));
    }
}
