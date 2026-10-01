using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceSharp.Host.Instances.Tests;

/// <summary>
/// A tiny OCI registry over HttpListener on 127.0.0.1: serves manifests by tag or digest and
/// blobs by digest, optionally behind an anonymous bearer challenge. Records every request path.
/// </summary>
sealed class FakeRegistry : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly Dictionary<string, (string Type, byte[] Body)> _manifests = [];
    readonly Dictionary<string, byte[]> _blobs = [];
    readonly Task _loop;
    public List<string> Requests { get; } = [];
    public bool RequireToken { get; set; }
    public const string Token = "anon-token";
    public string Host { get; }

    public FakeRegistry()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Host = $"127.0.0.1:{port}";
        _listener.Prefixes.Add($"http://{Host}/");
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    public static string Digest(byte[] b) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Pushes an image whose config carries these labels; returns the manifest digest.</summary>
    public string Push(string repo, string tag, IDictionary<string, string>? labels, bool asIndex = false, byte[]? corruptConfig = null)
    {
        var config = JsonSerializer.SerializeToUtf8Bytes(new { architecture = "amd64", os = "linux", config = new { Labels = labels } });
        var configDigest = Digest(config);
        _blobs[configDigest] = corruptConfig ?? config;
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.oci.image.manifest.v1+json",
            config = new { mediaType = "application/vnd.oci.image.config.v1+json", digest = configDigest, size = config.Length },
            layers = Array.Empty<object>(),
        });
        var md = Digest(manifest);
        _manifests[$"{repo}@{md}"] = ("application/vnd.oci.image.manifest.v1+json", manifest);
        if (!asIndex)
        {
            _manifests[$"{repo}:{tag}"] = ("application/vnd.oci.image.manifest.v1+json", manifest);
            return md;
        }
        // an index whose first entry is another platform, so choosing linux/amd64 is observable
        var armManifest = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, config = new { digest = "sha256:" + new string('0', 64) } });
        var armDigest = Digest(armManifest);
        _manifests[$"{repo}@{armDigest}"] = ("application/vnd.oci.image.manifest.v1+json", armManifest);
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.oci.image.index.v1+json",
            manifests = new object[]
            {
                new { digest = armDigest, platform = new { os = "linux", architecture = "arm64" } },
                new { digest = md, platform = new { os = "linux", architecture = "amd64" } },
            },
        });
        _manifests[$"{repo}:{tag}"] = ("application/vnd.oci.image.index.v1+json", index);
        return Digest(index);
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            lock (Requests) Requests.Add(path);
            var resp = ctx.Response;
            try
            {
                if (path == "/token")
                {
                    Write(resp, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { token = Token }));
                    continue;
                }
                if (RequireToken && ctx.Request.Headers["Authorization"] != $"Bearer {Token}")
                {
                    resp.AddHeader("WWW-Authenticate", $"Bearer realm=\"http://{Host}/token\",service=\"fake\",scope=\"repository:x:pull\"");
                    Write(resp, 401, "text/plain", "auth"u8.ToArray());
                    continue;
                }
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // v2 / repo... / manifests|blobs / ref
                var kind = parts[^2];
                var reference = parts[^1];
                var repo = string.Join('/', parts[1..^2]);
                if (kind == "manifests")
                {
                    var key = reference.StartsWith("sha256:") ? $"{repo}@{reference}" : $"{repo}:{reference}";
                    if (_manifests.TryGetValue(key, out var m)) Write(resp, 200, m.Type, m.Body);
                    else Write(resp, 404, "text/plain", "no manifest"u8.ToArray());
                }
                else if (kind == "blobs" && _blobs.TryGetValue(reference, out var blob)) Write(resp, 200, "application/octet-stream", blob);
                else Write(resp, 404, "text/plain", "no blob"u8.ToArray());
            }
            catch { resp.Abort(); }
        }
    }

    static void Write(HttpListenerResponse resp, int status, string type, byte[] body)
    {
        resp.StatusCode = status;
        resp.ContentType = type;
        resp.ContentLength64 = body.Length;
        resp.OutputStream.Write(body);
        resp.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try { await _loop; } catch { }
    }
}
