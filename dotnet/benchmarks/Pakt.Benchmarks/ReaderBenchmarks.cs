using BenchmarkDotNet.Attributes;
using Pakt;

namespace Pakt.Benchmarks;

/// <summary>
/// Measures PaktReader tokenization throughput and allocations.
/// warehouse.pakt is ~123 KB / ~10,302 tokens; scalars.pakt is a small document with all scalar types.
/// </summary>
[MemoryDiagnoser]
public class ReaderBenchmarks
{
    private byte[] _warehouseData = null!;
    private byte[] _scalarsData = null!;

    [GlobalSetup]
    public void Setup()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "testdata");
        _warehouseData = File.ReadAllBytes(Path.Combine(basePath, "warehouse.pakt"));
        _scalarsData = File.ReadAllBytes(Path.Combine(basePath, "scalars.pakt"));
    }

    [Benchmark]
    public int TokenizeWarehouse()
    {
        var reader = new PaktReader(_warehouseData);
        int count = 0;
        while (reader.Read()) count++;
        reader.Dispose();
        return count;
    }

    [Benchmark]
    public int TokenizeScalars()
    {
        var reader = new PaktReader(_scalarsData);
        int count = 0;
        while (reader.Read()) count++;
        reader.Dispose();
        return count;
    }
}
