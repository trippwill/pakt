using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Filesystem metadata benchmarks — the key PAKT scenario.
/// Compares PAKT (multi-statement with pack) vs JSON (single object)
/// across decode, deserialize, serialize, and pack-iterate workloads.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FSBenchmarks
{
    private byte[] _paktData = null!;
    private byte[] _jsonData = null!;
    private FSDataset _dataset = null!;
    private ArrayBufferWriter<byte> _paktBuffer = null!;
    private ArrayBufferWriter<byte> _jsonBuffer = null!;

    [Params(1000, 10000)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (pakt, json, dataset) = BenchmarkData.GenerateFS(EntryCount);
        _paktData = pakt;
        _jsonData = json;
        _dataset = dataset;
        _paktBuffer = new ArrayBufferWriter<byte>(pakt.Length + 4096);
        _jsonBuffer = new ArrayBufferWriter<byte>(json.Length + 4096);
    }

    // ---------------------------------------------------------------------------
    // Decode (tokenization throughput)
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Decode"), Benchmark(Baseline = true)]
    public int PAKT_Decode()
    {
        var reader = new PaktReader(_paktData);
        int count = 0;
        while (reader.Read()) count++;
        reader.Dispose();
        return count;
    }

    [BenchmarkCategory("Decode"), Benchmark]
    public int JSON_Decode()
    {
        var reader = new Utf8JsonReader(_jsonData);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // ---------------------------------------------------------------------------
    // Deserialize (PAKT PaktMemoryReader vs JSON JsonSerializer)
    //
    // PAKT reads the unit statement-by-statement and only materializes the
    // entries pack when it is encountered. JSON deserializes the whole document
    // in one call.
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Deserialize"), Benchmark(Baseline = true)]
    public FSDataset PAKT_Deserialize()
    {
        var dataset = new FSDataset
        {
            Root = "/data/warehouse",
            Scanned = "2026-06-01T14:30:00Z",
        };

        using var reader = PaktMemoryReader.Create(_paktData, BenchmarkPaktContext.Default);
        while (reader.ReadStatement())
        {
            if (reader.IsPack && reader.StatementName == "entries")
            {
                dataset.Entries = new List<FSEntry>(reader.ReadPack<FSEntry>());
            }
            else
            {
                reader.Skip();
            }
        }

        return dataset;
    }

    [BenchmarkCategory("Deserialize"), Benchmark]
    public FSDataset? JSON_Deserialize()
    {
        return JsonSerializer.Deserialize(_jsonData, BenchmarkJsonContext.Default.FSDataset);
    }

    // ---------------------------------------------------------------------------
    // Serialize (PAKT PaktWriter + source-gen vs JSON JsonSerializer)
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Serialize"), Benchmark(Baseline = true)]
    public int PAKT_Serialize()
    {
        _paktBuffer.ResetWrittenCount();
        using var writer = new PaktWriter(_paktBuffer);

        writer.WriteAssignmentStart("root", BenchmarkData.StrType);
        writer.WriteStringValue(_dataset.Root);
        writer.WriteAssignmentEnd();

        writer.WriteAssignmentStart("scanned", BenchmarkData.StrType);
        writer.WriteStringValue(_dataset.Scanned);
        writer.WriteAssignmentEnd();

        writer.WritePackStart("entries", BenchmarkData.FSEntryListPaktType);
        var serialize = BenchmarkPaktContext.Default.FSEntry.Serialize!;
        foreach (var entry in _dataset.Entries)
            serialize(writer, entry);
        writer.WritePackEnd();

        return _paktBuffer.WrittenCount;
    }

    [BenchmarkCategory("Serialize"), Benchmark]
    public int JSON_Serialize()
    {
        _jsonBuffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_jsonBuffer);
        JsonSerializer.Serialize(writer, _dataset, BenchmarkJsonContext.Default.FSDataset);
        writer.Flush();
        return _jsonBuffer.WrittenCount;
    }

    // ---------------------------------------------------------------------------
    // Pack (PAKT statement-level iteration vs JSON full-document load)
    //
    // Demonstrates the pack API pattern: PAKT can iterate pack elements once
    // the pack statement is reached, while JSON must deserialize the entire
    // document to access nested arrays.
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Pack"), Benchmark(Baseline = true)]
    public int PAKT_Pack()
    {
        using var reader = PaktMemoryReader.Create(_paktData, BenchmarkPaktContext.Default);
        int count = 0;
        while (reader.ReadStatement())
        {
            if (reader.IsPack)
            {
                foreach (var _ in reader.ReadPack<FSEntry>())
                {
                    count++;
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return count;
    }

    [BenchmarkCategory("Pack"), Benchmark]
    public int JSON_FullLoad()
    {
        var dataset = JsonSerializer.Deserialize(_jsonData, BenchmarkJsonContext.Default.FSDataset)!;
        return dataset.Entries.Count;
    }
}
