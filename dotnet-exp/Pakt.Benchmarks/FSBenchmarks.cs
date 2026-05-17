using System.Globalization;
using System.Text;
using System.Text.Json;

using BenchmarkDotNet.Attributes;

namespace Pakt.Benchmarks;

/// <summary>
/// Equivalent to dotnet/'s FSBenchmarks — 8-field struct pack.
/// Same Random(42) seed, same data shape, v0.1a syntax (layout separation, no commas).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FSBenchmarks
{
    [Params(1_000, 10_000)]
    public int EntryCount { get; set; }

    private byte[] _paktBytes = [];
    private byte[] _jsonBytes = [];

    [GlobalSetup]
    public void Setup()
    {
        (_paktBytes, _jsonBytes) = GenerateData(EntryCount);
    }

    [Benchmark(Description = "PAKT FS decode")]
    public int PaktDecode()
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(_paktBytes);
        var reader = new PaktReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [Benchmark(Baseline = true, Description = "JSON FS decode")]
    public int JsonDecode()
    {
        Utf8JsonReader jsonReader = new(_jsonBytes);
        int count = 0;
        while (jsonReader.Read())
            count++;
        return count;
    }

    private static (byte[] Pakt, byte[] Json) GenerateData(int n)
    {
        var rng = new Random(42);

        string[] extensions = [".csv", ".parquet", ".json", ".log", ".tmp", ".idx"];
        string[] subdirs = ["incoming", "archive", "staging", "reports", "temp", "indexes"];
        long[] fileModes = [0b_1000_0001_1010_0100, 0b_1000_0001_1000_0000, 0b_1000_0000_1010_0100];
        string[] owners = ["etl", "root", "app", "backup", "deploy"];
        string[] groups = ["data", "root", "apps", "ops"];

        var pb = new StringBuilder();
        var jb = new StringBuilder();

        pb.AppendLine("root:str = '/data/warehouse'");
        pb.AppendLine("scanned:ts = 2026-06-01T14:30:00Z");
        pb.AppendLine("entries:[{path:str size:int mode:int mod_time:ts is_dir:bool owner:str group:str hash:bin}] = ~[");

        jb.Append("{\"root\":\"/data/warehouse\",\"scanned\":\"2026-06-01T14:30:00Z\",\"entries\":[");

        for (int i = 0; i < n; i++)
        {
            AppendEntry(rng, i, n, extensions, subdirs, fileModes, owners, groups, pb, jb);
        }

        jb.Append("]}");

        return (Encoding.UTF8.GetBytes(pb.ToString()), Encoding.UTF8.GetBytes(jb.ToString()));
    }

    private static void AppendEntry(
        Random rng, int i, int n,
        string[] extensions, string[] subdirs, long[] fileModes,
        string[] owners, string[] groups,
        StringBuilder pb, StringBuilder jb)
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

        if (i > 0) jb.Append(',');
        jb.Append(CultureInfo.InvariantCulture,
            $"{{\"path\":\"{path}\",\"size\":{size},\"mode\":{mode},\"mod_time\":\"{modTimeStr}\",\"is_dir\":{(isDir ? "true" : "false")},\"owner\":\"{owner}\",\"group\":\"{group}\",\"hash\":\"{hashHex}\"}}");
    }
}