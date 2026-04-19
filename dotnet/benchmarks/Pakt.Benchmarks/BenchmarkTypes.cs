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
    [PaktScalar(PaktScalarType.Ts)]
    public DateTimeOffset ModTime { get; set; }

    [PaktProperty("is_dir")]
    public bool IsDir { get; set; }

    [PaktProperty("owner")]
    public string Owner { get; set; } = "";

    [PaktProperty("group")]
    public string Group { get; set; } = "";

    [PaktProperty("hash")]
    public byte[] Hash { get; set; } = [];
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
[PaktSerializable(typeof(FinTrade))]
[PaktSerializable(typeof(FinPosition))]
public partial class BenchmarkPaktContext : PaktSerializerContext { }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SmallDoc))]
[JsonSerializable(typeof(FSDataset))]
[JsonSerializable(typeof(FinDataset))]
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
        new PaktField("mod_time", PaktType.Scalar(PaktScalarType.Ts)),
        new PaktField("is_dir", PaktType.Scalar(PaktScalarType.Bool)),
        new PaktField("owner", PaktType.Scalar(PaktScalarType.Str)),
        new PaktField("group", PaktType.Scalar(PaktScalarType.Str)),
        new PaktField("hash", PaktType.Scalar(PaktScalarType.Bin))
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
        SmallPakt = PaktSerializer.Serialize<SmallDoc>(
            SmallDocValue, BenchmarkPaktContext.Default, "doc");
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
            byte[] hash = [];

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
                hash = Convert.FromHexString($"{i:x8}");
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
                ModTime = modTime,
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
        pb.AppendLine("scanned:ts = 2026-06-01T14:30:00Z");
        pb.Append("entries:[{path:str, size:int, mode:int, mod_time:ts, is_dir:bool, owner:str, group:str, hash:bin}] <<");
        pb.AppendLine();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) pb.AppendLine();
            var e = entries[i];
            var hashHex = Convert.ToHexString(e.Hash).ToLowerInvariant();
            pb.Append($"    {{ '{e.Path}', {e.Size}, {e.Mode}, {e.ModTime:yyyy-MM-ddTHH:mm:ssZ}, {(e.IsDir ? "true" : "false")}, '{e.Owner}', '{e.Group}', x'{hashHex}' }}");
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

    // ---------------------------------------------------------------------------
    // Financial: trade execution log with positions map pack
    // ---------------------------------------------------------------------------

    public static (byte[] Pakt, byte[] Json, FinDataset Dataset) GenerateFin(int n)
    {
        var rng = new Random(77);

        string[] tickers = ["AAPL", "GOOG", "MSFT", "AMZN", "NVDA", "META", "TSLA", "JPM", "V", "UNH",
            "XOM", "JNJ", "PG", "MA", "HD", "CVX", "MRK", "ABBV", "PEP", "KO"];
        string[] venues = ["NYSE", "NASDAQ", "BATS", "IEX", "EDGX", "MEMX"];
        string[] tagPool = ["algo", "manual", "dark-pool", "pre-market", "post-market", "block", "sweep", "iceberg"];

        var baseTime = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.FromHours(-5));

        var trades = new List<FinTrade>(n);
        for (int i = 0; i < n; i++)
        {
            var ticker = tickers[rng.Next(tickers.Length)];
            var side = rng.NextDouble() < 0.45 ? "sell" : "buy";
            var qty = (long)(rng.Next(9900) + 100);
            var priceDollars = rng.Next(400) + 10;
            var priceCents = rng.Next(100);
            var price = priceDollars + priceCents / 100m;
            var feesCents = rng.Next(500) + 1;
            var fees = feesCents / 100m;
            var filled = rng.NextDouble() < 0.92;
            var venue = venues[rng.Next(venues.Length)];
            var orderId = Guid.NewGuid(); // deterministic enough for bench data shape
            // Consume the same RNG state as the string-based generator for seed compatibility
            _ = rng.Next(); _ = rng.Next(); _ = rng.Next(); _ = rng.Next(); _ = rng.NextInt64();

            var numTags = rng.Next(3) + 1;
            var tags = new List<string>(numTags);
            for (int j = 0; j < numTags; j++)
                tags.Add(tagPool[rng.Next(tagPool.Length)]);

            var ts = baseTime.AddSeconds(i * 3 + rng.Next(3));

            trades.Add(new FinTrade
            {
                Timestamp = ts,
                Ticker = ticker,
                Side = side,
                Quantity = qty,
                Price = price,
                Fees = fees,
                Filled = filled,
                Venue = venue,
                OrderId = orderId,
                Tags = tags,
            });
        }

        var positions = new Dictionary<string, FinPosition>();
        foreach (var ticker in tickers)
        {
            var priceDollars = rng.Next(400) + 10;
            var priceCents = rng.Next(100);
            var costDollars = rng.Next(400) + 10;
            var costCents = rng.Next(100);
            var pnl = (priceDollars - costDollars) * (rng.Next(5000) + 100);

            positions[ticker] = new FinPosition
            {
                Qty = (long)(rng.Next(50000) + 100),
                AvgCost = costDollars + costCents / 100m,
                UnrealizedPnl = pnl + rng.Next(100) / 100m,
                LastPrice = priceDollars + priceCents / 100m,
                Updated = baseTime.AddSeconds(n * 3),
            };
        }

        var asOf = baseTime.AddSeconds(n * 3);
        var dataset = new FinDataset
        {
            Account = "ACCT-7734-PRIME",
            AsOf = asOf.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Trades = trades,
            Positions = positions,
        };

        // Build PAKT
        var pb = new StringBuilder();
        pb.AppendLine("account:str = 'ACCT-7734-PRIME'");
        pb.AppendLine($"as_of:ts = {asOf:yyyy-MM-ddTHH:mm:sszzz}");

        pb.Append("trades:[{timestamp:ts, ticker:str, side:|buy, sell|, quantity:int, price:dec, fees:dec, filled:bool, venue:str, order_id:uuid, tags:[str]}] <<");
        pb.AppendLine();
        for (int i = 0; i < trades.Count; i++)
        {
            if (i > 0) pb.AppendLine();
            var t = trades[i];
            var boolStr = t.Filled ? "true" : "false";
            var tagStr = string.Join(", ", t.Tags.Select(tag => $"'{tag}'"));
            pb.Append($"    {{ {t.Timestamp:yyyy-MM-ddTHH:mm:sszzz}, '{t.Ticker}', |{t.Side}, {t.Quantity}, {t.Price:0.00}, {t.Fees:0.00}, {boolStr}, '{t.Venue}', {t.OrderId}, [{tagStr}] }}");
        }
        pb.AppendLine();

        pb.Append("positions:<str ; {qty:int, avg_cost:dec, unrealized_pnl:dec, last_price:dec, updated:ts}> <<");
        pb.AppendLine();
        var first = true;
        foreach (var (ticker, pos) in positions)
        {
            if (!first) pb.AppendLine();
            first = false;
            pb.Append($"    '{ticker}' ; {{ {pos.Qty}, {pos.AvgCost:0.00}, {pos.UnrealizedPnl:0.00}, {pos.LastPrice:0.00}, {pos.Updated:yyyy-MM-ddTHH:mm:sszzz} }}");
        }
        pb.AppendLine();

        var paktBytes = Encoding.UTF8.GetBytes(pb.ToString());
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dataset, BenchmarkJsonContext.Default.FinDataset);
        return (paktBytes, jsonBytes, dataset);
    }
}

// ---------------------------------------------------------------------------
// Financial model types
// ---------------------------------------------------------------------------

public class FinTrade
{
    [PaktProperty("timestamp")]
    [PaktScalar(PaktScalarType.Ts)]
    public DateTimeOffset Timestamp { get; set; }

    [PaktProperty("ticker")]
    public string Ticker { get; set; } = "";

    [PaktProperty("side")]
    public string Side { get; set; } = "";

    [PaktProperty("quantity")]
    public long Quantity { get; set; }

    [PaktProperty("price")]
    [PaktScalar(PaktScalarType.Dec)]
    public decimal Price { get; set; }

    [PaktProperty("fees")]
    [PaktScalar(PaktScalarType.Dec)]
    public decimal Fees { get; set; }

    [PaktProperty("filled")]
    public bool Filled { get; set; }

    [PaktProperty("venue")]
    public string Venue { get; set; } = "";

    [PaktProperty("order_id")]
    [PaktScalar(PaktScalarType.Uuid)]
    public Guid OrderId { get; set; }

    [PaktProperty("tags")]
    public List<string> Tags { get; set; } = [];
}

public class FinPosition
{
    [PaktProperty("qty")]
    public long Qty { get; set; }

    [PaktProperty("avg_cost")]
    [PaktScalar(PaktScalarType.Dec)]
    public decimal AvgCost { get; set; }

    [PaktProperty("unrealized_pnl")]
    [PaktScalar(PaktScalarType.Dec)]
    public decimal UnrealizedPnl { get; set; }

    [PaktProperty("last_price")]
    [PaktScalar(PaktScalarType.Dec)]
    public decimal LastPrice { get; set; }

    [PaktProperty("updated")]
    [PaktScalar(PaktScalarType.Ts)]
    public DateTimeOffset Updated { get; set; }
}

public class FinDataset
{
    public string Account { get; set; } = "";
    public string AsOf { get; set; } = "";
    public List<FinTrade> Trades { get; set; } = [];
    public Dictionary<string, FinPosition> Positions { get; set; } = [];
}
