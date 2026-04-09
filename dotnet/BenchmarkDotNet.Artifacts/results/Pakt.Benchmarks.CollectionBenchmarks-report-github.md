```

BenchmarkDotNet v0.15.8, Linux Fedora Linux 43 (COSMIC)
Intel Core Ultra 5 228V 0.40GHz, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.5 (10.0.5, 10.0.526.15411), X64 RyuJIT x86-64-v3


```
| Method           | Categories  | Mean      | Error    | StdDev    | Median    | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|----------------- |------------ |----------:|---------:|----------:|----------:|------:|--------:|--------:|----------:|------------:|
| PAKT_Decode_List | List-Decode | 349.87 μs | 6.935 μs | 14.322 μs | 341.07 μs |  1.00 |    0.06 | 93.7500 |  392216 B |        1.00 |
| JSON_Decode_List | List-Decode |  92.99 μs | 0.713 μs |  0.952 μs |  93.09 μs |  0.27 |    0.01 |       - |         - |        0.00 |
|                  |             |           |          |           |           |       |         |         |           |             |
| PAKT_Encode_List | List-Encode |  84.97 μs | 1.686 μs |  4.040 μs |  81.95 μs |  1.00 |    0.07 |  0.1221 |     992 B |        1.00 |
| JSON_Encode_List | List-Encode |  47.37 μs | 0.945 μs |  2.523 μs |  45.82 μs |  0.56 |    0.04 |       - |     136 B |        0.14 |
|                  |             |           |          |           |           |       |         |         |           |             |
| PAKT_Decode_Map  | Map-Decode  |  70.90 μs | 1.418 μs |  3.834 μs |  68.95 μs |  1.00 |    0.07 | 67.0166 |  280312 B |        1.00 |
| JSON_Decode_Map  | Map-Decode  |  12.27 μs | 0.092 μs |  0.077 μs |  12.27 μs |  0.17 |    0.01 |       - |         - |        0.00 |
|                  |             |           |          |           |           |       |         |         |           |             |
| PAKT_Encode_Map  | Map-Encode  |  91.32 μs | 1.824 μs |  4.508 μs |  88.03 μs |  1.00 |    0.07 |  9.7656 |   41071 B |        1.00 |
| JSON_Encode_Map  | Map-Encode  |  35.07 μs | 0.428 μs |  0.400 μs |  34.85 μs |  0.38 |    0.02 |  9.5825 |   40136 B |        0.98 |
