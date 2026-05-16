using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

using BenchmarkDotNet.Attributes;

namespace Pakt.Benchmarks;

/// <summary>
/// Materialization benchmark: actually extract typed values from tokens.
/// Tests the cost of value decoding, not just tokenization.
/// Uses types where JSON must use string workarounds: UUID, date, timestamp, binary.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MaterializeBenchmarks
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

    [Benchmark(Description = "PAKT v8 materialize")]
    public int PaktV8Materialize()
    {
        var seq = new ReadOnlySequence<byte>(_paktBytes);
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
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
                case PaktTokenType.Uuid:
                    _ = reader.GetGuid();
                    count++;
                    break;
                case PaktTokenType.Timestamp:
                    _ = reader.GetTimestamp();
                    count++;
                    break;
                case PaktTokenType.Date:
                    _ = reader.GetDate();
                    count++;
                    break;
                case PaktTokenType.Binary:
                    _ = reader.GetBytes();
                    count++;
                    break;
                case PaktTokenType.Decimal:
                    _ = reader.GetDecimal();
                    count++;
                    break;
            }
        }

        return count;
    }

    [Benchmark(Baseline = true, Description = "JSON materialize")]
    public int JsonMaterialize()
    {
        Utf8JsonReader reader = new(_jsonBytes);
        int count = 0;
        string? propName = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    propName = reader.GetString();
                    break;
                case JsonTokenType.String:
                    // JSON stores UUID, date, timestamp, binary as strings —
                    // must parse from string representation
                    switch (propName)
                    {
                        case "id":
                            _ = Guid.Parse(reader.GetString()!);
                            break;
                        case "created":
                            _ = DateOnly.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
                            break;
                        case "modified":
                            _ = DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
                            break;
                        case "hash":
                            _ = Convert.FromHexString(reader.GetString()!);
                            break;
                        default:
                            _ = reader.GetString();
                            break;
                    }
                    count++;
                    break;
                case JsonTokenType.Number:
                    if (string.Equals(propName, "price", StringComparison.Ordinal))
                        _ = reader.GetDecimal();
                    else
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

    private static (byte[] Pakt, byte[] Json) GenerateData(int n)
    {
        var rng = new Random(42);
        var pb = new StringBuilder();
        var jb = new StringBuilder();

        // PAKT: pack of structs with all typed fields
        pb.AppendLine("records:[{name:str count:int price:dec active:bool id:uuid created:date modified:ts hash:bin}] = ~[");

        // JSON: array of objects (uuid/date/ts/binary as strings)
        jb.Append("[");

        for (int i = 0; i < n; i++)
        {
            AppendEntry(rng, i, n, pb, jb);
        }

        jb.Append("]");
        return (Encoding.UTF8.GetBytes(pb.ToString()), Encoding.UTF8.GetBytes(jb.ToString()));
    }

    private static void AppendEntry(Random rng, int i, int n, StringBuilder pb, StringBuilder jb)
    {
        string name = $"item-{i:D5}";
        int count = rng.Next(1, 10000);
        decimal price = Math.Round((decimal)(rng.NextDouble() * 1000), 2);
        bool active = rng.NextDouble() > 0.3;
        Guid id = new(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        string idStr = id.ToString("D");
        int dayOff = rng.Next(365);
        var created = new DateOnly(2025, 1, 1).AddDays(dayOff);
        var modified = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddDays(dayOff).AddHours(rng.Next(24)).AddMinutes(rng.Next(60));
        string hash = $"{i:x8}";

        // PAKT: native typed values — no string wrapping needed
        pb.AppendLine(CultureInfo.InvariantCulture,
            $"    {{ '{name}' {count} {price} {(active ? "true" : "false")} {idStr} {created:yyyy-MM-dd} {modified:yyyy-MM-ddTHH:mm:ssZ} x'{hash}' }}");

        // JSON: uuid/date/timestamp/binary MUST be strings
        if (i > 0) jb.Append(',');
        jb.Append(CultureInfo.InvariantCulture,
            $"{{\"name\":\"{name}\",\"count\":{count},\"price\":{price},\"active\":{(active ? "true" : "false")}," +
            $"\"id\":\"{idStr}\",\"created\":\"{created:yyyy-MM-dd}\",\"modified\":\"{modified:yyyy-MM-ddTHH:mm:ssZ}\",\"hash\":\"{hash}\"}}");
    }
}