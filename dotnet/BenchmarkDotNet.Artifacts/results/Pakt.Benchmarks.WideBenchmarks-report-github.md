```

BenchmarkDotNet v0.15.8, Linux Fedora Linux 43 (COSMIC)
Intel Core Ultra 5 228V 0.40GHz, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method      | Categories | Mean     | Error     | StdDev    | Median   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------ |----------- |---------:|----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| PAKT_Decode | Decode     | 8.713 μs | 0.1231 μs | 0.1368 μs | 8.780 μs |  1.00 |    0.02 | 6.3019 |   26400 B |        1.00 |
| JSON_Decode | Decode     | 1.367 μs | 0.0274 μs | 0.0546 μs | 1.332 μs |  0.16 |    0.01 |      - |         - |        0.00 |
|             |            |          |           |           |          |       |         |        |           |             |
| PAKT_Encode | Encode     | 9.443 μs | 0.1873 μs | 0.5033 μs | 9.127 μs |  1.00 |    0.07 | 2.9602 |   12434 B |        1.00 |
| JSON_Encode | Encode     | 5.887 μs | 0.1022 μs | 0.0906 μs | 5.834 μs |  0.63 |    0.03 | 1.4648 |    6136 B |        0.49 |
