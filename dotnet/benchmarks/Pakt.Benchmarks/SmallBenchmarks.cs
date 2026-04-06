using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Small document benchmarks: 10 mixed scalar fields.
/// Compares PAKT source-gen vs System.Text.Json source-gen.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SmallBenchmarks
{
    private byte[] _paktData = null!;
    private byte[] _jsonData = null!;
    private SmallDoc _doc = null!;

    [GlobalSetup]
    public void Setup()
    {
        _paktData = BenchmarkData.SmallPakt;
        _jsonData = BenchmarkData.SmallJson;
        _doc = BenchmarkData.SmallDocValue;
    }

    // --- Decode (tokenization throughput) ---

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

    // --- Deserialize (source-generated) ---

    [BenchmarkCategory("Deserialize"), Benchmark(Baseline = true)]
    public SmallDoc PAKT_Deserialize()
    {
        return PaktSerializer.Deserialize(_paktData, BenchmarkPaktContext.Default.SmallDoc);
    }

    [BenchmarkCategory("Deserialize"), Benchmark]
    public SmallDoc? JSON_Deserialize()
    {
        return JsonSerializer.Deserialize(_jsonData, BenchmarkJsonContext.Default.SmallDoc);
    }

    // --- Serialize (source-generated) ---

    [BenchmarkCategory("Serialize"), Benchmark(Baseline = true)]
    public byte[] PAKT_Serialize()
    {
        return PaktSerializer.Serialize(_doc, BenchmarkPaktContext.Default.SmallDoc, "doc");
    }

    [BenchmarkCategory("Serialize"), Benchmark]
    public byte[] JSON_Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(_doc, BenchmarkJsonContext.Default.SmallDoc);
    }
}
