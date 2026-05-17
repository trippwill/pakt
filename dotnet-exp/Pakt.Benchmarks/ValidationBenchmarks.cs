using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

namespace Pakt.Benchmarks;

/// <summary>
/// Measures the overhead of <see cref="PaktValidatingReader"/> vs raw
/// <see cref="PaktReader"/>. Each pair reads identical data —
/// the only difference is type-annotation enforcement.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ValidationBenchmarks
{
    private byte[] _scalarBytes = [];
    private byte[] _nestedBytes = [];
    private byte[] _fsBytes = [];

    [GlobalSetup]
    public void Setup()
    {
        _scalarBytes = GenerateScalarStatements();
        _nestedBytes = GenerateNestedDocument();
        _fsBytes = GenerateFSPack(1_000);
    }

    // ── Scalars: 100 typed scalar statements ────────────────────────

    [Benchmark(Baseline = true, Description = "Raw  — 100 scalars")]
    public int RawScalars()
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(_scalarBytes);
        var reader = new PaktReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark(Description = "Valid — 100 scalars")]
    public int ValidatingScalars()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_scalarBytes);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // ── Nested: 3-level struct ──────────────────────────────────────

    [Benchmark(Description = "Raw  — nested struct")]
    public int RawNested()
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(_nestedBytes);
        var reader = new PaktReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark(Description = "Valid — nested struct")]
    public int ValidatingNested()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_nestedBytes);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // ── FS Pack: 1K struct elements ─────────────────────────────────

    [Benchmark(Description = "Raw  — FS 1K pack")]
    public int RawFS()
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(_fsBytes);
        var reader = new PaktReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark(Description = "Valid — FS 1K pack")]
    public int ValidatingFS()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_fsBytes);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // ── Data generators ─────────────────────────────────────────────

    private static byte[] GenerateScalarStatements()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 25; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"s_{i:D3}:str = 'value_{i}'");
            sb.AppendLine(CultureInfo.InvariantCulture, $"i_{i:D3}:int = {i * 17}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"b_{i:D3}:bool = {(i % 2 == 0 ? "true" : "false")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"d_{i:D3}:dec = {i}.{i * 3:D2}");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] GenerateNestedDocument()
    {
        return Encoding.UTF8.GetBytes(
            "config:{server:{host:str port:int tls:{enabled:bool cert:str key:str}} db:{host:str port:int name:str pool:{min:int max:int timeout:int}} cache:{host:str port:int ttl:int}} = { { 'api.example.com' 443 { true '/etc/ssl/cert.pem' '/etc/ssl/key.pem' } } { 'db.internal' 5432 'myapp' { 5 50 30 } } { 'cache.internal' 6379 3600 } }");
    }

    private static byte[] GenerateFSPack(int n)
    {
        var rng = new Random(42);
        string[] extensions = [".csv", ".parquet", ".json", ".log", ".tmp", ".idx"];
        string[] subdirs = ["incoming", "archive", "staging", "reports", "temp", "indexes"];
        long[] fileModes = [0b_1000_0001_1010_0100, 0b_1000_0001_1000_0000, 0b_1000_0000_1010_0100];
        string[] owners = ["etl", "root", "app", "backup", "deploy"];
        string[] groups = ["data", "root", "apps", "ops"];

        var pb = new StringBuilder();
        pb.AppendLine("root:str = '/data/warehouse'");
        pb.AppendLine("scanned:ts = 2026-06-01T14:30:00Z");
        pb.AppendLine("entries:[{path:str size:int mode:int mod_time:ts is_dir:bool owner:str group:str hash:bin}] = ~[");

        for (int i = 0; i < n; i++)
        {
            bool isDir = rng.NextDouble() < 0.15;
            int depth = rng.Next(4) + 1;
            var parts = new List<string>(depth + 1) { "/data/warehouse" };
            for (int d = 0; d < depth - 1; d++)
                parts.Add(subdirs[rng.Next(subdirs.Length)]);

            string path;
            long size = 0;
            long mode;
            string hashHex = "";

            if (isDir)
            {
                parts.Add(subdirs[rng.Next(subdirs.Length)]);
                path = string.Join("/", parts) + "/";
                mode = 0b_0100_0001_1110_1101;
            }
            else
            {
                string name = $"file_{i:D5}{extensions[rng.Next(extensions.Length)]}";
                parts.Add(name);
                path = string.Join("/", parts);
                size = rng.Next(1024, 100 * 1024 * 1024);
                mode = fileModes[rng.Next(fileModes.Length)];
                hashHex = $"{i:x8}";
            }

            int dayOffset = rng.Next(151);
            int hourOffset = rng.Next(24);
            var modTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddDays(dayOffset).AddHours(hourOffset);
            string modTimeStr = modTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            string owner = owners[i % owners.Length];
            string group = groups[i % groups.Length];

            pb.AppendLine(CultureInfo.InvariantCulture,
                $"    {{ '{path}' {size} {mode} {modTimeStr} {(isDir ? "true" : "false")} '{owner}' '{group}' x'{hashHex}' }}");
        }

        return Encoding.UTF8.GetBytes(pb.ToString());
    }
}