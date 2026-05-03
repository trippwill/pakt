# Borrowed Grammar Pipeline Design Summary

## Goal

Build a .NET 10 streaming parser abstraction that can read from:

- large files
- sockets / network streams
- in-memory byte buffers

The parser emits strongly typed grammar events without eagerly materializing strings. Event payloads borrow parser-owned memory and are valid only during synchronous consumption.

## Core Architecture

```text
File / Socket / Memory
        ↓
PipeReader
        ↓
GrammarReader
        ↓
BorrowedGrammarEvent callback
        ↓
Console writer / deserializer / validator
```

The design uses `System.IO.Pipelines` for byte ingestion and buffering. It does **not** use `Channel<TEvent>` for parsed events.

## Main Design Choice

The core parser API is **borrowed-only**.

```text
The parser emits borrowed events.
Borrowed payloads reference parser-owned buffered bytes.
An event is valid only during the consumer callback.
The consumer must copy or decode anything it wants to keep.
```

This keeps the low-level parser fast and allocation-conscious. Friendlier APIs should be implemented above this layer, especially in the deserializer.

## Why Not Channels?

`Channel<T>` is useful when there is a detached producer and consumer, but the chosen design is consumer-pull-driven:

```text
consumer asks for next event
parser reads as needed
parser emits one borrowed event
consumer handles it synchronously
```

A channel of borrowed events would be unsafe because borrowed events point into parser-owned memory. If background read-ahead is later needed, channels or queues should hold **owned raw byte blocks**, not parsed borrowed events.

## Why Not IAsyncEnumerable?

`IAsyncEnumerable<BorrowedGrammarEvent>` looks ergonomic, but it makes lifetime rules too implicit. Consumers may accidentally store events beyond their valid lifetime.

Instead, expose a callback-based API:

```csharp
public delegate GrammarReadAction BorrowedGrammarEventHandler(
    scoped in BorrowedGrammarEvent evt);

public ValueTask<bool> ReadAsync(
    BorrowedGrammarEventHandler handler,
    CancellationToken cancellationToken = default);

public ValueTask DrainAsync(
    BorrowedGrammarEventHandler handler,
    CancellationToken cancellationToken = default);
```

This makes the event lifetime explicit: the event is valid only inside the handler call.

## Ref Struct Event Model

Since `ReadOnlySpan<T>` is a `ref struct`, any type that stores it must also be a `ref struct`.

```csharp
public readonly ref struct BorrowedGrammarEvent
{
    public GrammarEventKind Kind { get; }
    public long Offset { get; }
    public ReadOnlySpan<byte> Text { get; }

    public BorrowedGrammarEvent(
        GrammarEventKind kind,
        long offset,
        ReadOnlySpan<byte> text)
    {
        Kind = kind;
        Offset = offset;
        Text = text;
    }

    public string DecodeUtf8()
        => Encoding.UTF8.GetString(Text);
}
```

The parser can await while reading bytes, then enter a synchronous span/ref-struct region:

```text
await PipeReader.ReadAsync
  parse using SequenceReader<byte>
  emit BorrowedGrammarEvent to synchronous handler
  AdvanceTo
await again later
```

The borrowed event must not cross an `await` boundary.

## Factory Abstraction

A single static factory can create a `GrammarReader` from common inputs:

```csharp
public static class GrammarPipeline
{
    public static GrammarReader FromFile(
        string path,
        GrammarReaderOptions? options = null);

    public static GrammarReader FromStream(
        Stream stream,
        bool leaveOpen = false,
        GrammarReaderOptions? options = null);

    public static GrammarReader FromSocket(
        Socket socket,
        bool ownsSocket = false,
        GrammarReaderOptions? options = null);

    public static GrammarReader FromBytes(
        ReadOnlyMemory<byte> bytes,
        GrammarReaderOptions? options = null);

    public static GrammarReader FromString(
        string text,
        Encoding? encoding = null,
        GrammarReaderOptions? options = null);
}
```

Files and sockets normalize to `PipeReader` via stream wrappers. In-memory bytes use `PipeReader.Create(ReadOnlyMemory<byte>)`.

## Reader Options

```csharp
public sealed class GrammarReaderOptions
{
    public static GrammarReaderOptions Default { get; } = new();

    public int PipeBufferSize { get; init; } = 64 * 1024;

    public int MaxBufferedBytes { get; init; } = 1024 * 1024;

    public int MaxTokenBytes { get; init; } = 256 * 1024;
}
```

`MaxBufferedBytes` prevents unbounded accumulation of unread input.

`MaxTokenBytes` bounds token size, which matters because span-backed borrowed events need controlled token memory.

## GrammarReader Shape

```csharp
public sealed class GrammarReader : IAsyncDisposable
{
    public ValueTask<bool> ReadAsync(
        BorrowedGrammarEventHandler handler,
        CancellationToken cancellationToken = default);

    public ValueTask DrainAsync(
        BorrowedGrammarEventHandler handler,
        CancellationToken cancellationToken = default);

    public ValueTask DisposeAsync();
}
```

`ReadAsync` reads and parses until it can emit one event, then returns `true`. It returns `false` on end-of-input or if the handler requests `Stop`.

`DrainAsync` is a convenience wrapper that repeatedly calls `ReadAsync`.

## Handler Contract

```csharp
public enum GrammarReadAction
{
    Continue,
    Stop
}

public delegate GrammarReadAction BorrowedGrammarEventHandler(
    scoped in BorrowedGrammarEvent evt);
```

Handlers are synchronous by design. They may decode/copy data into longer-lived structures, but they must not retain spans or borrowed events.

## Console Consumer Example

```csharp
await using var reader = GrammarPipeline.FromFile("input.txt");

await reader.DrainAsync(static evt =>
{
    switch (evt.Kind)
    {
        case GrammarEventKind.StartObject:
            Console.Write("start object: ");
            Console.WriteLine(evt.DecodeUtf8());
            break;

        case GrammarEventKind.Value:
            Console.Write("value: ");
            Console.WriteLine(evt.DecodeUtf8());
            break;
    }

    return GrammarReadAction.Continue;
});
```

## Deserializer Layer

Most users should use a deserializer layer, not the borrowed parser API directly.

```csharp
public sealed class SimpleGrammarDeserializer
{
    public async ValueTask<SimpleDocument> DeserializeAsync(
        GrammarReader reader,
        CancellationToken cancellationToken = default)
    {
        var document = new SimpleDocument();
        SimpleNode? current = null;

        await reader.DrainAsync(evt =>
        {
            switch (evt.Kind)
            {
                case GrammarEventKind.StartObject:
                    current = new SimpleNode
                    {
                        Name = evt.DecodeUtf8()
                    };
                    document.Nodes.Add(current);
                    break;

                case GrammarEventKind.Value:
                    if (current is null)
                        throw new InvalidDataException("Value outside object.");

                    current.Values.Add(evt.DecodeUtf8());
                    break;

                case GrammarEventKind.EndObject:
                    current = null;
                    break;
            }

            return GrammarReadAction.Continue;
        }, cancellationToken).ConfigureAwait(false);

        return document;
    }
}
```

The deserializer owns copying, decoding, validation, and user-friendly error reporting.

## Read-Ahead Strategy

There are three possible levels.

### Level 1: Pull-only

```text
consumer asks
reader reads enough
parser emits one event
consumer handles it
```

This is the simplest model.

### Level 2: PipeReader buffering

```text
consumer asks
PipeReader may already have extra bytes buffered
parser consumes event from buffered bytes
```

Still pull-driven and simple.

### Level 3: Background raw read-ahead

```text
background task drains socket into bounded raw buffer
consumer pulls parser events from that raw buffer
```

Only add this if sockets need to be drained while consumers are slow. If added, queue owned raw byte blocks, not borrowed parsed events.

## Important Token Policy

A span-backed borrowed event requires a contiguous payload. For tokens split across pipe segments, choose a policy:

| Policy | Result |
|---|---|
| Reject split or oversized token | simplest, strict |
| Compact token into parser-owned scratch buffer | best practical option |
| Expose `ReadOnlySequence<byte>` | supports multi-segment payloads but weakens lifetime safety |

Recommended policy: compact split tokens into parser-owned scratch memory up to `MaxTokenBytes`, then expose a span over that scratch buffer.

## Final Boundary

Core package:

```text
SimpleGrammar.Core
  BorrowedGrammarEvent
  GrammarEventKind
  GrammarReadAction
  BorrowedGrammarEventHandler
  GrammarReader
  GrammarReaderOptions
  GrammarPipeline
  GrammarParser
```

Deserializer package:

```text
SimpleGrammar.Serialization
  domain model builders
  validation
  user-friendly errors
  DeserializeAsync APIs
```

## Final Rule Set

```text
Core parser:
  borrowed only
  callback only
  synchronous handlers
  no IAsyncEnumerable
  no Channel<GrammarEvent>
  no eager string materialization

Deserializer:
  copies/decodes when needed
  exposes friendly async APIs
  hides borrowed lifetime rules
```
