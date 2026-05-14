using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Pakt.Benchmarks;

/// <summary>
/// PAKT list pack vs JSONL (newline-delimited JSON).
/// FS-style struct entries: {path:str size:int is_dir:bool mod_time:str}
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class StreamBenchmarks
{
    [Params(10, 100, 1_000, 10_000)]
    public int EntryCount { get; set; }

    private byte[] _paktBytes = [];
    private byte[] _jsonlBytes = [];

    [GlobalSetup]
    public void Setup()
    {
        _paktBytes = GeneratePakt(EntryCount);
        _jsonlBytes = GenerateJsonl(EntryCount);
    }

    [Benchmark(Description = "PAKT v7 memory")]
    public int PaktV7Drain()
    {
        using var reader = new PaktMemoryReader(new ReadOnlyMemory<byte>(_paktBytes));
        int count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [Benchmark(Description = "PAKT v8 sequence")]
    public int PaktV8Drain()
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(_paktBytes);
        var reader = new PaktReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read())
            count++;
        return count;
    }

    [Benchmark(Baseline = true, Description = "JSONL Utf8JsonReader")]
    public int JsonlTokenize()
    {
        int count = 0;
        int offset = 0;
        ReadOnlySpan<byte> data = _jsonlBytes;

        while (offset < data.Length)
        {
            int newline = data[offset..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = newline >= 0
                ? data.Slice(offset, newline)
                : data[offset..];

            if (line.Length > 0)
            {
                Utf8JsonReader jsonReader = new(line);
                while (jsonReader.Read())
                    count++;
            }

            offset += line.Length + 1;
        }

        return count;
    }

    private static byte[] GeneratePakt(int n)
    {
        StringBuilder sb = new();
        sb.AppendLine("entries:[{path:str size:int is_dir:bool mod_time:str}] <<");
        for (int i = 0; i < n; i++)
        {
            string path = $"/data/warehouse/incoming/file_{i:D6}.dat";
            int size = (i * 7919) % 100_000_000;
            string isDir = i % 7 == 0 ? "true" : "false";
            string modTime = $"2026-01-{(i % 28) + 1:D2}T{i % 24:D2}:30:00Z";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{{ '{path}' {size} {isDir} '{modTime}' }}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] GenerateJsonl(int n)
    {
        StringBuilder sb = new();
        for (int i = 0; i < n; i++)
        {
            string path = $"/data/warehouse/incoming/file_{i:D6}.dat";
            int size = (i * 7919) % 100_000_000;
            bool isDir = i % 7 == 0;
            string modTime = $"2026-01-{(i % 28) + 1:D2}T{i % 24:D2}:30:00Z";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{{\"path\":\"{path}\",\"size\":{size},\"is_dir\":{(isDir ? "true" : "false")},\"mod_time\":\"{modTime}\"}}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
