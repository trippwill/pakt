using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Wide document benchmarks: 100 alternating str/int fields.
/// Measures parser throughput and encoder throughput on many fields.
/// No deserialization since there is no 100-field CLR type.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class WideBenchmarks
{
    private byte[] _paktData = null!;
    private byte[] _jsonData = null!;
    private ArrayBufferWriter<byte> _paktBuffer = null!;
    private ArrayBufferWriter<byte> _jsonBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _paktData = BenchmarkData.WidePakt;
        _jsonData = BenchmarkData.WideJson;
        _paktBuffer = new ArrayBufferWriter<byte>(8192);
        _jsonBuffer = new ArrayBufferWriter<byte>(8192);
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
        var strType = BenchmarkData.StrType;
        var intType = BenchmarkData.IntType;

        for (int i = 1; i <= 100; i++)
        {
            string name = $"field_{i:D3}";
            if (i % 2 != 0)
            {
                writer.WriteAssignmentStart(name, strType);
                writer.WriteStringValue($"value_{i:D3}");
                writer.WriteAssignmentEnd();
            }
            else
            {
                writer.WriteAssignmentStart(name, intType);
                writer.WriteIntValue(i);
                writer.WriteAssignmentEnd();
            }
        }

        return _paktBuffer.WrittenCount;
    }

    [BenchmarkCategory("Encode"), Benchmark]
    public int JSON_Encode()
    {
        _jsonBuffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_jsonBuffer);
        writer.WriteStartObject();

        for (int i = 1; i <= 100; i++)
        {
            string name = $"field_{i:D3}";
            if (i % 2 != 0)
                writer.WriteString(name, $"value_{i:D3}");
            else
                writer.WriteNumber(name, i);
        }

        writer.WriteEndObject();
        writer.Flush();
        return _jsonBuffer.WrittenCount;
    }
}
