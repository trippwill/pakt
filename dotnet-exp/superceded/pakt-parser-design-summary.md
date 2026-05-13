# PAKT 0.1a Parser Design Summary

This document summarizes the intended parser architecture for PAKT 0.1a.

The design target is a streaming, type-directed, fail-fast parser that emits borrowed structural events with minimal allocation.

## 1. Core Shape

```text
file / socket / in-memory bytes
        ↓
PipeReader
        ↓
state-machine parser
        ↓
explicit frame stack
        ↓
borrowed ref-struct events
        ↓
synchronous consumer callback
        ↓
deserializer / console writer / validator
```

The parser core should be sharp and low-level. Most users should interact with a friendlier deserializer layer that consumes borrowed events internally and materializes application objects.

## 2. Input Abstraction

Expose an abstraction that creates the same parser over multiple byte sources:

```csharp
public static class PaktPipeline
{
    public static PaktReader FromFile(string path, PaktReaderOptions? options = null);
    public static PaktReader FromStream(Stream stream, bool leaveOpen = false, PaktReaderOptions? options = null);
    public static PaktReader FromSocket(Socket socket, bool ownsSocket = false, PaktReaderOptions? options = null);
    public static PaktReader FromBytes(ReadOnlyMemory<byte> bytes, PaktReaderOptions? options = null);
}
```

Internally, all sources normalize to a `PipeReader`.

The parser should not know whether bytes came from a file, socket, pipe, memory buffer, test fixture, or future WASI host.

## 3. Public Reader Contract

The core API should be callback-based, not `IAsyncEnumerable<T>`.

```csharp
public sealed class PaktReader : IAsyncDisposable
{
    public ValueTask<bool> ReadAsync(
        BorrowedPaktEventHandler handler,
        CancellationToken cancellationToken = default);

    public ValueTask DrainAsync(
        BorrowedPaktEventHandler handler,
        CancellationToken cancellationToken = default);
}
```

The callback is synchronous:

```csharp
public delegate PaktReadAction BorrowedPaktEventHandler(
    scoped in BorrowedPaktEvent evt);

public enum PaktReadAction
{
    Continue,
    Stop
}
```

Rationale:

- Borrowed events may contain `ReadOnlySpan<byte>`.
- `ReadOnlySpan<T>` requires `ref struct` containment.
- `ref struct` event values must not cross async suspension points.
- The parser can `await` to receive bytes, then parse and call the consumer synchronously, then advance the pipe, then await again.

## 4. Borrowed Event Contract

A borrowed event is stack-only and valid only during the handler call.

```csharp
public readonly ref struct BorrowedPaktEvent
{
    public PaktEventKind Kind { get; }
    public long Offset { get; }
    public PaktTypeRef Type { get; }
    public ReadOnlySpan<byte> Raw { get; }
    public ReadOnlySpan<byte> Body { get; }
}
```

Rules:

- Do not store borrowed events.
- Do not store spans from borrowed events.
- Consumers must copy, decode, parse, or materialize anything they need to keep.
- The deserializer layer owns ordinary object materialization.

The low-level API should make this sharpness obvious rather than hiding it behind ordinary enumerable semantics.

## 5. Event Model

The grammar is the event model.

Suggested event kinds:

```csharp
public enum PaktEventKind
{
    UnitStart,
    UnitEnd,

    AssignStart,
    AssignEnd,

    PackStart,
    PackEnd,

    StructStart,
    StructField,
    StructEnd,

    TupleStart,
    TupleItem,
    TupleEnd,

    ListStart,
    ListItem,
    ListEnd,

    MapStart,
    MapKey,
    MapValue,
    MapEnd,

    Scalar,
    Nil,
    Atom
}
```

The consumer should not inspect punctuation to infer structure. The parser emits explicit structural events.

Example:

```pakt
deploy:{level:str release:int} = {
  'platform'
  26
}
```

Possible event stream:

```text
AssignStart(name=deploy, type={level:str release:int})
StructStart(type={level:str release:int})
StructField(name=level, index=0, type=str)
Scalar(type=str, raw='platform')
StructField(name=release, index=1, type=int)
Scalar(type=int, raw=26)
StructEnd
AssignEnd
```

## 6. Parser Strategy

Use:

```text
state machine
+ explicit frame stack
+ scanner helpers
+ type arena
```

Avoid:

```text
CLR stack recursion for nested values
event channels in the core parser
IAsyncEnumerable for borrowed events
building a full token stream first
building an AST in the core decoder
```

The parser should be deterministic, streaming, and fail-fast.

## 7. State Machine

Top-level state examples:

```csharp
internal enum ParserState
{
    StartUnit,
    BeforeStatement,
    StatementName,
    StatementType,
    StatementOperator,
    AssignValue,
    PackBody,
    AfterValue,
    Complete,
    Error
}
```

The state machine answers:

- what token or construct is expected next?
- can the parser emit an event now?
- does the parser need more bytes?
- did the unit complete?
- did the input violate the grammar?

## 8. Explicit Frame Stack

Frames track nested context and expected type.

```csharp
internal enum FrameKind
{
    Unit,
    Assign,
    PackList,
    PackMap,
    StructValue,
    TupleValue,
    ListValue,
    MapValue,
    TypeStruct,
    TypeTuple,
    TypeList,
    TypeMap,
    AtomSet
}

internal enum FramePhase
{
    BeforeFirst,
    AfterItem,
    AfterLayout,
    ExpectKey,
    ExpectBind,
    ExpectValue,
    Done
}

internal struct PaktFrame
{
    public FrameKind Kind;
    public FramePhase Phase;
    public PaktTypeRef Type;
    public int Index;
}
```

The frame stack answers:

- what nested construct are we inside?
- what type is expected here?
- what field/item/key/value phase are we in?
- what closing delimiter is legal?
- how many positional values have been seen?

This avoids stack overflow on deep inputs and allows implementation-defined nesting limits.

## 9. Type-Directed Parsing

Every value is parsed with an expected type.

The parser should not globally classify values first and validate later. It should call type-specific scanner routines:

```csharp
ReadIntLiteral(...)
ReadDecimalLiteral(...)
ReadFloatLiteral(...)
ReadTimestampLiteral(...)
ReadUuidLiteral(...)
ReadBinaryLiteral(...)
ReadStringLiteral(...)
ReadAtomValue(...)
```

Benefits:

- fewer ambiguous cases
- better errors
- no retroactive interpretation
- simpler scalar event payloads
- direct validation of type assertions

## 10. Layout-Only Parsing

PAKT 0.1a uses layout as the member separator.

There are no comma separators.

The parser should not have one global `SkipWhitespace()` that blindly consumes all layout. Instead, it should use context-specific layout handling.

Core invariant:

```text
layout separates complete syntactic items;
layout is not blindly skipped everywhere.
```

Frame handling pattern:

```text
BeforeFirst:
  consume optional layout
  close delimiter -> empty construct
  otherwise parse first item

AfterItem:
  close delimiter -> end construct
  layout -> separator
  otherwise error

AfterLayout:
  consume additional layout
  close delimiter -> end construct
  otherwise parse next item
```

Examples:

```pakt
[1 2 3]
```

After `1`, layout is required before `2`.

```pakt
[1]
```

After `1`, the close delimiter is valid without layout.

```pakt
[1# comment
2]
```

The comment plus newline participates in layout, so `2` is the next value.

## 11. Statement Header Rule

Statement headers must be one physical line.

Valid:

```pakt
name:str = 'midwatch'
events:[event] <<
```

Invalid:

```pakt
name
:str = 'midwatch'
```

```pakt
events:[event]
<<
```

This simplifies top-level parsing and pack-boundary detection.

The parser should produce a specific error for headers that span multiple lines, such as `invalid_header`.

## 12. Map Binding

Map binding uses `=>` in both type and value contexts.

Type:

```pakt
headers:<str => str>
```

Value:

```pakt
headers:<str => str> = <
  'content-type' => 'application/json'
  'accept' => 'application/json'
>
```

Scanner guidance:

- use longest-match for multi-character operators
- recognize `<<` and `=>`
- context determines which operator is legal
- `=` alone is only valid in statement assignment
- `=>` is only valid in map type and map value contexts

## 13. Strings

PAKT 0.1a keeps only single-quoted string forms:

```pakt
'text'
r'raw text'
'''multi-line'''
r'''raw multi-line'''
```

Double-quoted strings are reserved or rejected outside string content.

The parser should preserve raw multi-line string content. Indentation stripping is not a parser responsibility. It is a consumer/deserializer policy, similar to duplicate key handling.

For string events, expose metadata:

```csharp
public readonly ref struct BorrowedStringLiteral
{
    public ReadOnlySpan<byte> RawLiteral { get; }
    public ReadOnlySpan<byte> Body { get; }
    public bool IsRaw { get; }
    public bool IsMultiline { get; }
    public bool RequiresUnescape { get; }
}
```

The parser validates:

- closing delimiter exists
- invalid escapes in non-raw strings
- no NUL bytes
- no forbidden newline in single-line strings

The parser does not:

- strip indentation
- trim leading/trailing newlines
- materialize strings unless a consumer asks
- normalize presentation

## 14. Persistent Parser State Must Not Use Spans

Use spans only for current borrowed event payloads.

Do not store `ReadOnlySpan<byte>` in persistent parser state across calls or awaits.

Persistent state should use:

```text
enums
integers
type refs
byte offsets
source positions
arena references
small copied identifiers if needed
```

Current event payloads may use:

```text
ref struct
ReadOnlySpan<byte>
valid only during callback
```

## 15. Type Arena

Parsed type annotations should be stored in a compact type arena.

```csharp
internal readonly struct PaktTypeRef
{
    public int Id { get; }
}

internal sealed class TypeArena
{
    private readonly List<PaktType> _types = [];
}
```

Reasons:

- root statement type must guide value parsing
- pack element type must guide every pack item
- struct/tuple field types must guide positional values
- map key/value types must guide entries
- type aliases may be added later

## 16. Source Position and Errors

The parser should maintain source position as bytes are consumed.

```csharp
internal struct SourcePosition
{
    public long Offset;
    public int Line;
    public int Column;
}
```

Errors should be fail-fast and structured:

```csharp
public sealed class PaktParseException : Exception
{
    public int Code { get; }
    public string Identifier { get; }
    public SourcePosition Position { get; }
}
```

Expected core error categories:

- unexpected EOF
- syntax error
- type mismatch
- nil for non-nullable type
- missing layout
- reserved token
- invalid statement header
- arity mismatch
- nesting depth exceeded
- token length exceeded

## 17. Buffering and Read-Ahead

Initial implementation should rely on `PipeReader` buffering.

Options:

```csharp
public sealed class PaktReaderOptions
{
    public int PipeBufferSize { get; init; } = 64 * 1024;
    public int MaxBufferedBytes { get; init; } = 1024 * 1024;
    public int MaxTokenBytes { get; init; } = 1024 * 1024;
    public int MaxNestingDepth { get; init; } = 128;
}
```

True background socket read-ahead can be added later if needed:

```text
NetworkStream
  -> background raw-byte pump
  -> bounded raw byte buffer
  -> parser
  -> borrowed events
```

If added, queue raw byte blocks, not parsed borrowed events.

## 18. Why Not Channels for Events?

`Channel<T>` is not the right core abstraction for borrowed events.

Problems:

- borrowed events cannot safely outlive parser-owned buffers
- event queueing encourages storage beyond the event lifetime
- async consumers would blur ref-struct lifetime rules
- channels imply producer/consumer decoupling, but borrowed events require scoped consumption

Channels may be useful later for raw byte blocks, not for parsed borrowed events.

## 19. Deserializer Layer

The friendly API belongs above the parser.

Example:

```csharp
public sealed class PaktDeserializer<T>
{
    public ValueTask<T> DeserializeAsync(
        PaktReader reader,
        CancellationToken cancellationToken = default);
}
```

The deserializer:

- consumes borrowed events synchronously
- copies/decodes values it needs to keep
- applies duplicate-key policy
- applies multi-line string normalization policy
- builds application/domain objects
- exposes ordinary async APIs

Most users should use this layer.

## 20. Implementation Order

Suggested order:

1. Byte source abstraction over `PipeReader`.
2. Scanner for identifiers, layout, comments, delimiters, and operators.
3. Statement header parser with one-line enforcement.
4. Type parser and type arena.
5. Scalar scanner by expected type.
6. Composite value frames for tuple/list/struct/map.
7. Pack body state and pack termination.
8. Borrowed event emission.
9. Error model with source positions.
10. Deserializer prototype.
11. Multi-line string scanner.
12. Limits: max nesting, max token, max buffered bytes.
13. Optional raw-byte read-ahead experiment.

## 21. Final Architecture Position

Use:

```text
state machine
+ explicit frame stack
+ type arena
+ borrowed ref-struct events
+ synchronous callback
+ fail-fast errors
```

Do not use:

```text
recursive descent over the CLR stack
event channels
IAsyncEnumerable for borrowed payloads
AST construction in the core decoder
indentation stripping in the parser
```

This keeps PAKT aligned with its main constraints: streaming, type-directed parsing, minimal allocation, lossless decoding, and clear separation between core syntax and consumer policy.
