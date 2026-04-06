using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Deep nesting benchmarks: 10 levels of nested structs.
/// Measures decoder stack handling and encoder recursive write.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DeepBenchmarks
{
    private byte[] _paktData = null!;
    private byte[] _jsonData = null!;
    private PaktType _deepType = null!;
    private ArrayBufferWriter<byte> _paktBuffer = null!;
    private ArrayBufferWriter<byte> _jsonBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _paktData = BenchmarkData.DeepPakt;
        _jsonData = BenchmarkData.DeepJson;
        _deepType = BenchmarkData.DeepPaktType;
        _paktBuffer = new ArrayBufferWriter<byte>(4096);
        _jsonBuffer = new ArrayBufferWriter<byte>(4096);
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

    // --- Encode (low-level writer) ---

    [BenchmarkCategory("Encode"), Benchmark(Baseline = true)]
    public int PAKT_Encode()
    {
        _paktBuffer.ResetWrittenCount();
        using var writer = new PaktWriter(_paktBuffer);
        writer.WriteAssignmentStart("root", _deepType);
        BenchmarkData.WriteDeepPaktValue(writer, 0, BenchmarkData.DeepLevels);
        writer.WriteAssignmentEnd();
        return _paktBuffer.WrittenCount;
    }

    [BenchmarkCategory("Encode"), Benchmark]
    public int JSON_Encode()
    {
        _jsonBuffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_jsonBuffer);
        writer.WriteStartObject();
        writer.WritePropertyName("root");
        BenchmarkData.WriteDeepJsonValue(writer, 0, BenchmarkData.DeepLevels);
        writer.WriteEndObject();
        writer.Flush();
        return _jsonBuffer.WrittenCount;
    }
}
