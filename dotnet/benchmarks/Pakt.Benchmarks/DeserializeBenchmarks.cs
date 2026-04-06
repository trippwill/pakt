using System.Buffers;
using BenchmarkDotNet.Attributes;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Benchmarks;

// --- Benchmark types ---

public class BenchmarkServer
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public class BenchmarkPerson
{
    public string Name { get; set; } = "";
    public BenchmarkAddress Home { get; set; } = new();
}

public class BenchmarkAddress
{
    public string City { get; set; } = "";
    public int Zip { get; set; }
}

[PaktSerializable(typeof(BenchmarkServer))]
[PaktSerializable(typeof(BenchmarkPerson))]
[PaktSerializable(typeof(BenchmarkAddress))]
public partial class BenchmarkPaktContext : PaktSerializerContext { }

// --- Benchmarks ---

/// <summary>
/// Compares raw tokenization overhead vs full deserialization through the source-generated path.
/// </summary>
[MemoryDiagnoser]
public class DeserializeBenchmarks
{
    private byte[] _flatStructData = null!;
    private byte[] _nestedStructData = null!;
    private byte[] _serializedServerData = null!;

    [GlobalSetup]
    public void Setup()
    {
        _flatStructData = "server:{host:str, port:int} = {'localhost', 8080}\n"u8.ToArray();
        _nestedStructData = "p:{name:str, home:{city:str, zip:int}} = {'Alice', {'Springfield', 62704}}\n"u8.ToArray();

        // Pre-serialize a value for round-trip benchmark
        var server = new BenchmarkServer { Host = "benchmark-host.example.com", Port = 9090 };
        _serializedServerData = PaktSerializer.Serialize(server, BenchmarkPaktContext.Default.BenchmarkServer, "s");
    }

    [Benchmark(Baseline = true)]
    public int TokenizeOnly()
    {
        var reader = new PaktReader(_flatStructData);
        int count = 0;
        while (reader.Read()) count++;
        reader.Dispose();
        return count;
    }

    [Benchmark]
    public BenchmarkServer DeserializeFlatStruct()
    {
        return PaktSerializer.Deserialize(_flatStructData, BenchmarkPaktContext.Default.BenchmarkServer);
    }

    [Benchmark]
    public BenchmarkPerson DeserializeNestedStruct()
    {
        return PaktSerializer.Deserialize(_nestedStructData, BenchmarkPaktContext.Default.BenchmarkPerson);
    }

    [Benchmark]
    public byte[] SerializeFlatStruct()
    {
        var server = new BenchmarkServer { Host = "localhost", Port = 8080 };
        return PaktSerializer.Serialize(server, BenchmarkPaktContext.Default.BenchmarkServer, "s");
    }

    [Benchmark]
    public BenchmarkServer RoundTrip()
    {
        return PaktSerializer.Deserialize(_serializedServerData, BenchmarkPaktContext.Default.BenchmarkServer);
    }
}
