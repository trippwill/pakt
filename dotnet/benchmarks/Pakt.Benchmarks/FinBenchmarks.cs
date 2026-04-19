using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Financial trade + position benchmarks — the second golden PAKT scenario.
/// Richer than FS: mixed scalar types (dec, bool, uuid-shaped), embedded lists
/// (tags), and map packs (positions by ticker).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FinBenchmarks
{
    private byte[] _paktData = null!;
    private byte[] _jsonData = null!;
    private FinDataset _dataset = null!;

    [Params(1000, 10000)]
    public int TradeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (pakt, json, dataset) = BenchmarkData.GenerateFin(TradeCount);
        _paktData = pakt;
        _jsonData = json;
        _dataset = dataset;
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
    // Deserialize (whole-unit materialization)
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Deserialize"), Benchmark(Baseline = true)]
    public FinDataset PAKT_Deserialize()
    {
        var dataset = new FinDataset
        {
            Account = "ACCT-7734-PRIME",
            AsOf = _dataset.AsOf,
        };

        using var reader = PaktMemoryReader.Create(_paktData, BenchmarkPaktContext.Default);
        while (reader.ReadStatement())
        {
            if (reader.IsPack && reader.StatementName == "trades")
            {
                dataset.Trades = new List<FinTrade>(reader.ReadPack<FinTrade>());
            }
            else if (reader.IsPack && reader.StatementName == "positions")
            {
                dataset.Positions = new Dictionary<string, FinPosition>();
                foreach (var entry in reader.ReadMapPack<string, FinPosition>())
                    dataset.Positions[entry.Key] = entry.Value;
            }
            else
            {
                reader.Skip();
            }
        }

        return dataset;
    }

    [BenchmarkCategory("Deserialize"), Benchmark]
    public FinDataset? JSON_Deserialize()
    {
        return JsonSerializer.Deserialize(_jsonData, BenchmarkJsonContext.Default.FinDataset);
    }

    // ---------------------------------------------------------------------------
    // Pack (streaming iteration — the golden metric)
    // ---------------------------------------------------------------------------

    [BenchmarkCategory("Stream"), Benchmark(Baseline = true)]
    public int PAKT_Stream()
    {
        using var reader = PaktMemoryReader.Create(_paktData, BenchmarkPaktContext.Default);
        int count = 0;
        while (reader.ReadStatement())
        {
            if (reader.IsPack && reader.StatementName == "trades")
            {
                foreach (var _ in reader.ReadPack<FinTrade>())
                    count++;
            }
            else
            {
                reader.Skip();
            }
        }

        return count;
    }

    [BenchmarkCategory("Stream"), Benchmark]
    public int JSON_Stream()
    {
        var dataset = JsonSerializer.Deserialize(_jsonData, BenchmarkJsonContext.Default.FinDataset)!;
        return dataset.Trades.Count;
    }
}
