```

BenchmarkDotNet v0.15.8, Linux Fedora Linux 43 (COSMIC)
Intel Core Ultra 5 228V 0.40GHz, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method      | Categories | Mean       | Error    | StdDev   | Median     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------ |----------- |-----------:|---------:|---------:|-----------:|------:|--------:|-------:|----------:|------------:|
| PAKT_Decode | Decode     | 1,682.6 ns | 33.46 ns | 77.54 ns | 1,628.8 ns |  1.00 |    0.06 | 1.7166 |    7184 B |        1.00 |
| JSON_Decode | Decode     |   337.8 ns |  6.66 ns | 10.17 ns |   332.7 ns |  0.20 |    0.01 |      - |         - |        0.00 |
|             |            |            |          |          |            |       |         |        |           |             |
| PAKT_Encode | Encode     | 2,089.5 ns |  5.53 ns |  4.62 ns | 2,088.8 ns |  1.00 |    0.00 | 2.0485 |    8570 B |        1.00 |
| JSON_Encode | Encode     |   454.4 ns |  7.51 ns |  5.86 ns |   456.9 ns |  0.22 |    0.00 | 0.1278 |     536 B |        0.06 |
