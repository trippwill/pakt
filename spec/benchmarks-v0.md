# PAKT Benchmark Specification v0

This document defines the standard benchmark datasets and measurement categories
for PAKT implementations. Any platform implementing PAKT should be able to
reproduce these benchmarks for cross-platform performance comparison.

## Design Principles

1. **Deterministic**: All datasets use fixed RNG seeds. The same seed must produce
   the same data on any platform.
2. **Domain-realistic**: Datasets model real workloads (filesystem metadata, financial trades)
   rather than synthetic micro-patterns.
3. **Scale-parametric**: Each dataset has 1K and 10K variants. The 10K variants are
   the primary performance targets.
4. **Format-comparative**: Each benchmark compares PAKT against the platform's canonical
   JSON implementation (Go: `encoding/json`, .NET: `System.Text.Json`, etc.).

## Measurement Categories

Each dataset is measured across these categories where applicable:

| Category | What it measures | PAKT operation | JSON baseline |
|----------|-----------------|----------------|---------------|
| **Decode** | Raw tokenization throughput | Token-by-token reader loop | Token-by-token reader loop |
| **Deserialize** | Whole-unit materialization into typed objects | `Deserialize<T>` / `UnmarshalNew` | `Deserialize<T>` / `json.Unmarshal` |
| **Stream** | Streaming pack iteration with per-element deserialization | Statement reader + `ReadPack<T>` / `PackItems[T]` | Full-document deserialize + access collection |
| **Serialize** | Typed object to PAKT/JSON bytes | `Serialize<T>` / `Marshal` | `Serialize` / `json.Marshal` |
| **Encode** | Low-level writer API throughput | Writer API calls | Writer API calls |

**Golden metrics**: Stream 10K for both FS and Fin datasets. These represent PAKT's
streaming advantage and are the primary regression targets.

## Dataset 1: Small (single struct)

**Purpose**: Baseline for per-unit overhead. Measures the fixed cost of parsing one
statement with a moderate number of fields.

### Type

```pakt
doc:{name:str, version:int, debug:bool, rate:float, host:str, port:int, max_retry:int, timeout:int, verbose:bool, label:str}
```

### Static Value

| Field | Value |
|-------|-------|
| name | `"my-app"` |
| version | `42` |
| debug | `true` |
| rate | `3.14` |
| host | `"localhost"` |
| port | `8080` |
| max_retry | `3` |
| timeout | `30` |
| verbose | `false` |
| label | `"production"` |

### Host Type Mapping

| PAKT field | PAKT type | Go | C# | Notes |
|------------|-----------|-----|-----|-------|
| name | str | string | string | |
| version | int | int | int | |
| debug | bool | bool | bool | |
| rate | float | float64 | double | |
| host | str | string | string | |
| port | int | int | int | |
| max_retry | int | int | int | |
| timeout | int | int | int | |
| verbose | bool | bool | bool | |
| label | str | string | string | |

### Categories

Decode, Deserialize, Serialize.

---

## Dataset 2: Wide (100 statements)

**Purpose**: Measures multi-statement unit parsing overhead. Tests the statement
header parsing path at scale.

### Shape

100 top-level statements named `field_001` through `field_100`, alternating types:

- Odd indices: `field_NNN:str = 'value_NNN'`
- Even indices: `field_NNN:int = N`

### Categories

Decode, Encode.

---

## Dataset 3: Deep (nested struct)

**Purpose**: Tests nesting depth handling. Stresses the parser frame stack.

### Shape

A single statement with 10 levels of nested structs:

```pakt
root:{name:str, child:{name:str, child:{...}}} = {'level_0', {'level_1', {...}}}
```

Innermost level has only `name:str`.

**Nesting depth**: 10.

### Categories

Decode, Encode.

---

## Dataset 4: LargeList (10K integers)

**Purpose**: Tests homogeneous list parsing throughput.

### Shape

```pakt
numbers:[int] = [1, 2, 3, ..., 10000]
```

**Element count**: 10,000.

### Categories

List-Decode, List-Encode.

---

## Dataset 5: LargeMap (1K string→int entries)

**Purpose**: Tests map parsing throughput with key-value separator handling.

### Shape

```pakt
data:<str ; int> = <'key_0001' ; 1, 'key_0002' ; 2, ..., 'key_1000' ; 1000>
```

**Entry count**: 1,000.

### Categories

Map-Decode, Map-Encode.

---

## Dataset 6: FS — Filesystem Metadata (1K/10K) ⭐

**Purpose**: The first golden benchmark. Models a filesystem scan result with
scalar-heavy struct elements in a list pack. This is the "wide struct, many
elements" archetype.

### Types

```pakt
# Statement: scalar assignments
root:str = '...'
scanned:ts = 2026-06-01T14:30:00Z

# Statement: list pack of struct elements
entries:[{path:str, size:int, mode:int, mod_time:ts, is_dir:bool, owner:str, group:str, hash:bin}] <<
    { '...', 1234, 33188, 2026-01-15T10:30:00Z, false, 'etl', 'data', x'00000001' }
    { '...', 0, 16877, 2026-02-20T14:00:00Z, true, 'root', 'root', x'' }
    ...
```

### Host Type Mapping

| PAKT field | PAKT type | Go | C# |
|------------|-----------|-----|-----|
| path | str | string | string |
| size | int | int64 | long |
| mode | int | int64 | long |
| mod_time | ts | string | string |
| is_dir | bool | bool | bool |
| owner | str | string | string |
| group | str | string | string |
| hash | bin | []byte | byte[] |

### Data Generation

**RNG seed**: 42

**Parameters**:
- `is_dir`: 15% probability
- Path depth: 1–4 subdirectory levels
- Subdirectory pool: `incoming`, `archive`, `staging`, `reports`, `temp`, `indexes`
- Root path: `/data/warehouse`
- File size: 1 KB – 100 MB (uniform random)
- File modes: `0o100644`, `0o100600`, `0o100444` (files); `0o40755` (directories)
- Owner pool (cyclic by index): `etl`, `root`, `app`, `backup`, `deploy`
- Group pool (cyclic by index): `data`, `root`, `apps`, `ops`
- Hash: hex-encoded file index (`%08x`), empty for directories
- Modification time: base `2026-01-01T00:00:00Z`, random offset 0–150 days + 0–23 hours

**Scales**: N=1,000 and N=10,000.

### Categories

Decode, Deserialize, Stream ⭐, Serialize.

---

## Dataset 7: Fin — Financial Trades + Positions (1K/10K) ⭐

**Purpose**: The second golden benchmark. Models a trade execution log with
richer scalar types and both list packs and map packs. This is the "complex
struct, mixed composites" archetype.

### Types

```pakt
# Scalar assignments
account:str = 'ACCT-7734-PRIME'
as_of:ts = 2026-03-01T17:30:00-05:00

# List pack: trades
trades:[{timestamp:ts, ticker:str, side:|buy, sell|, quantity:int, price:dec, fees:dec, filled:bool, venue:str, order_id:uuid, tags:[str]}] <<
    { 2026-03-01T09:30:00-05:00, 'AAPL', |buy, 500, 152.30, 1.25, true, 'NYSE', a1b2c3d4-..., ['algo', 'sweep'] }
    ...

# Map pack: positions by ticker
positions:<str ; {qty:int, avg_cost:dec, unrealized_pnl:dec, last_price:dec, updated:ts}> <<
    'AAPL' ; { 12000, 148.50, 45600.00, 152.30, 2026-03-01T17:30:00-05:00 }
    ...
```

### Host Type Mapping

| PAKT field | PAKT type | Go | C# |
|------------|-----------|-----|-----|
| **Trade** | | | |
| timestamp | ts | string | string |
| ticker | str | string | string |
| side | \|buy, sell\| | string | string |
| quantity | int | int64 | long |
| price | dec | string | string |
| fees | dec | string | string |
| filled | bool | bool | bool |
| venue | str | string | string |
| order_id | uuid | string | string |
| tags | [str] | []string | List\<string\> |
| **Position** | | | |
| qty | int | int64 | long |
| avg_cost | dec | string | string |
| unrealized_pnl | dec | string | string |
| last_price | dec | string | string |
| updated | ts | string | string |

### Data Generation

**RNG seed**: 77

**Parameters**:
- Ticker pool (20): AAPL, GOOG, MSFT, AMZN, NVDA, META, TSLA, JPM, V, UNH, XOM, JNJ, PG, MA, HD, CVX, MRK, ABBV, PEP, KO
- Venue pool (6): NYSE, NASDAQ, BATS, IEX, EDGX, MEMX
- Tag pool (8): algo, manual, dark-pool, pre-market, post-market, block, sweep, iceberg
- Base time: `2026-03-01T09:30:00-05:00`
- Side: buy (55%) / sell (45%)
- Quantity: 100–10,000 shares (uniform)
- Price: $10.00–$409.99 (random dollars + cents)
- Fees: $0.01–$5.00 (random cents)
- Filled: 92% probability
- Tags per trade: 1–3 (uniform)
- Trade timestamps: sequential, base + `i*3 + rand(0..2)` seconds
- Order ID: UUID v4 format (hex-encoded random segments)
- Positions: one per ticker in the pool (20 total), with random cost/price/PnL

**Scales**: N=1,000 and N=10,000.

### Categories

Decode, Deserialize, Stream ⭐, Serialize (future).

---

## Regression Targets

These are the performance ratios that implementations should maintain. The baseline
is the platform's canonical JSON implementation with source generation / compile-time
optimization enabled.

| Metric | Target | Notes |
|--------|--------|-------|
| **Stream FS10K** | ≤ 1.5× JSON | Currently 1.24× in .NET, ~1.0× in Go |
| **Stream Fin10K** | ≤ 1.0× JSON | Currently 0.93× in .NET (PAKT wins), ~1.0× in Go |
| **Deserialize FS10K** | ≤ 3.0× JSON | Structural overhead from typed grammar |
| **Decode FS10K** | ≤ 4.0× JSON | PAKT decoder allocates; JSON tokenizers are zero-alloc |

> Stream metrics are the golden targets. PAKT's value proposition is streaming
> pack iteration — if a new implementation can't match JSON on Stream 10K
> workloads, the architecture needs investigation before shipping.

---

## Implementing a Benchmark Suite

For a new platform implementation:

1. **Implement data generators** using the seeds and parameters above. Verify
   determinism by checking the first and last element of each dataset.

2. **Register model types** with the platform's source generation / compile-time
   serialization if available. The benchmark should measure the optimized path,
   not reflection-based fallbacks.

3. **Implement all categories** for FS and Fin datasets at minimum. Small, Wide,
   Deep, and Collection datasets are useful for diagnostics but not required for
   the golden metrics.

4. **Compare against JSON** using the platform's best JSON implementation with
   equivalent compile-time optimization. Report both absolute times and the
   PAKT/JSON ratio.

5. **Report allocations** if the platform supports allocation tracking. The
   allocation ratio is a secondary metric but important for understanding
   overhead sources.

6. **Use short-run mode** for quick iteration, full runs for publishable results.
   Minimum: 3 iterations with warmup. Recommended: statistical benchmarking
   framework (BenchmarkDotNet, Go `testing.B`, JMH, etc.).
