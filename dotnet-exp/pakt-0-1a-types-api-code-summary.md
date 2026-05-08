# PAKT 0.1a Implementation Summary

PAKT 0.1a has landed in a sharp, implementation-friendly direction:

```text
layout-only syntax
type-directed parsing
state-machine parser
explicit frame stack
borrowed ref-struct events
synchronous event callbacks
friendly deserializer layer above the parser
```

The design target is a streaming, self-describing, type-directed, lossless data format where grammar constructs map directly to structural events.

---

# 1. Syntax snapshot

## 1.1 Layout-only members

No commas:

```pakt
name:str = 'midwatch'
version:(int int int) = (1 0 0)

env:|dev staging prod| = |dev

ports:[int] = [8080 8081 8082]
```

Structs are positional values matched against declared fields:

```pakt
deploy:{level:str release:int active:bool} = {
  'platform'
  26
  true
}
```

Equivalent inline form:

```pakt
deploy:{level:str release:int active:bool} = { 'platform' 26 true }
```

## 1.2 Map binding with `=>`

Map type:

```pakt
headers:<str => str>
```

Map value:

```pakt
headers:<str => str> = <
  'content-type' => 'application/json'
  'accept' => 'application/json'
>
```

Composite key:

```pakt
rates:<(str date) => dec> = <
  ('GBP' 2026-05-01) => 1.25
  ('EUR' 2026-05-01) => 1.08
>
```

## 1.3 Packs

A pack is a named, typed, open-ended stream of values:

```pakt
events:[{ts:ts level:|info warn error| msg:str}] <<
{ 2026-06-01T14:30:00Z |info 'server started' }
{ 2026-06-01T14:31:00Z |warn 'high latency' }
```

For map packs:

```pakt
headers:<str => str> <<
'content-type' => 'application/json'
'accept' => 'application/json'
```

A pack ends at EOF, NUL framing, or the start of the next top-level statement.

## 1.4 Statement headers

Statement header must be one physical line:

```pakt
name:str = 'midwatch'
events:[event] <<
```

Invalid:

```pakt
name
:str = 'midwatch'
```

Invalid:

```pakt
events:[event]
<<
```

This makes the root parser and pack-boundary detection cleaner.

## 1.5 Strings

Only single-quoted string forms:

```pakt
name:str = 'midwatch'
path:str = r'C:\Users\alice'
query:str = '''
SELECT id, name
FROM users
'''
raw:str = r'''
C:\Users\alice\nothing-is-escaped
'''
```

Dropped:

```pakt
"double quoted"
"""triple double quoted"""
```

Multiline indentation stripping is not parser behavior. The parser preserves raw multiline content; the deserializer or consumer may normalize indentation later.

---

# 2. Type model

## 2.1 Public type kinds

```csharp
public enum PaktTypeKind
{
    String,
    Int,
    Decimal,
    Float,
    Bool,
    Uuid,
    Date,
    Timestamp,
    Binary,

    Nullable,
    AtomSet,
    Struct,
    Tuple,
    List,
    Map,

    // Future:
    Alias
}
```

## 2.2 Type references

The parser should not copy rich type graphs everywhere. Use compact references into a type arena:

```csharp
public readonly struct PaktTypeRef
{
    public int Id { get; }

    public PaktTypeRef(int id)
    {
        Id = id;
    }

    public bool IsDefault => Id == 0;
}
```

Internal arena:

```csharp
internal sealed class PaktTypeArena
{
    private readonly List<PaktTypeNode> _nodes = [];

    public PaktTypeRef Add(PaktTypeNode node)
    {
        _nodes.Add(node);
        return new PaktTypeRef(_nodes.Count - 1);
    }

    public ref readonly PaktTypeNode Get(PaktTypeRef type)
        => ref CollectionsMarshal.AsSpan(_nodes)[type.Id];
}
```

Node shape:

```csharp
internal readonly struct PaktTypeNode
{
    public PaktTypeKind Kind { get; init; }

    // Nullable
    public PaktTypeRef InnerType { get; init; }

    // List
    public PaktTypeRef ElementType { get; init; }

    // Map
    public PaktTypeRef KeyType { get; init; }
    public PaktTypeRef ValueType { get; init; }

    // Struct / tuple / atom-set payloads are indexed into side tables.
    public int FirstChild { get; init; }
    public int ChildCount { get; init; }
}
```

Struct field descriptor:

```csharp
internal readonly struct PaktField
{
    public ReadOnlyMemory<byte> NameUtf8 { get; init; }
    public PaktTypeRef Type { get; init; }
}
```

Atom descriptor:

```csharp
internal readonly struct PaktAtom
{
    public ReadOnlyMemory<byte> NameUtf8 { get; init; }
}
```

## 2.3 Scalar type handling

Scalars are parsed using the expected type, not global token guessing.

```csharp
internal enum PaktScalarKind
{
    String,
    Int,
    Decimal,
    Float,
    Bool,
    Uuid,
    Date,
    Timestamp,
    Binary
}
```

Suggested scalar payload:

```csharp
public readonly ref struct BorrowedPaktScalar
{
    public PaktScalarKind Kind { get; }
    public ReadOnlySpan<byte> Raw { get; }
    public ReadOnlySpan<byte> Body { get; }
    public bool RequiresUnescape { get; }
    public bool IsRawString { get; }
    public bool IsMultilineString { get; }

    public BorrowedPaktScalar(
        PaktScalarKind kind,
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> body,
        bool requiresUnescape = false,
        bool isRawString = false,
        bool isMultilineString = false)
    {
        Kind = kind;
        Raw = raw;
        Body = body;
        RequiresUnescape = requiresUnescape;
        IsRawString = isRawString;
        IsMultilineString = isMultilineString;
    }
}
```

Deserializer-level parsing examples:

```csharp
public static long ParseInt64(in BorrowedPaktScalar scalar)
{
    if (scalar.Kind != PaktScalarKind.Int)
        throw new InvalidOperationException("Expected int scalar.");

    return Utf8Parser.TryParse(scalar.Raw, out long value, out int consumed)
        && consumed == scalar.Raw.Length
        ? value
        : throw new FormatException("Invalid int.");
}
```

```csharp
public static string MaterializeString(in BorrowedPaktScalar scalar)
{
    if (scalar.Kind != PaktScalarKind.String)
        throw new InvalidOperationException("Expected string scalar.");

    if (!scalar.RequiresUnescape)
        return Encoding.UTF8.GetString(scalar.Body);

    return PaktStringEscaper.UnescapeUtf8(scalar.Body);
}
```

---

# 3. Public API layers

## 3.1 Layering

```text
Pakt.Core
  borrowed event reader
  parser options
  event kinds
  error model
  low-level scalar helpers

Pakt.Serialization
  deserializer
  materialization policies
  duplicate handling
  multiline normalization
  object binding

Pakt.Formatting
  optional formatter / pretty-printer later
```

Most users should use `Pakt.Serialization`.

The low-level parser is intentionally sharp.

---

# 4. Input API

Normalize file, stream, socket, and memory inputs into the same reader abstraction.

```csharp
public static class PaktPipeline
{
    public static PaktReader FromFile(
        string path,
        PaktReaderOptions? options = null)
    {
        var stream = File.OpenRead(path);

        var pipe = PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(
                bufferSize: options?.PipeBufferSize ?? 64 * 1024,
                leaveOpen: false));

        return new PaktReader(pipe, options ?? PaktReaderOptions.Default);
    }

    public static PaktReader FromStream(
        Stream stream,
        bool leaveOpen = false,
        PaktReaderOptions? options = null)
    {
        var pipe = PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(
                bufferSize: options?.PipeBufferSize ?? 64 * 1024,
                leaveOpen: leaveOpen));

        return new PaktReader(pipe, options ?? PaktReaderOptions.Default);
    }

    public static PaktReader FromSocket(
        Socket socket,
        bool ownsSocket = false,
        PaktReaderOptions? options = null)
    {
        var stream = new NetworkStream(socket, ownsSocket);
        return FromStream(stream, leaveOpen: false, options);
    }

    public static PaktReader FromBytes(
        ReadOnlyMemory<byte> bytes,
        PaktReaderOptions? options = null)
    {
        var pipe = PipeReader.Create(bytes);
        return new PaktReader(pipe, options ?? PaktReaderOptions.Default);
    }

    public static PaktReader FromString(
        string text,
        PaktReaderOptions? options = null)
    {
        return FromBytes(Encoding.UTF8.GetBytes(text), options);
    }
}
```

Options:

```csharp
public sealed class PaktReaderOptions
{
    public static PaktReaderOptions Default { get; } = new();

    public int PipeBufferSize { get; init; } = 64 * 1024;
    public int MaxBufferedBytes { get; init; } = 1024 * 1024;
    public int MaxTokenBytes { get; init; } = 1024 * 1024;
    public int MaxNestingDepth { get; init; } = 128;

    public bool AllowBom { get; init; } = true;
    public bool AllowNulFraming { get; init; } = true;
}
```

---

# 5. Borrowed event API

## 5.1 Event lifetime

Events are borrowed. Their spans point into parser-owned memory.

```text
valid only during callback
do not store
do not await with event in scope
copy/decode anything needed later
```

## 5.2 Event handler

```csharp
public enum PaktReadAction
{
    Continue,
    Stop
}

public delegate PaktReadAction BorrowedPaktEventHandler(
    scoped in BorrowedPaktEvent evt);
```

## 5.3 Event type

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

```csharp
public readonly ref struct BorrowedPaktEvent
{
    public PaktEventKind Kind { get; }
    public SourcePosition Position { get; }
    public PaktTypeRef Type { get; }

    // For names, atoms, raw scalar tokens, string bodies, etc.
    public ReadOnlySpan<byte> Raw { get; }
    public ReadOnlySpan<byte> Body { get; }

    // For struct/tuple/list indexes.
    public int Index { get; }

    // Scalar-specific flags.
    public PaktScalarKind ScalarKind { get; }
    public bool RequiresUnescape { get; }
    public bool IsRawString { get; }
    public bool IsMultilineString { get; }

    public BorrowedPaktEvent(
        PaktEventKind kind,
        SourcePosition position,
        PaktTypeRef type,
        ReadOnlySpan<byte> raw = default,
        ReadOnlySpan<byte> body = default,
        int index = -1,
        PaktScalarKind scalarKind = default,
        bool requiresUnescape = false,
        bool isRawString = false,
        bool isMultilineString = false)
    {
        Kind = kind;
        Position = position;
        Type = type;
        Raw = raw;
        Body = body;
        Index = index;
        ScalarKind = scalarKind;
        RequiresUnescape = requiresUnescape;
        IsRawString = isRawString;
        IsMultilineString = isMultilineString;
    }

    public string DecodeBodyUtf8()
        => Encoding.UTF8.GetString(Body);

    public string DecodeRawUtf8()
        => Encoding.UTF8.GetString(Raw);
}
```

## 5.4 Reader

```csharp
public sealed class PaktReader : IAsyncDisposable
{
    private readonly PipeReader _pipe;
    private readonly PaktReaderOptions _options;
    private readonly PaktParser _parser;

    internal PaktReader(PipeReader pipe, PaktReaderOptions options)
    {
        _pipe = pipe;
        _options = options;
        _parser = new PaktParser(options);
    }

    public async ValueTask<bool> ReadAsync(
        BorrowedPaktEventHandler handler,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult read = await _pipe.ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            ReadOnlySequence<byte> buffer = read.Buffer;

            if (buffer.Length > _options.MaxBufferedBytes)
                throw PaktParseException.BufferedBytesExceeded(_parser.Position);

            var sequenceReader = new SequenceReader<byte>(buffer);

            ParseStepResult result = _parser.Step(
                ref sequenceReader,
                read.IsCompleted);

            _pipe.AdvanceTo(sequenceReader.Position, buffer.End);

            switch (result.Status)
            {
                case ParseStepStatus.Event:
                    return handler(in result.Event) == PaktReadAction.Continue;

                case ParseStepStatus.NeedMoreData:
                    if (read.IsCompleted)
                        throw PaktParseException.UnexpectedEof(_parser.Position);

                    continue;

                case ParseStepStatus.Complete:
                    return false;

                case ParseStepStatus.Error:
                    throw result.Error.ToException();

                default:
                    throw new InvalidOperationException("Unknown parse status.");
            }
        }
    }

    public async ValueTask DrainAsync(
        BorrowedPaktEventHandler handler,
        CancellationToken cancellationToken = default)
    {
        while (await ReadAsync(handler, cancellationToken).ConfigureAwait(false))
        {
        }
    }

    public ValueTask DisposeAsync()
        => _pipe.CompleteAsync();
}
```

---

# 6. Internal parser types

## 6.1 Parse statuses

```csharp
internal enum ParseStepStatus
{
    Event,
    NeedMoreData,
    Complete,
    Error
}
```

```csharp
internal readonly ref struct ParseStepResult
{
    public ParseStepStatus Status { get; }
    public BorrowedPaktEvent Event { get; }
    public PaktParseError Error { get; }

    private ParseStepResult(
        ParseStepStatus status,
        BorrowedPaktEvent evt,
        PaktParseError error)
    {
        Status = status;
        Event = evt;
        Error = error;
    }

    public static ParseStepResult FromEvent(BorrowedPaktEvent evt)
        => new(ParseStepStatus.Event, evt, default);

    public static ParseStepResult NeedMoreData()
        => new(ParseStepStatus.NeedMoreData, default, default);

    public static ParseStepResult Complete()
        => new(ParseStepStatus.Complete, default, default);

    public static ParseStepResult FromError(PaktParseError error)
        => new(ParseStepStatus.Error, default, error);
}
```

## 6.2 Parser state

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

## 6.3 Frame stack

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
```

```csharp
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
```

```csharp
internal struct PaktFrame
{
    public FrameKind Kind;
    public FramePhase Phase;
    public PaktTypeRef Type;
    public int Index;

    // Map handling.
    public bool ReadingMapKey;

    // Root/pack context.
    public PaktTypeRef PackElementType;
    public PaktTypeRef MapKeyType;
    public PaktTypeRef MapValueType;
}
```

Frame invariant:

```text
BeforeFirst  -> optional layout, close delimiter, or first item
AfterItem    -> close delimiter or required layout
AfterLayout  -> additional layout, close delimiter, or next item
```

## 6.4 Source position

```csharp
public readonly struct SourcePosition
{
    public long Offset { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }

    public override string ToString()
        => $"{Line}:{Column}";
}
```

Mutable tracker:

```csharp
internal struct SourceCursor
{
    public long Offset;
    public int Line;
    public int Column;

    public void Advance(byte b)
    {
        Offset++;

        if (b == (byte)'\n')
        {
            Line++;
            Column = 1;
        }
        else
        {
            Column++;
        }
    }

    public SourcePosition Position => new()
    {
        Offset = Offset,
        Line = Line,
        Column = Column
    };
}
```

---

# 7. Error API

Parse errors should include code, identifier, position, and a human-readable message.

```csharp
public enum PaktErrorCode
{
    UnexpectedEof = 1,
    TypeMismatch = 2,
    NilNonNullable = 3,
    Syntax = 4,
    MissingLayout = 5,
    ReservedToken = 6,
    InvalidHeader = 7,
    ArityMismatch = 8,

    // Implementation-defined.
    NestingDepthExceeded = 100,
    TokenLengthExceeded = 101,
    BufferedBytesExceeded = 102
}
```

```csharp
public sealed class PaktParseException : Exception
{
    public PaktErrorCode Code { get; }
    public string Identifier { get; }
    public SourcePosition Position { get; }

    public PaktParseException(
        PaktErrorCode code,
        string identifier,
        SourcePosition position,
        string message)
        : base(message)
    {
        Code = code;
        Identifier = identifier;
        Position = position;
    }

    public static PaktParseException UnexpectedEof(SourcePosition position)
        => new(
            PaktErrorCode.UnexpectedEof,
            "unexpected_eof",
            position,
            "Input ended before the current syntactic construct was complete.");

    public static PaktParseException BufferedBytesExceeded(SourcePosition position)
        => new(
            PaktErrorCode.BufferedBytesExceeded,
            "buffered_bytes_exceeded",
            position,
            "Buffered input exceeded the configured maximum.");
}
```

Internal error payload:

```csharp
internal readonly struct PaktParseError
{
    public PaktErrorCode Code { get; init; }
    public string Identifier { get; init; }
    public SourcePosition Position { get; init; }
    public string Message { get; init; }

    public PaktParseException ToException()
        => new(Code, Identifier, Position, Message);
}
```

---

# 8. Scanner API

The scanner should avoid building a token stream. It should provide targeted, context-aware methods.

```csharp
internal ref struct PaktScanner
{
    private SequenceReader<byte> _reader;
    private SourceCursor _cursor;

    public PaktScanner(
        SequenceReader<byte> reader,
        SourceCursor cursor)
    {
        _reader = reader;
        _cursor = cursor;
    }

    public bool TryReadIdentifier(out ReadOnlySpan<byte> ident);
    public bool TryReadLayout();
    public bool TryReadRequiredLayout();
    public bool TryReadColon();
    public bool TryReadAssign();
    public bool TryReadPack();
    public bool TryReadBind();
    public bool TryReadOpenDelimiter(out PaktDelimiter delimiter);
    public bool TryReadCloseDelimiter(PaktDelimiter delimiter);

    public bool TryReadString(out BorrowedPaktScalar scalar);
    public bool TryReadRawString(out BorrowedPaktScalar scalar);
    public bool TryReadMultilineString(out BorrowedPaktScalar scalar);
    public bool TryReadAtom(out ReadOnlySpan<byte> atom);

    public bool TryReadScalarByType(
        PaktTypeRef expectedType,
        PaktTypeArena types,
        out BorrowedPaktScalar scalar);
}
```

Delimiter enum:

```csharp
internal enum PaktDelimiter
{
    Brace,
    Paren,
    Bracket,
    Angle,
    Pipe
}
```

Operator matching should use longest match:

```text
<<
=>
=
<
>
```

Context decides which operator is legal.

---

# 9. Layout parsing

## 9.1 No global whitespace skip

Do not blindly call `SkipWhitespace()` everywhere.

Use context-specific layout functions:

```csharp
internal enum LayoutResult
{
    None,
    Consumed,
    NeedMoreData
}
```

```csharp
internal LayoutResult TryConsumeLayout(
    ref SequenceReader<byte> reader,
    bool allowNewlines = true)
{
    var consumed = false;

    while (!reader.End)
    {
        if (reader.TryPeek(out byte b))
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n')
            {
                reader.Advance(1);
                consumed = true;
                continue;
            }

            if (b == (byte)'#')
            {
                // consume comment but not newline
                reader.Advance(1);
                consumed = true;

                while (!reader.End &&
                       reader.TryPeek(out byte c) &&
                       c != (byte)'\n')
                {
                    reader.Advance(1);
                }

                continue;
            }
        }

        break;
    }

    return consumed ? LayoutResult.Consumed : LayoutResult.None;
}
```

## 9.2 Composite frame handling

Pseudocode:

```csharp
private ParseStepResult StepComposite(
    ref SequenceReader<byte> reader,
    bool isFinalBlock)
{
    ref var frame = ref _frames.Peek();

    switch (frame.Phase)
    {
        case FramePhase.BeforeFirst:
            ConsumeOptionalLayout(ref reader);

            if (TryReadCloseForFrame(ref reader, frame))
                return EmitCompositeEnd(frame);

            return ParseNextItem(ref reader, frame);

        case FramePhase.AfterItem:
            if (TryReadCloseForFrame(ref reader, frame))
                return EmitCompositeEnd(frame);

            if (!TryConsumeRequiredLayout(ref reader))
                return Error("missing_layout");

            frame.Phase = FramePhase.AfterLayout;
            return Continue();

        case FramePhase.AfterLayout:
            ConsumeOptionalLayout(ref reader);

            if (TryReadCloseForFrame(ref reader, frame))
                return EmitCompositeEnd(frame);

            return ParseNextItem(ref reader, frame);

        default:
            return Error("syntax");
    }
}
```

This is the core simplification from layout-only syntax.

---

# 10. Low-level usage samples

## 10.1 Console event dump

```csharp
await using var reader = PaktPipeline.FromFile("input.pakt");

await reader.DrainAsync(static evt =>
{
    switch (evt.Kind)
    {
        case PaktEventKind.AssignStart:
            Console.Write("assign ");
            Console.WriteLine(Encoding.UTF8.GetString(evt.Body));
            break;

        case PaktEventKind.PackStart:
            Console.Write("pack ");
            Console.WriteLine(Encoding.UTF8.GetString(evt.Body));
            break;

        case PaktEventKind.Scalar:
            Console.Write("scalar ");
            Console.WriteLine(Encoding.UTF8.GetString(evt.Raw));
            break;

        case PaktEventKind.Atom:
            Console.Write("atom ");
            Console.WriteLine(Encoding.UTF8.GetString(evt.Body));
            break;
    }

    return PaktReadAction.Continue;
});
```

## 10.2 Stop after first matching statement

```csharp
string? found = null;

await using var reader = PaktPipeline.FromFile("input.pakt");

await reader.DrainAsync(evt =>
{
    if (evt.Kind == PaktEventKind.AssignStart &&
        evt.Body.SequenceEqual("name"u8))
    {
        found = evt.DecodeBodyUtf8();
        return PaktReadAction.Stop;
    }

    return PaktReadAction.Continue;
});
```

Note: this only captures the statement name. A real value extractor would keep a small state machine and capture the following scalar.

## 10.3 Read from memory

```csharp
ReadOnlyMemory<byte> bytes = """
name:str = 'midwatch'
version:(int int int) = (1 0 0)
"""u8.ToArray();

await using var reader = PaktPipeline.FromBytes(bytes);

await reader.DrainAsync(static evt =>
{
    Console.WriteLine(evt.Kind);
    return PaktReadAction.Continue;
});
```

## 10.4 Read from socket

```csharp
using var socket = new Socket(
    AddressFamily.InterNetwork,
    SocketType.Stream,
    ProtocolType.Tcp);

await socket.ConnectAsync("example.com", 9000, cancellationToken);

await using var reader = PaktPipeline.FromSocket(
    socket,
    ownsSocket: true,
    new PaktReaderOptions
    {
        MaxBufferedBytes = 4 * 1024 * 1024,
        MaxTokenBytes = 1024 * 1024,
        MaxNestingDepth = 128
    });

await reader.DrainAsync(static evt =>
{
    // Borrowed event. Consume synchronously.
    return PaktReadAction.Continue;
}, cancellationToken);
```

---

# 11. Deserializer layer

## 11.1 User-facing API

```csharp
public sealed class PaktDeserializerOptions
{
    public DuplicatePolicy RootDuplicatePolicy { get; init; } = DuplicatePolicy.Preserve;
    public DuplicatePolicy MapDuplicatePolicy { get; init; } = DuplicatePolicy.Preserve;
    public MultilineStringPolicy MultilineStringPolicy { get; init; } = MultilineStringPolicy.Preserve;
}

public enum DuplicatePolicy
{
    Preserve,
    Reject,
    FirstWins,
    LastWins,
    Accumulate
}

public enum MultilineStringPolicy
{
    Preserve,
    TrimOuterBlankLines,
    StripCommonIndent,
    StripFirstContentLineIndent
}
```

```csharp
public sealed class PaktDeserializer<T>
{
    private readonly PaktDeserializerOptions _options;

    public PaktDeserializer(PaktDeserializerOptions? options = null)
    {
        _options = options ?? new PaktDeserializerOptions();
    }

    public async ValueTask<T> DeserializeAsync(
        PaktReader reader,
        CancellationToken cancellationToken = default)
    {
        var builder = new PaktObjectBuilder<T>(_options);

        await reader.DrainAsync(evt =>
        {
            builder.Accept(in evt);
            return PaktReadAction.Continue;
        }, cancellationToken).ConfigureAwait(false);

        return builder.Build();
    }
}
```

## 11.2 Strongly typed model example

PAKT:

```pakt
name:str = 'midwatch'
version:(int int int) = (1 0 0)

headers:<str => str> = <
  'content-type' => 'application/json'
  'accept' => 'application/json'
>
```

C# model:

```csharp
public sealed record Manifest(
    string Name,
    VersionTriple Version,
    IReadOnlyDictionary<string, string> Headers);

public readonly record struct VersionTriple(
    int Major,
    int Minor,
    int Patch);
```

Deserializer usage:

```csharp
await using var reader = PaktPipeline.FromFile("manifest.pakt");

var deserializer = new PaktDeserializer<Manifest>(
    new PaktDeserializerOptions
    {
        RootDuplicatePolicy = DuplicatePolicy.Reject,
        MapDuplicatePolicy = DuplicatePolicy.LastWins,
        MultilineStringPolicy = MultilineStringPolicy.Preserve
    });

Manifest manifest = await deserializer.DeserializeAsync(reader, cancellationToken);
```

## 11.3 Hand-written deserializer sketch

```csharp
public sealed class ManifestReader
{
    public async ValueTask<Manifest> ReadAsync(
        PaktReader reader,
        CancellationToken cancellationToken = default)
    {
        string? name = null;
        VersionTriple? version = null;
        Dictionary<string, string>? headers = null;

        string? currentStatement = null;
        string? pendingMapKey = null;

        await reader.DrainAsync(evt =>
        {
            switch (evt.Kind)
            {
                case PaktEventKind.AssignStart:
                    currentStatement = evt.DecodeBodyUtf8();
                    break;

                case PaktEventKind.Scalar:
                    if (currentStatement == "name")
                    {
                        name = MaterializeString(in evt);
                    }
                    break;

                case PaktEventKind.TupleStart:
                    // version tuple starts
                    break;

                case PaktEventKind.MapStart:
                    if (currentStatement == "headers")
                        headers = new Dictionary<string, string>();
                    break;

                case PaktEventKind.MapKey:
                    pendingMapKey = evt.DecodeBodyUtf8();
                    break;

                case PaktEventKind.MapValue:
                    if (headers is not null && pendingMapKey is not null)
                    {
                        headers[pendingMapKey] = evt.DecodeBodyUtf8();
                        pendingMapKey = null;
                    }
                    break;

                case PaktEventKind.AssignEnd:
                    currentStatement = null;
                    break;
            }

            return PaktReadAction.Continue;
        }, cancellationToken);

        return new Manifest(
            name ?? throw new InvalidDataException("Missing name."),
            version ?? throw new InvalidDataException("Missing version."),
            headers ?? new Dictionary<string, string>());
    }

    private static string MaterializeString(scoped in BorrowedPaktEvent evt)
    {
        if (!evt.RequiresUnescape)
            return Encoding.UTF8.GetString(evt.Body);

        return PaktStringEscaper.UnescapeUtf8(evt.Body);
    }
}
```

---

# 12. Parser implementation samples

## 12.1 Parser entry

```csharp
internal sealed class PaktParser
{
    private readonly PaktReaderOptions _options;
    private readonly PaktTypeArena _types = new();
    private readonly Stack<PaktFrame> _frames = new();

    private ParserState _state = ParserState.StartUnit;
    private SourceCursor _cursor = new() { Line = 1, Column = 1 };

    public SourcePosition Position => _cursor.Position;

    public PaktParser(PaktReaderOptions options)
    {
        _options = options;
    }

    public ParseStepResult Step(
        ref SequenceReader<byte> reader,
        bool isFinalBlock)
    {
        while (true)
        {
            return _state switch
            {
                ParserState.StartUnit =>
                    StepStartUnit(ref reader, isFinalBlock),

                ParserState.BeforeStatement =>
                    StepBeforeStatement(ref reader, isFinalBlock),

                ParserState.StatementName =>
                    StepStatementName(ref reader, isFinalBlock),

                ParserState.StatementType =>
                    StepStatementType(ref reader, isFinalBlock),

                ParserState.StatementOperator =>
                    StepStatementOperator(ref reader, isFinalBlock),

                ParserState.AssignValue =>
                    StepAssignValue(ref reader, isFinalBlock),

                ParserState.PackBody =>
                    StepPackBody(ref reader, isFinalBlock),

                ParserState.Complete =>
                    ParseStepResult.Complete(),

                _ =>
                    Error("syntax", "Invalid parser state.")
            };
        }
    }
}
```

## 12.2 Statement header enforcement

```csharp
private ParseStepResult StepStatementName(
    ref SequenceReader<byte> reader,
    bool isFinalBlock)
{
    var start = _cursor.Position;

    if (!TryReadIdentifier(ref reader, out ReadOnlySpan<byte> name))
        return NeedMoreOrEof(isFinalBlock);

    if (ContainsNewlineSince(start))
    {
        return Error(
            "invalid_header",
            "Statement header cannot span multiple lines.");
    }

    if (!TryReadColon(ref reader))
    {
        return Error(
            "syntax",
            "Expected ':' after statement name.");
    }

    _currentStatementName = CopySmallIdentifier(name);
    _state = ParserState.StatementType;
    return Continue();
}
```

## 12.3 Map type parsing

```csharp
private TypeParseResult ParseMapType(
    ref SequenceReader<byte> reader)
{
    // Already consumed '<'.

    ConsumeOptionalLayout(ref reader);

    var key = ParseType(ref reader);
    if (!key.Success)
        return key;

    if (!TryConsumeRequiredLayout(ref reader))
        return TypeParseResult.Error("Expected layout before '=>'.");

    if (!TryReadBind(ref reader))
        return TypeParseResult.Error("Expected '=>' between map key type and value type.");

    if (!TryConsumeRequiredLayout(ref reader))
        return TypeParseResult.Error("Expected layout after '=>'.");

    var value = ParseType(ref reader);
    if (!value.Success)
        return value;

    ConsumeOptionalLayout(ref reader);

    if (!TryReadRightAngle(ref reader))
        return TypeParseResult.Error("Expected '>' after map type.");

    var mapType = _types.Add(new PaktTypeNode
    {
        Kind = PaktTypeKind.Map,
        KeyType = key.Type,
        ValueType = value.Type
    });

    return TypeParseResult.Success(mapType);
}
```

## 12.4 Map value frame

```csharp
private ParseStepResult StepMapValue(
    ref SequenceReader<byte> reader,
    bool isFinalBlock)
{
    ref var frame = ref _frames.Peek();

    switch (frame.Phase)
    {
        case FramePhase.BeforeFirst:
            ConsumeOptionalLayout(ref reader);

            if (TryReadRightAngle(ref reader))
                return EndMap();

            frame.Phase = FramePhase.ExpectKey;
            return Continue();

        case FramePhase.ExpectKey:
            return ParseValue(
                ref reader,
                frame.MapKeyType,
                nextPhase: FramePhase.ExpectBind);

        case FramePhase.ExpectBind:
            if (!TryConsumeRequiredLayout(ref reader))
                return Error("missing_layout", "Expected layout before '=>'.");

            if (!TryReadBind(ref reader))
                return Error("syntax", "Expected '=>' after map key.");

            if (!TryConsumeRequiredLayout(ref reader))
                return Error("missing_layout", "Expected layout after '=>'.");

            frame.Phase = FramePhase.ExpectValue;
            return Continue();

        case FramePhase.ExpectValue:
            return ParseValue(
                ref reader,
                frame.MapValueType,
                nextPhase: FramePhase.AfterItem);

        case FramePhase.AfterItem:
            if (TryReadRightAngle(ref reader))
                return EndMap();

            if (!TryConsumeRequiredLayout(ref reader))
                return Error("missing_layout", "Expected layout or '>' after map entry.");

            frame.Phase = FramePhase.AfterLayout;
            return Continue();

        case FramePhase.AfterLayout:
            ConsumeOptionalLayout(ref reader);

            if (TryReadRightAngle(ref reader))
                return EndMap();

            frame.Phase = FramePhase.ExpectKey;
            return Continue();

        default:
            return Error("syntax", "Invalid map frame phase.");
    }
}
```

---

# 13. Package surface proposal

## 13.1 `Pakt.Core`

```text
PaktReader
PaktPipeline
PaktReaderOptions

BorrowedPaktEvent
BorrowedPaktEventHandler
PaktReadAction
PaktEventKind

PaktTypeRef
PaktTypeKind

SourcePosition
PaktParseException
PaktErrorCode
```

## 13.2 `Pakt.Serialization`

```text
PaktDeserializer<T>
PaktDeserializerOptions

DuplicatePolicy
MultilineStringPolicy

PaktObjectBuilder<T>
PaktBindingException
```

## 13.3 `Pakt.Formatting` later

```text
PaktFormatter
PaktFormatterOptions
PaktCanonicalWriter
```

---

# 14. Practical implementation order

1. `PaktPipeline` and `PaktReader` over `PipeReader`.
2. Source position tracking.
3. Layout/comment scanner.
4. Statement header parser with one-line enforcement.
5. Type parser and type arena.
6. Scalar scanners by expected type.
7. Borrowed event shape.
8. Struct/tuple/list frames.
9. Map type/value handling with `=>`.
10. Pack body handling and next-statement boundary detection.
11. String and multiline string scanner.
12. Error codes and exception model.
13. Deserializer prototype.
14. Limits: max token, max nesting, max buffered bytes.
15. Optional raw-byte read-ahead for sockets.

---

# 15. Minimal end-to-end sample

Input:

```pakt
name:str = 'midwatch'
version:(int int int) = (1 0 0)

headers:<str => str> = <
  'content-type' => 'application/json'
  'accept' => 'application/json'
>

events:[{ts:ts level:|info warn error| msg:str}] <<
{ 2026-06-01T14:30:00Z |info 'server started' }
{ 2026-06-01T14:31:00Z |warn 'high latency' }
```

Event dump:

```csharp
await using var reader = PaktPipeline.FromFile("sample.pakt");

await reader.DrainAsync(static evt =>
{
    Console.Write(evt.Kind);

    if (!evt.Body.IsEmpty)
    {
        Console.Write(" ");
        Console.Write(Encoding.UTF8.GetString(evt.Body));
    }
    else if (!evt.Raw.IsEmpty)
    {
        Console.Write(" ");
        Console.Write(Encoding.UTF8.GetString(evt.Raw));
    }

    Console.WriteLine();
    return PaktReadAction.Continue;
});
```

Expected shape:

```text
AssignStart name
Scalar 'midwatch'
AssignEnd
AssignStart version
TupleStart
TupleItem
Scalar 1
TupleItem
Scalar 0
TupleItem
Scalar 0
TupleEnd
AssignEnd
AssignStart headers
MapStart
MapKey 'content-type'
MapValue 'application/json'
MapKey 'accept'
MapValue 'application/json'
MapEnd
AssignEnd
PackStart events
StructStart
StructField ts
Scalar 2026-06-01T14:30:00Z
StructField level
Atom info
StructField msg
Scalar 'server started'
StructEnd
StructStart
...
PackEnd
```

The exact event stream can be tuned, but the principle should hold: **structure is represented by event kinds, not inferred from punctuation or raw text**.

---

# 16. Final architecture position

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

This keeps PAKT aligned with its main constraints:

```text
streaming
type-directed parsing
minimal allocation
lossless decoding
clear separation between core syntax and consumer policy
```
