using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace Pakt.Benchmarks;

// ── Model types ──

/// <summary>
/// Identical to dotnet/ SmallDoc: 10 mixed scalar fields.
/// </summary>
public class BenchSmallDoc
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public bool Debug { get; set; }
    public double Rate { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public int MaxRetry { get; set; }
    public int Timeout { get; set; }
    public bool Verbose { get; set; }
    public string Label { get; set; } = "";
}

// ── Source gen contexts ──

[PaktSerializable(typeof(BenchSmallDoc))]
public partial class BenchDeserContext : PaktSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BenchSmallDoc))]
public partial class BenchJsonContext : JsonSerializerContext;

/// <summary>
/// Cross-comparison: dotnet-exp deserialization pipeline performance.
/// Measures the full path from bytes → CLR objects, comparable to dotnet/ benchmarks.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DeserializeBenchmarks
{
    // ── Small doc ──
    private byte[] _smallPakt = [];
    private byte[] _smallJson = [];

    // ── FS pack ──
    private byte[] _fsPakt = [];
    private byte[] _fsJson = [];

    [Params(1_000, 10_000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _smallPakt = Encoding.UTF8.GetBytes(
            "name:str = 'my-app'\nversion:int = 42\ndebug:bool = true\nrate:float = 3.14e0\nhost:str = 'localhost'\nport:int = 8080\nmax-retry:int = 3\ntimeout:int = 30\nverbose:bool = false\nlabel:str = 'production'");
        _smallJson = Encoding.UTF8.GetBytes(
            """{"name":"my-app","version":42,"debug":true,"rate":3.14,"host":"localhost","port":8080,"max_retry":3,"timeout":30,"verbose":false,"label":"production"}""");
        _fsPakt = GenerateFSPakt(EntryCount);
        _fsJson = GenerateFSJson(EntryCount);
    }

    // ═══════════════════ Small: unit deserialization ═══════════════════

    [BenchmarkCategory("Small"), Benchmark(Baseline = true, Description = "PAKT unit deser")]
    public BenchSmallDoc PaktSmallDeserialize()
    {
        return PaktUnitDeserializer.Deserialize<BenchSmallDoc>(
            _smallPakt, BenchDeserContext.Default);
    }

    [BenchmarkCategory("Small"), Benchmark(Description = "JSON deser")]
    public BenchSmallDoc? JsonSmallDeserialize()
    {
        return JsonSerializer.Deserialize(_smallJson, BenchJsonContext.Default.BenchSmallDoc);
    }

    // ═══════════════════ FS: tokenize only (no materialization) ═══════════════════

    [BenchmarkCategory("FS-Decode"), Benchmark(Baseline = true, Description = "PAKT raw decode")]
    public int PaktFSDecode()
    {
        var seq = new ReadOnlySequence<byte>(_fsPakt);
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [BenchmarkCategory("FS-Decode"), Benchmark(Description = "PAKT validating decode")]
    public int PaktFSValidatingDecode()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_fsPakt);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // ═══════════════════ FS: materialize scalars ═══════════════════

    [BenchmarkCategory("FS-Materialize"), Benchmark(Baseline = true, Description = "PAKT materialize")]
    public int PaktFSMaterialize()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_fsPakt);
        int count = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case PaktTokenType.String:
                    _ = reader.GetString();
                    count++;
                    break;
                case PaktTokenType.Int:
                    _ = reader.GetInt64();
                    count++;
                    break;
                case PaktTokenType.Bool:
                    _ = reader.GetBool();
                    count++;
                    break;
                case PaktTokenType.Timestamp:
                    _ = reader.GetTimestamp();
                    count++;
                    break;
                case PaktTokenType.Binary:
                    _ = reader.GetBytes();
                    count++;
                    break;
            }
        }
        return count;
    }

    [BenchmarkCategory("FS-Materialize"), Benchmark(Description = "JSON materialize")]
    public int JsonFSMaterialize()
    {
        Utf8JsonReader reader = new(_fsJson);
        int count = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    _ = reader.GetString();
                    count++;
                    break;
                case JsonTokenType.Number:
                    _ = reader.GetInt64();
                    count++;
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    _ = reader.GetBoolean();
                    count++;
                    break;
            }
        }
        return count;
    }

    // ── Data generation ──

    private static byte[] GenerateFSPakt(int n)
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

    private static byte[] GenerateFSJson(int n)
    {
        var rng = new Random(42);
        string[] extensions = [".csv", ".parquet", ".json", ".log", ".tmp", ".idx"];
        string[] subdirs = ["incoming", "archive", "staging", "reports", "temp", "indexes"];
        long[] fileModes = [0b_1000_0001_1010_0100, 0b_1000_0001_1000_0000, 0b_1000_0000_1010_0100];
        string[] owners = ["etl", "root", "app", "backup", "deploy"];
        string[] groups = ["data", "root", "apps", "ops"];

        var jb = new StringBuilder();
        jb.Append("{\"root\":\"/data/warehouse\",\"scanned\":\"2026-06-01T14:30:00Z\",\"entries\":[");

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

            if (i > 0) jb.Append(',');
            jb.Append(CultureInfo.InvariantCulture,
                $"{{\"path\":\"{path}\",\"size\":{size},\"mode\":{mode},\"mod_time\":\"{modTimeStr}\",\"is_dir\":{(isDir ? "true" : "false")},\"owner\":\"{owner}\",\"group\":\"{group}\",\"hash\":\"{hashHex}\"}}");
        }

        jb.Append("]}");
        return Encoding.UTF8.GetBytes(jb.ToString());
    }
}