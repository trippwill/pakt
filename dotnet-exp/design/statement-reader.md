# PaktStatementReader — Streaming Statement Driver

> **Status**: Design sketch. Not yet implemented.

## Purpose

Forward-only, statement-by-statement cursor over a PAKT unit. Enables streaming consumption of large units without materializing everything into a POCO.

Use cases:
- Process a 1GB file with millions of streaming pack elements, constant memory
- Read only specific statements, skip the rest
- Stream collection elements via callback as they're parsed

## API Sketch

```csharp
public sealed class PaktStatementReader : IAsyncDisposable
{
    // Factory
    public static ValueTask<PaktStatementReader> CreateAsync(
        Stream stream, PaktSerializerContext context, CancellationToken ct = default);
    public static PaktStatementReader Create(
        ReadOnlyMemory<byte> data, PaktSerializerContext context);

    // Cursor
    public ValueTask<bool> ReadStatementAsync(CancellationToken ct = default);

    // Statement metadata (valid until next ReadStatementAsync)
    public ReadOnlySpan<byte> StatementName { get; }   // zero-alloc, compare with u8 literals
    public string StatementNameString { get; }          // convenience, allocates

    // Value consumption (exactly one per statement)
    public T ReadValue<T>();                            // scalar or struct
    public ValueTask ReadElementsAsync<T>(              // streaming collection
        Action<T> callback, CancellationToken ct = default);
    public ValueTask SkipValueAsync(CancellationToken ct = default);

    public ValueTask DisposeAsync();
}
```

## Usage

```csharp
await using var reader = await PaktStatementReader.CreateAsync(fileStream, context, ct);
while (await reader.ReadStatementAsync(ct))
{
    if (reader.StatementName.SequenceEqual("events"u8))
        await reader.ReadElementsAsync<Event>(ProcessEvent, ct);
    else if (reader.StatementName.SequenceEqual("name"u8))
        header.Name = reader.ReadValue<string>();
    else
        await reader.SkipValueAsync(ct);
}
```

## Design Decisions

- **`sealed class`**, not struct — mutable state (name buffer, value-consumed flag, pipe position) needs to survive across async boundaries without copy semantics issues.
- **`PaktPipeSource` internally** — same buffer management as `PaktSerializer.DeserializeAsync`. PipeReader handles compaction, pooling, consumed/examined tracking.
- **Context in constructor** — used internally for type resolution on `ReadValue<T>` and `ReadElementsAsync<T>`. Scalar types resolved directly via `PaktReader.Get*()` methods.
- **Byte-level `StatementName`** — `ReadOnlySpan<byte>` for hot-path matching against `u8` literals. Backed by a retained byte array, valid until the next `ReadStatementAsync`.
- **No source gen needed** — consumer does name matching manually. Element deserialization uses existing `PaktDeserializeFunc<T>` from the context.

## Implementation Notes

- Each operation (ReadStatementAsync, ReadValue, ReadElementsAsync, SkipValueAsync) creates a scoped `PaktReader`, reads tokens, then advances the pipe via `AdvanceAndRefillAsync`.
- The `PaktReader` is a `ref struct` — must be scoped in a block that ends before any `await`.
- `ReadElementsAsync` drives the refill loop for large collections: inner `while(reader.Read())` loop processes elements, outer loop refills at buffer boundaries.
- `ReadValue<T>` is sync — the value must fit in the current buffer. For large values, the pipe's minimum read size ensures enough data.
- Auto-skip: if `ReadStatementAsync` is called without consuming the previous value, it auto-skips.

## Key Challenge

The PaktReader's state machine phases must be at clean boundaries between operations. After `ReadStatementAsync` consumes `StatementName + TypeAnnotation + AssignOperator`, the saved `PaktReaderState` must leave the reader in a phase where the next `Read()` yields the value token. After `ReadValue` consumes the value, the state must be at `ExpectStatementOrEnd`. This phase alignment needs careful testing with the refill loop.

## Relationship to PaktSerializer

`PaktSerializer.DeserializeAsync<T>` (Model 1) materializes an entire unit into a POCO. `PaktStatementReader` (Model 2) gives fine-grained control. They share `PaktPipeSource` infrastructure.

| | PaktSerializer | PaktStatementReader |
|---|---|---|
| Output | Complete POCO | Per-statement values |
| Memory | All collections materialized | Elements streamed via callback |
| Source gen | Required (generated deserializers) | Optional (manual name matching) |
| Use case | Config files, API responses | Large datasets, streaming packs |
