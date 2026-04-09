```

BenchmarkDotNet v0.15.8, Linux Fedora Linux 43 (COSMIC)
Intel Core Ultra 5 228V 0.40GHz, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method           | Categories  | Mean       | Error    | StdDev   | Median     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------- |------------ |-----------:|---------:|---------:|-----------:|------:|--------:|-------:|----------:|------------:|
| PAKT_Decode      | Decode      |   851.2 ns | 17.25 ns | 50.86 ns |   833.7 ns |  1.00 |    0.08 | 0.7019 |    2936 B |        1.00 |
| JSON_Decode      | Decode      |   151.5 ns |  1.79 ns |  1.49 ns |   151.9 ns |  0.18 |    0.01 |      - |         - |        0.00 |
|                  |             |            |          |          |            |       |         |        |           |             |
| PAKT_Deserialize | Deserialize | 1,063.3 ns | 24.43 ns | 72.04 ns | 1,033.0 ns |  1.00 |    0.09 | 0.7896 |    3304 B |        1.00 |
| JSON_Deserialize | Deserialize |   272.7 ns |  6.00 ns | 17.70 ns |   260.8 ns |  0.26 |    0.02 | 0.0477 |     200 B |        0.06 |
|                  |             |            |          |          |            |       |         |        |           |             |
| PAKT_Serialize   | Serialize   |   859.1 ns | 17.03 ns | 43.66 ns |   849.8 ns |  1.00 |    0.07 | 0.8640 |    3617 B |        1.00 |
| JSON_Serialize   | Serialize   |   147.5 ns |  3.46 ns | 10.20 ns |   142.6 ns |  0.17 |    0.01 | 0.0420 |     176 B |        0.05 |
