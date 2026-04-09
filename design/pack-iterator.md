# Pack Iterator API

## Status

Design sketch. Not yet implemented.

## Problem

The current `UnmarshalNext` API for pack statements (`<<`) has two modes:

- **Struct target**: reads the entire pack into a matching slice/map field — allocates a full backing array, with `reflect.growslice` copying on each resize.
- **Direct target**: reads one element per call via `More()` + `UnmarshalNext` — works for scalar types but is awkward for struct elements (the struct-target path intercepts first).

Profiling shows `reflect.growslice` accounts for ~55% of allocations in streaming benchmarks, and `strings.Builder` accounts for ~30%. The slice growth is fundamentally wasteful for packs, which are designed as open-ended sequences consumed one element at a time.

## Proposal

Add a generic pack iterator API using Go's `iter.Seq[T]` (available since Go 1.23):

```go
// PackItems returns an iterator over pack elements of type T.
// Each element is decoded on demand — no backing slice is allocated.
func PackItems[T any](dec *Decoder) iter.Seq[T]
```

Usage:

```go
dec := encoding.NewDecoder(reader)
defer dec.Close()

for entry := range encoding.PackItems[Entry](dec) {
    process(entry)
}
```

### How it works

1. Caller begins iterating. `PackItems` reads the pack header on first call.
2. Each iteration decodes one element directly into a caller-owned `T` value.
3. The `yield` callback returns false when the caller breaks out of the loop.
4. Pack termination (next statement, EOF, or NUL) ends the sequence naturally.

### Error handling

`iter.Seq[T]` has no error return. Options:

- **`iter.Seq2[T, error]`**: Each iteration returns `(value, error)`. Caller checks error in the loop body. Explicit but verbose.
- **Decoder-level error**: `PackItems` stores the first error on the `Decoder`. Caller checks `dec.Err()` after the loop. Cleaner loop body, matches `bufio.Scanner` pattern.
- **Panic/recover**: Iterator panics on error, `PackItems` wrapper recovers. Fragile — not recommended.

The `Decoder.Err()` pattern is the most ergonomic:

```go
for entry := range encoding.PackItems[Entry](dec) {
    process(entry)
}
if err := dec.Err(); err != nil {
    log.Fatal(err)
}
```

## Allocation profile

Current streaming benchmark (FS1K, 1000 struct entries):

| Source | % of allocs | With iterator |
|--------|-------------|---------------|
| `reflect.growslice` | 55% | Eliminated |
| `strings.Builder` | 30% | Unchanged |
| Struct field cache lookup | ~0% | Already cached |

Expected improvement: ~50% reduction in allocations for pack reads. The `strings.Builder` cost remains — addressing that would require a zero-copy string path (e.g., referencing the input buffer directly), which is a separate optimization.

## Chunked buffer alternative

Instead of (or in addition to) an iterator, a chunked buffer strategy could reduce growth cost for the existing slice-based API:

- Allocate fixed-size chunks (e.g., 256 elements) as linked segments.
- No copying during growth — just allocate a new chunk.
- Flatten to a contiguous `[]T` at the end (one exact-size allocation + copy).
- Trade: N chunk allocations + 1 final copy vs. ~25 grow+copy cycles.

This could be used internally by the reflect-based `Unmarshal` path while the iterator API serves the streaming use case.

## Relationship to existing APIs

| API | Use case | Allocation |
|-----|----------|------------|
| `Unmarshal(data, &v)` | Batch: entire unit into a struct | Full slice |
| `UnmarshalNext(&struct)` | Incremental: whole pack into struct field | Full slice |
| `UnmarshalNext(&scalar)` | Element-by-element: one value per call | Per-element only |
| `Decode()` | Event streaming: one event at a time | Per-event only |
| `PackItems[T](dec)` | **Proposed**: typed element iterator | Per-element only |

`PackItems` fills the gap between the low-level event API (`Decode`) and the high-level struct API (`Unmarshal`/`UnmarshalNext`) — typed, streaming, zero-slice-overhead.
