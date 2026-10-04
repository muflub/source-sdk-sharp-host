using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

// ============================================================================
// The §3.1 measurement table of docs/plans/plan_build_patch.md, reproducible.
//
//   make bench        prints the table, asserts the rulings, exits ≠ 0 on drift
//
// Fixture: fixtures/gen.{A,B,C}.cs — 200 classes × 3 static methods = 600
// MethodDefs (~30 KB dll). A is the base; B edits two method bodies in a way
// that inserts into the metadata heaps (T7.G gains an interpolated string,
// T42.F grows Math.Sqrt/Math.Abs calls → new TypeRef/MemberRef names + blobs);
// C edits two method bodies with NO heap insertion and NO body growth (T42 42→43
// is same-size IL; T7 "7"→"8" only appends a #US entry).
//
// The three rulings this harness keeps falsifiable:
//   R1 whole-file dictionary delta beats any per-stream/per-section split, even
//      though the split is "semantically correct" (segments diff total > 1.4×
//      the flat delta — the segmentation negative control).
//   R2 the 74 % byte divergence of A→B is the *insertion cascade*, not code
//      change: A→C, same file size and zero heap insertions, must produce zero
//      moved MethodDef RVAs, a zero-byte #~ diff, and a delta near the floor.
//   R3 Roslyn already dedups and end-appends the heaps: #Strings keeps a long
//      common prefix, so re-sorting heaps has no headroom (all heaps together
//      are a rounding error of the delta).
// A missing/stale/unbuildable fixture is a failure, never a skip.
// ============================================================================

string Dotnet = Environment.GetEnvironmentVariable("DOTNET")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
string Root = FindRepoRoot();
string Fixtures = Path.Combine(Root, "bench", "fixtures");
string Work = Path.Combine(Path.GetTempPath(), "sshost-bench", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(Work);
int failures = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}: {detail}");
    if (!ok) failures++;
}

try
{
    // ---------- 1. build the three fixture variants ----------
    Console.WriteLine("== fixture build ==");
    var dll = new Dictionary<string, byte[]>();
    foreach (var v in new[] { "A", "B", "C" })
    {
        var srcFile = $"gen.{v}.cs";
        if (!File.Exists(Path.Combine(Fixtures, srcFile))) Fail($"fixture source missing: {srcFile}");
        var outDir = Path.Combine(Work, v);
        var rc = Run(Root, Dotnet, "build",
            Path.Combine(Fixtures, "big.csproj"),
            "-c", "Release", "-v", "q", "--nologo", "-p:DebugType=none",
            $"-p:FixtureSource={srcFile}", $"-p:FixtureName={v}",
            $"-p:BaseIntermediateOutputPath={Path.Combine(Work, "obj", v)}/",
            $"-p:OutputPath={outDir}/");
        var dllPath = Path.Combine(outDir, "big.dll");
        if (rc != 0 || !File.Exists(dllPath)) Fail($"fixture {v} did not build (rc={rc})");
        dll[v] = File.ReadAllBytes(dllPath);
        Console.WriteLine($"  {v}: big.dll {dll[v].Length:N0} B  sha256 {Convert.ToHexString(SHA256.HashData(dll[v]))[..16]}");
    }

    // ---------- 2. determinism probe: same source, two output dirs ----------
    Console.WriteLine("== same-source reproducibility ==");
    {
        var outB2 = Path.Combine(Work, "A2");
        var rc = Run(Root, Dotnet, "build",
            Path.Combine(Fixtures, "big.csproj"),
            "-c", "Release", "-v", "q", "--nologo", "-p:DebugType=none",
            $"-p:FixtureSource=gen.A.cs", "-p:FixtureName=A",
            $"-p:BaseIntermediateOutputPath={Path.Combine(Work, "obj", "A2")}/",
            $"-p:OutputPath={outB2}/");
        if (rc != 0 || !File.Exists(Path.Combine(outB2, "big.dll"))) Fail("determinism arm did not build");
        var a2 = File.ReadAllBytes(Path.Combine(outB2, "big.dll"));
        int diff = DiffBytes(dll["A"], a2);
        Console.WriteLine($"  rebuild of A in a different dir: {diff} differing bytes");
        Check("R-determinism: Deterministic=true makes a rebuild byte-identical (the repo build; the sharp repo's 73-B drift is what §3.3 repack normalises)",
            diff == 0, $"{diff} bytes");
    }

    // ---------- 3. the table ----------
    Console.WriteLine();
    Console.WriteLine("== §3.1 table ==");
    var res = Table(dll["A"], dll["B"], "A→B (heap-inserting edit: 2 bodies, insertions in #Strings/#Blob/#US)");
    var resC = Table(dll["A"], dll["C"], "A→C (content-only control: same size, #US append only)");

    // ---------- 4. the rulings ----------
    Console.WriteLine();
    Console.WriteLine("== rulings ----------");
    Check("R2a  A→B is byte-wild (74 % baseline)", Between(res.DiffBytes * 100.0 / res.SizeB, 65, 82), $"{res.DiffBytes}/{res.SizeB} = {100.0 * res.DiffBytes / res.SizeB:F0} %");
    Check("R2b  A→B cascade: nearly every MethodDef RVA moved (96 % baseline)", Between(res.MovedRvas * 100.0 / res.TotalMethods, 85, 100), $"{res.MovedRvas}/{res.TotalMethods}");
    Check("R2c  A→B #~ diffs wholesale after a tiny prefix (28 B baseline)", res.Streams["#~"].Prefix < 200 && res.Streams["#~"].InPlaceDiff > res.Streams["#~"].SizeA / 2, $"prefix {res.Streams["#~"].Prefix}, diff {res.Streams["#~"].InPlaceDiff}/{res.Streams["#~"].SizeA}");
    Check("R2d  A→C control: zero moved RVAs, zero #~ diff (insertions, not code, are the cost)", resC.MovedRvas == 0 && resC.Streams["#~"].InPlaceDiff == 0, $"moved {resC.MovedRvas}, #~ diff {resC.Streams["#~"].InPlaceDiff}");
    Check("R2e  A→C flat delta near the floor (429 B baseline, ≤ 700)", resC.FlatDelta is > 0 and <= 700, $"{resC.FlatDelta} B");
    Check("R1a  A→B flat delta small (2,196 B baseline, 1.4–3.2 KB)", res.FlatDelta is >= 1400 and <= 3200, $"{res.FlatDelta} B vs {res.SizeB:N0} B file");
    Check("R1b  segmentation loses: per-stream+section split ≥ 1.4× the flat delta", res.SegTotal >= res.FlatDelta * 1.4, $"segmented {res.SegTotal} B vs flat {res.FlatDelta} B = {res.SegTotal / (double)res.FlatDelta:F2}×");
    Check("R3a  heaps already cheap: all five heaps' deltas ≪ #~+.text (133 B baseline)",
        res.HeapsDelta < res.FlatDelta / 8, $"heaps {res.HeapsDelta} B of {res.FlatDelta} B");
    Check("R3b  #Strings already end-appended: long common prefix (1289/1414 baseline)",
        res.Streams["#Strings"].Prefix > res.Streams["#Strings"].SizeA * 0.85, $"{res.Streams["#Strings"].Prefix}/{res.Streams["#Strings"].SizeA}");
    Check("R0   the delta beats whole-file compression of the same file", res.FlatDelta < res.WholeZstd && res.FlatDelta < res.WholeBrotli, $"delta {res.FlatDelta} B vs whole zstd {res.WholeZstd} B / brotli {res.WholeBrotli} B");


    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "ALL CHECKS PASS" : $"{failures} CHECK(S) FAILED — the §3.1 measured basis moved; revisit the ruling");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(Work, true); } catch { }
}

// ---------------------------------------------------------------------------

Result Table(byte[] A, byte[] B, string title)
{
    Console.WriteLine();
    Console.WriteLine($"--- {title}  ({A.Length:N0} B → {B.Length:N0} B)");
    Console.WriteLine($"{"stream",-10} {"sizeA",9} {"sizeB",9} {"prefix",8} {"suffix",8} {"bytes-diff",11} {"zstd-delta",11}");
    var sa = Streams(A); var sb = Streams(B);
    var streams = new Dictionary<string, (int SizeA, int Prefix, int InPlaceDiff, int Delta)>();
    long segTotal = 0, heapsDelta = 0;
    foreach (var (name, a) in sa)
    {
        if (!sb.TryGetValue(name, out var b)) throw new Exception($"stream {name} missing in B");
        var (pre, suf) = PrefixSuffix(a, b);
        int mid = Math.Min(a.Length, b.Length) - pre - suf;
        int diff = 0;
        for (int i = 0; i < mid; i++) if (a[pre + i] != b[pre + i]) diff++;
        int dlen = DictDelta(a, b);
        segTotal += dlen;
        if (name is "#Strings" or "#US" or "#GUID" or "#Blob") heapsDelta += dlen; // the four heaps, NOT #~
        streams[name] = (a.Length, pre, diff, dlen);
        Console.WriteLine($"{name,-10} {a.Length,9} {b.Length,9} {pre,8} {suf,8} {diff,11} {dlen,11}");
    }
    // PE sections + raw headers, each against its own base dictionary.
    using (var pea = new PEReader(new MemoryStream(A)))
    using (var peb = new PEReader(new MemoryStream(B)))
    {
        foreach (var n in new[] { ".text", ".reloc" })
        {
            var ta = Section(A, pea.PEHeaders, n); var tb = Section(B, peb.PEHeaders, n);
            if (ta.Length == 0) continue;
            int d = DictDelta(ta, tb); segTotal += d;
            var (pre, _) = PrefixSuffix(ta, tb);
            int diff = 0; for (int i = pre; i < Math.Min(ta.Length, tb.Length); i++) if (ta[i] != tb[i]) diff++;
            Console.WriteLine($"{n,-10} {ta.Length,9} {tb.Length,9} {pre,8} {"",8} {diff,11} {d,11}");
        }
        int hoff = pea.PEHeaders.SectionHeaders[0].PointerToRawData;
        int dh = DictDelta(A[..hoff], B[..hoff]); segTotal += dh;
        Console.WriteLine($"{"headers",-10} {hoff,9} {hoff,9} {"",8} {"",8} {"",11} {dh,11}");
    }
    int flat = DictDelta(A, B);
    Console.WriteLine($"FLAT whole-file delta: {flat} B   (segmented total incl. per-frame overhead: {segTotal} B, {segTotal / (double)flat:F2}×)");
    int moved = 0, total = 0;
    using (var pae = new PEReader(new MemoryStream(A)))
    using (var pbe = new PEReader(new MemoryStream(B)))
    {
        var ra = pae.GetMetadataReader(); var rb = pbe.GetMetadataReader();
        var ib = rb.MethodDefinitions.GetEnumerator();
        foreach (var h in ra.MethodDefinitions)
        {
            if (!ib.MoveNext()) break;
            total++;
            if (ra.GetMethodDefinition(h).RelativeVirtualAddress != rb.GetMethodDefinition(ib.Current).RelativeVirtualAddress) moved++;
        }
    }
    Console.WriteLine($"MethodDef bodies: {moved} of {total} moved RVAs ({(total == 0 ? 0 : 100.0 * moved / total):F0}%)");
    return new Result(B.Length, DiffBytes(A, B), streams, heapsDelta, segTotal, flat, moved, total, WholeZstd(B), WholeBrotli(B));
}

static (int, int) PrefixSuffix(byte[] a, byte[] b)
{
    int pre = 0; while (pre < Math.Min(a.Length, b.Length) && a[pre] == b[pre]) pre++;
    int suf = 0; while (suf < Math.Min(a.Length, b.Length) - pre && a[^ (suf + 1)] == b[^ (suf + 1)]) suf++;
    return (pre, suf);
}

static int DictDelta(byte[] basis, byte[] target)
{
    using var c = new Compressor(19);
    c.LoadDictionary(basis);
    return c.Wrap(target).ToArray().Length;
}

static int WholeZstd(byte[] f) { using var c = new Compressor(19); return c.Wrap(f).ToArray().Length; }
static int WholeBrotli(byte[] f)
{
    using var ms = new MemoryStream();
    using (var b = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, true)) b.Write(f);
    return (int)ms.Length;
}

static int DiffBytes(byte[] a, byte[] b)
{
    // "bytes that differ": over the common region, plus the size delta (a grown
    // region is unambiguously new content). cmp -l semantics extended to resize.
    int n = Math.Min(a.Length, b.Length);
    int d = Math.Abs(a.Length - b.Length);
    for (int i = 0; i < n; i++) if (a[i] != b[i]) d++;
    return d;
}

static byte[] Section(byte[] f, PEHeaders ph, string name)
{
    foreach (var sh in ph.SectionHeaders)
        if (sh.Name == name) return f.AsSpan(sh.PointerToRawData, Math.Max(sh.VirtualSize, sh.SizeOfRawData)).ToArray();
    return Array.Empty<byte>();
}

static Dictionary<string, byte[]> Streams(byte[] f)
{
    using var pe = new PEReader(new MemoryStream(f));
    var ph = pe.PEHeaders;
    int RvaToOff(int rva)
    {
        foreach (var s in ph.SectionHeaders)
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                return s.PointerToRawData + (rva - s.VirtualAddress);
        throw new Exception("bad rva");
    }
    int off = RvaToOff(ph.CorHeader!.MetadataDirectory.RelativeVirtualAddress);
    if (BitConverter.ToInt32(f, off) != 0x424A5342) throw new Exception("no BSJB");
    int verLen = BitConverter.ToInt32(f, off + 12);
    int p = off + 16 + ((verLen + 3) / 4) * 4;
    ushort nStreams = BitConverter.ToUInt16(f, p + 2);
    p += 4;
    var dic = new Dictionary<string, byte[]>();
    for (int i = 0; i < nStreams; i++)
    {
        int hdrStart = p;
        int so = BitConverter.ToInt32(f, p); int ss = BitConverter.ToInt32(f, p + 4);
        p += 8;
        int ne = p; while (f[ne] != 0) ne++;
        string name = Encoding.ASCII.GetString(f, p, ne - p);
        p = ne + 1;
        while ((p - hdrStart) % 4 != 0) p++;
        dic[name] = f.AsSpan(off + so, ss).ToArray();
    }
    if (dic.Count < 5) throw new Exception("metadata streams not found — stale/unreadable input is a failure");
    return dic;
}

static bool Between(double v, double lo, double hi) => v >= lo && v <= hi;

static int Run(string cwd, string file, params string[] args)
{
    var psi = new System.Diagnostics.ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = System.Diagnostics.Process.Start(psi)!;
    string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) Console.WriteLine(outp);
    return p.ExitCode;
}



static void Fail(string message) => throw new Exception(message);

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "Host.slnx"))) d = d.Parent;
    return d?.FullName ?? throw new Exception("bench harness must live inside the repo (Host.slnx not found)");
}

record Result(int SizeB, int DiffBytes, Dictionary<string, (int SizeA, int Prefix, int InPlaceDiff, int Delta)> Streams,
    long HeapsDelta, long SegTotal, int FlatDelta, int MovedRvas, int TotalMethods, int WholeZstd, int WholeBrotli);
