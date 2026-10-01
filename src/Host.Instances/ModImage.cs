using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SourceSharp.Host.Abstractions;

namespace SourceSharp.Host.Instances;

/// <summary>A registry could not answer for a mod image: the check fails closed (a missing input is a failure).</summary>
public sealed class ModImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads what a mod image says it is for (docs/mod-image.md: label org.sourcesharp.mod).</summary>
public interface IModImageInspector
{
    /// <summary>The image's org.sourcesharp.mod label, or null when it has none. Throws <see cref="ModImageException"/> when the registry cannot say.</summary>
    Task<string?> ModLabel(ImageRef image, CancellationToken ct = default);
}

/// <summary>
/// The OCI distribution API, anonymously: manifest (or image index → the linux/amd64
/// manifest) → config blob → Labels. Plain http for localhost registries and the
/// configured insecure ones; an anonymous bearer token when the registry asks for one.
/// </summary>
public sealed class OciModImageInspector(HttpClient http, IReadOnlyCollection<string> insecureRegistries) : IModImageInspector
{
    public const string Label = "org.sourcesharp.mod";

    static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    public async Task<string?> ModLabel(ImageRef image, CancellationToken ct = default)
    {
        var (baseUrl, repo) = Locate(image, insecureRegistries);
        var reference = string.IsNullOrEmpty(image.Digest) ? image.Tag : image.Digest;
        try
        {
            string? bearer = null;
            var manifest = await GetJson($"{baseUrl}/v2/{repo}/manifests/{reference}", true, b => bearer = b, () => bearer, ct);
            if (manifest.RootElement.TryGetProperty("manifests", out var list))
            {
                var chosen = list.EnumerateArray()
                    .OrderByDescending(m => m.TryGetProperty("platform", out var p)
                        && p.GetProperty("os").GetString() == "linux" && p.GetProperty("architecture").GetString() == "amd64")
                    .FirstOrDefault();
                if (chosen.ValueKind != JsonValueKind.Object)
                    throw new ModImageException($"{image}: image index lists no manifests");
                manifest = await GetJson($"{baseUrl}/v2/{repo}/manifests/{chosen.GetProperty("digest").GetString()}", true, b => bearer = b, () => bearer, ct);
            }
            if (!manifest.RootElement.TryGetProperty("config", out var config))
                throw new ModImageException($"{image}: manifest has no config");
            var configDigest = config.GetProperty("digest").GetString()!;

            var bytes = await GetBytes($"{baseUrl}/v2/{repo}/blobs/{configDigest}", false, b => bearer = b, () => bearer, ct);
            var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actual, configDigest, StringComparison.OrdinalIgnoreCase))
                throw new ModImageException($"{image}: config blob hashes to {actual}, manifest says {configDigest}");

            using var cfg = JsonDocument.Parse(bytes);
            return cfg.RootElement.TryGetProperty("config", out var c)
                && c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("Labels", out var labels)
                && labels.ValueKind == JsonValueKind.Object
                && labels.TryGetProperty(Label, out var v)
                ? v.GetString()
                : null;
        }
        catch (ModImageException) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
        {
            throw new ModImageException($"{image}: {e.Message}", e);
        }
    }

    /// <summary>The registry base URL and repository path for an image reference.</summary>
    public static (string BaseUrl, string Repository) Locate(ImageRef image, IReadOnlyCollection<string> insecure)
    {
        var registry = image.Registry;
        var repo = image.Name;
        if (string.IsNullOrEmpty(registry) || registry is "docker.io" or "index.docker.io")
        {
            registry = "registry-1.docker.io";
            if (!repo.Contains('/')) repo = "library/" + repo;
        }
        var host = registry.StartsWith('[') ? registry[..(registry.IndexOf(']') + 1)] : registry.Split(':')[0];
        var plain = host is "localhost" or "127.0.0.1" or "[::1]"
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || insecure.Contains(registry, StringComparer.OrdinalIgnoreCase)
            || insecure.Contains(host, StringComparer.OrdinalIgnoreCase);
        return ($"{(plain ? "http" : "https")}://{registry}", repo);
    }

    async Task<JsonDocument> GetJson(string url, bool manifest, Action<string> setBearer, Func<string?> bearer, CancellationToken ct) =>
        JsonDocument.Parse(await GetBytes(url, manifest, setBearer, bearer, ct));

    async Task<byte[]> GetBytes(string url, bool manifest, Action<string> setBearer, Func<string?> bearer, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (manifest)
                foreach (var t in ManifestTypes) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(t));
            if (bearer() is { } b) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", b);

            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0
                && resp.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) is { } challenge)
            {
                setBearer(await AnonymousToken(challenge.Parameter ?? "", ct));
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new ModImageException($"GET {url}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
    }

    async Task<string> AnonymousToken(string challenge, CancellationToken ct)
    {
        var p = challenge.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2)).Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1].Trim('"'), StringComparer.OrdinalIgnoreCase);
        if (!p.TryGetValue("realm", out var realm)) throw new ModImageException($"bearer challenge without a realm: {challenge}");
        var query = string.Join('&', new[] { "service", "scope" }.Where(p.ContainsKey).Select(k => $"{k}={Uri.EscapeDataString(p[k])}"));
        using var resp = await http.GetAsync(query.Length == 0 ? realm : $"{realm}?{query}", ct);
        if (!resp.IsSuccessStatusCode) throw new ModImageException($"token {realm}: {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct));
        return (doc.RootElement.TryGetProperty("token", out var t) ? t : doc.RootElement.GetProperty("access_token")).GetString()!;
    }
}

/// <summary>
/// docs/mod-image.md as a check over a mod image's file listing (`tar tf` of an exported
/// image, or `docker export | tar t`): the paths the contract requires, and the ones it forbids.
/// </summary>
public static class ModImageContract
{
    /// <summary>The contract's required paths, "/…" absolute; a trailing '/' is a directory that must hold something.</summary>
    public static IReadOnlyList<string> Required(string modName) =>
    [
        "/game/mods.yaml",
        "/game/bin/linux64/",
        "/game/bin/managed/",
        "/game/bin/dotnet/",
        $"/game/mods/{modName}/",
        "/bin/sh",
    ];

    /// <summary>One of these must exist: the init container runs `cp`.</summary>
    public static readonly IReadOnlyList<string> CopyTool = ["/bin/cp", "/usr/bin/cp"];

    /// <summary>What the image must not carry: the engine (it is the engine image's) and TF2 content.</summary>
    public static bool IsForbidden(string path)
    {
        var name = path.TrimEnd('/').Split('/')[^1];
        return name == "srcds_linux64" || (name.StartsWith("tf2_", StringComparison.Ordinal) && name.EndsWith(".vpk", StringComparison.Ordinal));
    }

    static string Normalize(string entry)
    {
        var e = entry.Trim();
        while (e.StartsWith("./", StringComparison.Ordinal)) e = e[2..];
        return "/" + e.TrimStart('/');
    }

    /// <summary>Every contract problem in a listing: "missing …" and "forbidden …" lines; empty = conforms.</summary>
    public static IReadOnlyList<string> Check(IEnumerable<string> listing, string modName)
    {
        var entries = listing.Where(l => l.Trim().Length > 0).Select(Normalize).ToList();
        var problems = new List<string>();
        foreach (var path in Required(modName))
        {
            var ok = path.EndsWith('/')
                ? entries.Any(e => e.StartsWith(path, StringComparison.Ordinal) && e.Length > path.Length)
                : entries.Contains(path);
            if (!ok) problems.Add($"missing {path}");
        }
        if (!CopyTool.Any(entries.Contains)) problems.Add($"missing {string.Join(" or ", CopyTool)}");
        problems.AddRange(entries.Where(IsForbidden).Select(e => $"forbidden {e}"));
        return problems;
    }
}

/// <summary>
/// The service's refusal (§H1d): a pod is created from a mod image only when the image's
/// label equals Mod.Name. Successful answers are cached per image reference for a minute.
/// </summary>
public sealed class ModImageGuard(IModImageInspector inspector, TimeProvider clock)
{
    readonly Dictionary<string, DateTimeOffset> _verified = [];
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary>Null when the image may be used, else why not.</summary>
    public async Task<string?> Refusal(ImageRef image, string modName, CancellationToken ct)
    {
        var key = image.ToString();
        if (_verified.TryGetValue(key, out var at) && clock.GetUtcNow() - at < CacheFor) return null;
        string? label;
        try { label = await inspector.ModLabel(image, ct); }
        catch (ModImageException e) { return $"mod image {key} could not be inspected: {e.Message}"; }
        if (label != modName)
            return label is null
                ? $"mod image {key} has no {OciModImageInspector.Label} label (want '{modName}')"
                : $"mod image {key} is labelled {OciModImageInspector.Label}='{label}', not '{modName}'";
        _verified[key] = clock.GetUtcNow();
        return null;
    }
}
