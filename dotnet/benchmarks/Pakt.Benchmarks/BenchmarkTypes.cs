using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Benchmarks;

// ---------------------------------------------------------------------------
// Model types
// ---------------------------------------------------------------------------

public class SmallDoc
{
    [PaktProperty("name")]
    public string Name { get; set; } = "";

    [PaktProperty("version")]
    public int Version { get; set; }

    [PaktProperty("debug")]
    public bool Debug { get; set; }

    [PaktProperty("rate")]
    [PaktScalar(PaktScalarType.Float)]
    public double Rate { get; set; }

    [PaktProperty("host")]
    public string Host { get; set; } = "";

    [PaktProperty("port")]
    public int Port { get; set; }

    [PaktProperty("max_retry")]
    public int MaxRetry { get; set; }

    [PaktProperty("timeout")]
    public int Timeout { get; set; }

    [PaktProperty("verbose")]
    public bool Verbose { get; set; }

    [PaktProperty("label")]
    public string Label { get; set; } = "";
}

public class FSEntry
{
    [PaktProperty("path")]
    public string Path { get; set; } = "";

    [PaktProperty("size")]
    public long Size { get; set; }

    [PaktProperty("mode")]
    public long Mode { get; set; }

    [PaktProperty("mod_time")]
    public string ModTime { get; set; } = "";

    [PaktProperty("is_dir")]
    public bool IsDir { get; set; }

    [PaktProperty("owner")]
    public string Owner { get; set; } = "";

    [PaktProperty("group")]
    public string Group { get; set; } = "";

    [PaktProperty("hash")]
    public string Hash { get; set; } = "";
}

public class FSDataset
{
    public string Root { get; set; } = "";
    public string Scanned { get; set; } = "";
    public List<FSEntry> Entries { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Source-generator contexts
// ---------------------------------------------------------------------------

[PaktSerializable(typeof(SmallDoc))]
[PaktSerializable(typeof(FSEntry))]
public partial class BenchmarkPaktContext : PaktSerializerContext { }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SmallDoc))]
[JsonSerializable(typeof(FSDataset))]
public partial class BenchmarkJsonContext : JsonSerializerContext { }

// ---------------------------------------------------------------------------
// Pre-computed benchmark data
// ---------------------------------------------------------------------------

public static class BenchmarkData
{
    // --- Small ---
    public static readonly SmallDoc SmallDocValue = new()
    {
        Name = "my-app",
        Version = 42,
        Debug = true,
        Rate = 3.14,
        Host = "localhost",
        Port = 8080,
        MaxRetry = 3,
        Timeout = 30,
        Verbose = false,
        Label = "production",
    };

    public static byte[] SmallPakt { get; private set; } = null!;
    public static byte[] SmallJson { get; private set; } = null!;

    // --- Wide (100 fields) ---
    public static byte[] WidePakt { get; private set; } = null!;
    public static byte[] WideJson { get; private set; } = null!;

    // --- Deep (10-level nesting) ---
    public static byte[] DeepPakt { get; private set; } = null!;
    public static byte[] DeepJson { get; private set; } = null!;
    public static PaktType DeepPaktType { get; private set; } = null!;
    public const int DeepLevels = 10;

    // --- LargeList (10K ints) ---
    public static byte[] ListPakt { get; private set; } = null!;
    public static byte[] ListJson { get; private set; } = null!;
    public const int ListSize = 10_000;

    // --- LargeMap (1K entries) ---
    public static byte[] MapPakt { get; private set; } = null!;
    public static byte[] MapJson { get; private set; } = null!;
    public const int MapSize = 1_000;

    // --- Shared PaktType constants ---
    public static readonly PaktType StrType = PaktType.Scalar(PaktScalarType.Str);
    public static readonly PaktType IntType = PaktType.Scalar(PaktScalarType.Int);
    public static readonly PaktType BoolType = PaktType.Scalar(PaktScalarType.Bool);

    // --- FS entry struct type (for PaktWriter) ---
    public static readonly PaktType FSEntryPaktType = PaktType.Struct(ImmutableArray.Create(
        new PaktField("path", PaktType.Scalar(PaktScalarType.Str)),
        new PaktField("size", PaktType.Scalar(PaktScalarType.Int)),
        new PaktField("mode", PaktType.Scalar(PaktScalarType.Int)),
        new PaktField("mod_time", PaktType.Scalar(PaktScalarType.Str)),
        new PaktField("is_dir", PaktType.Scalar(PaktScalarType.Bool)),
        new PaktField("owner", PaktType.Scalar(PaktScalarType.Str)),
        new PaktField("group", PaktType.Scalar(PaktScalarType.Str)),
        new PaktField("hash", PaktType.Scalar(PaktScalarType.Str))
    ));

    public static readonly PaktType FSEntryListPaktType = PaktType.List(FSEntryPaktType);

    static BenchmarkData()
    {
        InitSmall();
        InitWide();
        InitDeep();
        InitLargeList();
        InitLargeMap();
    }

    // ---------------------------------------------------------------------------
    // Small: source-gen round-trip
    // ---------------------------------------------------------------------------

    private static void InitSmall()
    {
        SmallPakt = PaktSerializer.Serialize(
            SmallDocValue, BenchmarkPaktContext.Default.SmallDoc, "doc");
        SmallJson = JsonSerializer.SerializeToUtf8Bytes(
            SmallDocValue, BenchmarkJsonContext.Default.SmallDoc);
    }

    // ---------------------------------------------------------------------------
    // Wide: 100 alternating str/int fields (multi-statement PAKT, JSON object)
    // ---------------------------------------------------------------------------

    private static void InitWide()
    {
        const int n = 100;
        var pb = new StringBuilder();
        var jb = new StringBuilder();
        jb.Append('{');

        for (int i = 1; i <= n; i++)
        {
            string name = $"field_{i:D3}";
            if (i > 1) jb.Append(',');

            if (i % 2 != 0)
            {
                string val = $"value_{i:D3}";
                pb.AppendLine($"{name}:str = '{val}'");
                jb.Append($"\"{name}\":\"{val}\"");
            }
            else
            {
                pb.AppendLine($"{name}:int = {i}");
                jb.Append($"\"{name}\":{i}");
            }
        }
        jb.Append('}');

        WidePakt = Encoding.UTF8.GetBytes(pb.ToString());
        WideJson = Encoding.UTF8.GetBytes(jb.ToString());
    }

    // ---------------------------------------------------------------------------
    // Deep: 10-level nested struct
    // ---------------------------------------------------------------------------

    private static void InitDeep()
    {
        DeepPaktType = BuildDeepType(DeepLevels);

        // PAKT: root:{name:str, child:{...}} = {'level_0', {'level_1', ...}}
        string typeStr = "{name:str}";
        string valStr = $"{{'level_{DeepLevels - 1}'}}";
        for (int i = DeepLevels - 2; i >= 0; i--)
        {
            typeStr = $"{{name:str, child:{typeStr}}}";
            valStr = $"{{'level_{i}', {valStr}}}";
        }
        DeepPakt = Encoding.UTF8.GetBytes($"root:{typeStr} = {valStr}\n");

        // JSON: {"name":"level_0","child":{"name":"level_1",...}}
        var jb = new StringBuilder();
        BuildDeepJson(jb, 0, DeepLevels);
        DeepJson = Encoding.UTF8.GetBytes($"{{\"root\":{jb}}}\n");
    }

    private static PaktType BuildDeepType(int depth)
    {
        var nameField = new PaktField("name", PaktType.Scalar(PaktScalarType.Str));
        if (depth <= 1)
            return PaktType.Struct(ImmutableArray.Create(nameField));
        var childType = BuildDeepType(depth - 1);
        return PaktType.Struct(ImmutableArray.Create(nameField, new PaktField("child", childType)));
    }

    private static void BuildDeepJson(StringBuilder sb, int level, int maxDepth)
    {
        sb.Append($"{{\"name\":\"level_{level}\"");
        if (level < maxDepth - 1)
        {
            sb.Append(",\"child\":");
            BuildDeepJson(sb, level + 1, maxDepth);
        }
        sb.Append('}');
    }

    // ---------------------------------------------------------------------------
    // LargeList: 10K integers
    // ---------------------------------------------------------------------------

    private static void InitLargeList()
    {
        var pb = new StringBuilder();
        pb.Append("numbers:[int] = [");
        for (int i = 1; i <= ListSize; i++)
        {
            if (i > 1) pb.Append(", ");
            pb.Append(i);
        }
        pb.Append("]\n");
        ListPakt = Encoding.UTF8.GetBytes(pb.ToString());

        // JSON: {"numbers":[1,2,...,10000]}
        var jb = new StringBuilder();
        jb.Append("{\"numbers\":[");
        for (int i = 1; i <= ListSize; i++)
        {
            if (i > 1) jb.Append(',');
            jb.Append(i);
        }
        jb.Append("]}");
        ListJson = Encoding.UTF8.GetBytes(jb.ToString());
    }

    // ---------------------------------------------------------------------------
    // LargeMap: 1K string→int entries
    // ---------------------------------------------------------------------------

    private static void InitLargeMap()
    {
        var pb = new StringBuilder();
        pb.Append("data:<str ; int> = <");
        for (int i = 1; i <= MapSize; i++)
        {
            if (i > 1) pb.Append(", ");
            pb.Append($"'key_{i:D4}' ; {i}");
        }
        pb.Append(">\n");
        MapPakt = Encoding.UTF8.GetBytes(pb.ToString());

        // JSON: {"data":{"key_0001":1,...}}
        var jb = new StringBuilder();
        jb.Append("{\"data\":{");
        for (int i = 1; i <= MapSize; i++)
        {
            if (i > 1) jb.Append(',');
            jb.Append($"\"key_{i:D4}\":{i}");
        }
        jb.Append("}}");
        MapJson = Encoding.UTF8.GetBytes(jb.ToString());
    }

    // ---------------------------------------------------------------------------
    // FS: filesystem metadata with pack syntax
    // ---------------------------------------------------------------------------

    public static (byte[] Pakt, byte[] Json, FSDataset Dataset) GenerateFS(int n)
    {
        var rng = new Random(42);

        string[] extensions = [".csv", ".parquet", ".json", ".log", ".tmp", ".idx"];
        string[] subdirs = ["incoming", "archive", "staging", "reports", "temp", "indexes"];
        long[] fileModes = [0b_1000_0001_1010_0100, 0b_1000_0001_1000_0000, 0b_1000_0000_1010_0100]; // 33188, 33152, 32932
        string[] owners = ["etl", "root", "app", "backup", "deploy"];
        string[] groups = ["data", "root", "apps", "ops"];

        var entries = new List<FSEntry>(n);
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
            string hash = "";

            if (isDir)
            {
                parts.Add(subdirs[rng.Next(subdirs.Length)]);
                path = string.Join("/", parts) + "/";
                mode = 0b_0100_0001_1110_1101; // 16877 (directory)
            }
            else
            {
                string name = $"file_{i:D5}{extensions[rng.Next(extensions.Length)]}";
                parts.Add(name);
                path = string.Join("/", parts);
                size = rng.Next(1024, 100 * 1024 * 1024);
                mode = fileModes[rng.Next(fileModes.Length)];
                hash = $"{i:x8}";
            }

            int dayOffset = rng.Next(151);
            int hourOffset = rng.Next(24);
            var modTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddDays(dayOffset).AddHours(hourOffset);

            entries.Add(new FSEntry
            {
                Path = path,
                Size = size,
                Mode = mode,
                ModTime = modTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                IsDir = isDir,
                Owner = owners[i % owners.Length],
                Group = groups[i % groups.Length],
                Hash = hash,
            });
        }

        var dataset = new FSDataset
        {
            Root = "/data/warehouse",
            Scanned = "2026-06-01T14:30:00Z",
            Entries = entries,
        };

        // Build PAKT bytes using pack syntax (<<)
        var pb = new StringBuilder();
        pb.AppendLine("root:str = '/data/warehouse'");
        pb.AppendLine("scanned:str = '2026-06-01T14:30:00Z'");
        pb.Append("entries:[{path:str, size:int, mode:int, mod_time:str, is_dir:bool, owner:str, group:str, hash:str}] <<");
        pb.AppendLine();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) pb.AppendLine();
            var e = entries[i];
            pb.Append($"    {{ '{e.Path}', {e.Size}, {e.Mode}, '{e.ModTime}', {(e.IsDir ? "true" : "false")}, '{e.Owner}', '{e.Group}', '{e.Hash}' }}");
        }
        pb.AppendLine();

        var paktBytes = Encoding.UTF8.GetBytes(pb.ToString());
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dataset, BenchmarkJsonContext.Default.FSDataset);
        return (paktBytes, jsonBytes, dataset);
    }

    // ---------------------------------------------------------------------------
    // Encode helpers: write deep nested struct with PaktWriter
    // ---------------------------------------------------------------------------

    public static void WriteDeepPaktValue(PaktWriter writer, int level, int maxDepth)
    {
        writer.WriteStructStart();
        writer.WriteStringValue($"level_{level}");
        if (level < maxDepth - 1)
            WriteDeepPaktValue(writer, level + 1, maxDepth);
        writer.WriteStructEnd();
    }

    public static void WriteDeepJsonValue(System.Text.Json.Utf8JsonWriter writer, int level, int maxDepth)
    {
        writer.WriteStartObject();
        writer.WriteString("name", $"level_{level}");
        if (level < maxDepth - 1)
        {
            writer.WritePropertyName("child");
            WriteDeepJsonValue(writer, level + 1, maxDepth);
        }
        writer.WriteEndObject();
    }
}

// ---------------------------------------------------------------------------
// Async enumeration helper (PaktStreamReader yields IAsyncEnumerable via ReadPackElements)
// ---------------------------------------------------------------------------

internal static class AsyncHelper
{
    internal static List<T> ToListSync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        var enumerator = source.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                list.Add(enumerator.Current);
        }
        finally
        {
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }
        return list;
    }

    internal static int CountSync<T>(IAsyncEnumerable<T> source)
    {
        int count = 0;
        var enumerator = source.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().GetAwaiter().GetResult())
                count++;
        }
        finally
        {
            enumerator.DisposeAsync().GetAwaiter().GetResult();
        }
        return count;
    }
}
