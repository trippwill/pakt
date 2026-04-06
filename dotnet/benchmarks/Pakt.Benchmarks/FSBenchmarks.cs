using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Filesystem metadata benchmarks — the key PAKT scenario.
/// Compares PAKT (multi-statement with stream) vs JSON (single object)
/// across decode, deserialize, serialize, and stream-iterate workloads.
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
    // Deserialize (PAKT PaktStreamReader vs JSON JsonSerializer)
    //
    // Note: PAKT uses PaktStreamReader to read multi-statement format.
    // Root and scanned are skipped; entries are deserialized via source-gen.
    // JSON deserializes the entire document in one call.
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Deserialize"), Benchmark(Baseline = true)]
    public FSDataset PAKT_Deserialize()
    {
        var dataset = new FSDataset
        {
            Root = "/data/warehouse",
            Scanned = "2026-06-01T14:30:00Z",
        };

        var stream = PaktStreamReader.Create(_paktData);
        try
        {
            while (stream.ReadStatementAsync().GetAwaiter().GetResult())
            {
                if (stream.IsStream && stream.StatementName == "entries")
                {
                    dataset.Entries = AsyncHelper.ToListSync(
                        stream.ReadStreamElements(BenchmarkPaktContext.Default.FSEntry));
                }
                else
                {
                    stream.SkipAsync().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            stream.DisposeAsync().GetAwaiter().GetResult();
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

        writer.WriteStreamStart("entries", BenchmarkData.FSEntryPaktType);
        var serialize = BenchmarkPaktContext.Default.FSEntry.Serialize!;
        foreach (var entry in _dataset.Entries)
            serialize(writer, entry);
        writer.WriteStreamEnd();

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
    // Stream (PAKT statement-level iteration vs JSON full-document load)
    //
    // Demonstrates the streaming API pattern: PAKT can iterate stream elements
    // statement-by-statement, while JSON must deserialize the entire document
    // to access nested arrays.
    //
    // Note: The current PaktStreamReader implementation materializes stream
    // elements internally; the API shape is designed for future true streaming.
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Stream"), Benchmark(Baseline = true)]
    public int PAKT_Stream()
    {
        var stream = PaktStreamReader.Create(_paktData);
        int count = 0;
        try
        {
            while (stream.ReadStatementAsync().GetAwaiter().GetResult())
            {
                if (stream.IsStream)
                {
                    count = AsyncHelper.CountSync(
                        stream.ReadStreamElements(BenchmarkPaktContext.Default.FSEntry));
                }
                else
                {
                    stream.SkipAsync().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            stream.DisposeAsync().GetAwaiter().GetResult();
        }

        return count;
    }

    [BenchmarkCategory("Stream"), Benchmark]
    public int JSON_FullLoad()
    {
        var dataset = JsonSerializer.Deserialize(_jsonData, BenchmarkJsonContext.Default.FSDataset)!;
        return dataset.Entries.Count;
    }
}
