using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Collection benchmarks: large list (10K ints) and large map (1K str→int).
/// Measures throughput on bulk collection tokenization and encoding.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CollectionBenchmarks
{
    private byte[] _listPakt = null!;
    private byte[] _listJson = null!;
    private byte[] _mapPakt = null!;
    private byte[] _mapJson = null!;
    private ArrayBufferWriter<byte> _paktBuffer = null!;
    private ArrayBufferWriter<byte> _jsonBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _listPakt = BenchmarkData.ListPakt;
        _listJson = BenchmarkData.ListJson;
        _mapPakt = BenchmarkData.MapPakt;
        _mapJson = BenchmarkData.MapJson;
        _paktBuffer = new ArrayBufferWriter<byte>(256 * 1024);
        _jsonBuffer = new ArrayBufferWriter<byte>(256 * 1024);
    }

    // --- List Decode ---

    [BenchmarkCategory("List-Decode"), Benchmark(Baseline = true)]
    public int PAKT_Decode_List()
    {
        var reader = new PaktReader(_listPakt);
        int count = 0;
        while (reader.Read()) count++;
        reader.Dispose();
        return count;
    }

    [BenchmarkCategory("List-Decode"), Benchmark]
    public int JSON_Decode_List()
    {
        var reader = new Utf8JsonReader(_listJson);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // --- List Encode ---

    [BenchmarkCategory("List-Encode"), Benchmark(Baseline = true)]
    public int PAKT_Encode_List()
    {
        _paktBuffer.ResetWrittenCount();
        using var writer = new PaktWriter(_paktBuffer);
        var listType = PaktType.List(BenchmarkData.IntType);
        writer.WriteAssignmentStart("numbers", listType);
        writer.WriteListStart();
        for (int i = 1; i <= BenchmarkData.ListSize; i++)
            writer.WriteIntValue(i);
        writer.WriteListEnd();
        writer.WriteAssignmentEnd();
        return _paktBuffer.WrittenCount;
    }

    [BenchmarkCategory("List-Encode"), Benchmark]
    public int JSON_Encode_List()
    {
        _jsonBuffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_jsonBuffer);
        writer.WriteStartObject();
        writer.WritePropertyName("numbers");
        writer.WriteStartArray();
        for (int i = 1; i <= BenchmarkData.ListSize; i++)
            writer.WriteNumberValue(i);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return _jsonBuffer.WrittenCount;
    }

    // --- Map Decode ---

    [BenchmarkCategory("Map-Decode"), Benchmark(Baseline = true)]
    public int PAKT_Decode_Map()
    {
        var reader = new PaktReader(_mapPakt);
        int count = 0;
        while (reader.Read()) count++;
        reader.Dispose();
        return count;
    }

    [BenchmarkCategory("Map-Decode"), Benchmark]
    public int JSON_Decode_Map()
    {
        var reader = new Utf8JsonReader(_mapJson);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // --- Map Encode ---

    [BenchmarkCategory("Map-Encode"), Benchmark(Baseline = true)]
    public int PAKT_Encode_Map()
    {
        _paktBuffer.ResetWrittenCount();
        using var writer = new PaktWriter(_paktBuffer);
        var mapType = PaktType.Map(BenchmarkData.StrType, BenchmarkData.IntType);
        writer.WriteAssignmentStart("data", mapType);
        writer.WriteMapStart();
        for (int i = 1; i <= BenchmarkData.MapSize; i++)
        {
            writer.WriteStringValue($"key_{i:D4}");
            writer.WriteMapKeySeparator();
            writer.WriteIntValue(i);
        }
        writer.WriteMapEnd();
        writer.WriteAssignmentEnd();
        return _paktBuffer.WrittenCount;
    }

    [BenchmarkCategory("Map-Encode"), Benchmark]
    public int JSON_Encode_Map()
    {
        _jsonBuffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_jsonBuffer);
        writer.WriteStartObject();
        writer.WritePropertyName("data");
        writer.WriteStartObject();
        for (int i = 1; i <= BenchmarkData.MapSize; i++)
            writer.WriteNumber($"key_{i:D4}", i);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return _jsonBuffer.WrittenCount;
    }
}
