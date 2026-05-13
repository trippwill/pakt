using System.Buffers;
using System.Runtime.InteropServices;

namespace Pakt;

internal readonly ref struct TypeStepResult
{
    public readonly TypeStepStatus Status;
    public readonly PaktEvent TypeEvent;
    public readonly PaktParseError? ParseError;

    private TypeStepResult(TypeStepStatus status, PaktEvent evt, PaktParseError? error)
    {
        Status = status;
        TypeEvent = evt;
        ParseError = error;
    }

    public static TypeStepResult Event(PaktEvent evt)
        => new(TypeStepStatus.Event, evt, error: null);

    public static TypeStepResult Continue()
        => new(TypeStepStatus.Continue, evt: default, error: null);

    public static TypeStepResult MoreData()
        => new(TypeStepStatus.MoreData, evt: default, error: null);

    public static TypeStepResult Complete()
        => new(TypeStepStatus.Complete, evt: default, error: null);

    public static TypeStepResult Error(PaktParseError error)
        => new(TypeStepStatus.Error, evt: default, error);
}

internal enum TypeStepStatus : byte
{
    Continue,
    Event,
    MoreData,
    Complete,
    Error,
}

[StructLayout(LayoutKind.Auto)]
internal struct TypeFrame
{
    public PaktTypeKind CompositeKind;
    public byte SubState;
    public int MemberScratchStart;
    public int MemberCount;
    public int FieldNameStart;
    public int FieldNameLength;
}

/// <summary>
/// Step-by-step type annotation parser. Emits one type event per
/// <see cref="Step"/> call, using a frame stack for nesting.
/// Receives <see cref="ReadOnlySequence{T}"/> via
/// <c>SequenceReader.UnreadSequence</c> — no ref struct constraints.
/// </summary>
internal sealed class TypeParser
{
    private static class SubState
    {
        // Shared initial state for all composites
        public const byte LayoutAndCheckEmpty = 0;

        // Struct: {field:type field:type}
        public const byte StructFieldName = 1;
        public const byte StructFieldColon = 2;
        public const byte StructFieldType = 3;
        public const byte StructPostField = 4;
        public const byte StructClose = 5;

        // Tuple: (type type)
        public const byte TupleElementType = 1;
        public const byte TuplePostElement = 2;
        public const byte TupleClose = 3;

        // List: [type]
        public const byte ListElementType = 1;
        public const byte ListClose = 2;

        // Map: <type => type>
        public const byte MapKeyType = 1;
        public const byte MapPreBind = 2;
        public const byte MapBind = 3;
        public const byte MapPostBind = 4;
        public const byte MapValueType = 5;
        public const byte MapClose = 6;

        // AtomSet: |name name|
        public const byte AtomName = 1;
        public const byte AtomSetClose = 2;
    }
    private readonly PaktTypeArena _types;
    private readonly PaktReaderOptions _options;

    private readonly TypeFrame[] _stack;
    private readonly PaktTypeRef[] _memberScratch;
    private int _stackDepth;
    private int _scratchUsed;

    private SourceCursor _cursor;
    private PaktTypeRef _rootTypeRef;
    private bool _checkNullable;
    private bool _pendingPostComplete;

    public TypeParser(PaktTypeArena types, PaktReaderOptions options)
    {
        _types = types;
        _options = options;
        _stack = new TypeFrame[options.MaxNestingDepth];
        _memberScratch = new PaktTypeRef[options.MaxNestingDepth * 4];
    }

    public SourceCursor CurrentCursor => _cursor;
    public PaktTypeRef RootTypeRef => _rootTypeRef;

    public void Begin(SourceCursor startCursor)
    {
        _cursor = startCursor;
        _stackDepth = 0;
        _scratchUsed = 0;
        _rootTypeRef = default;
        _checkNullable = false;
        _pendingPostComplete = false;
        _types.ClearNames();
    }

    /// <summary>
    /// Parses one step of the type annotation.
    /// Creates a stack-local <see cref="SequenceReader{T}"/> from
    /// <paramref name="unread"/> on each call.
    /// </summary>
    public TypeStepResult Step(
        ReadOnlySequence<byte> unread,
        bool isFinal,
        out long bytesConsumed)
    {
        var reader = new SequenceReader<byte>(unread);
        TypeStepResult result = StepCore(ref reader, isFinal);
        bytesConsumed = reader.Consumed;
        return result;
    }

    private TypeStepResult StepCore(
        scoped ref SequenceReader<byte> reader, bool isFinal)
    {
        // After emitting NullableModifier, complete the post-type step
        if (_pendingPostComplete)
        {
            _pendingPostComplete = false;
            return PostTypeComplete(ref reader, isFinal);
        }

        // After a completed type, check for nullable suffix
        if (_checkNullable)
        {
            _checkNullable = false;
            if (!reader.TryPeek(out byte q))
            {
                if (!isFinal)
                    return TypeStepResult.MoreData();
                // isFinal: no '?' coming, fall through to post-type
                return PostTypeComplete(ref reader, isFinal);
            }

            if (q == Syntax.NullableModifier)
            {
                reader.Advance(1);
                _cursor.Offset++;
                _cursor.Column++;
                ApplyNullable();
                _pendingPostComplete = true;
                return TypeStepResult.Event(new PaktEvent(
                    PaktEvent.Kind.NullableModifier, _cursor.Offset, PaktTypeKind.None, default));
            }
            // Not '?' — fall through to post-type handling
            return PostTypeComplete(ref reader, isFinal);
        }

        // If stack is empty, we need to start parsing a new type
        if (_stackDepth == 0)
            return BeginType(ref reader, isFinal);

        // Dispatch to current frame's composite handler
        ref TypeFrame frame = ref _stack[_stackDepth - 1];
        return frame.CompositeKind switch
        {
            PaktTypeKind.Struct => StepStruct(ref reader, ref frame, isFinal),
            PaktTypeKind.Tuple => StepTuple(ref reader, ref frame, isFinal),
            PaktTypeKind.List => StepList(ref reader, ref frame, isFinal),
            PaktTypeKind.Map => StepMap(ref reader, ref frame, isFinal),
            PaktTypeKind.AtomSet => StepAtomSet(ref reader, ref frame, isFinal),
            _ => TypeStepResult.Error(PaktParseError.Syntax(_cursor.ToPosition())),
        };
    }

    // ── Type dispatch ───────────────────────────────────────────────

    private TypeStepResult BeginType(
        scoped ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!reader.TryPeek(out byte b))
        {
            return isFinal
                ? TypeStepResult.Error(PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition()))
                : TypeStepResult.MoreData();
        }

        return b switch
        {
            Syntax.StructOpen => PushComposite(ref reader, PaktTypeKind.Struct, PaktEvent.Kind.StructTypeStart),
            Syntax.TupleOpen => PushComposite(ref reader, PaktTypeKind.Tuple, PaktEvent.Kind.TupleTypeStart),
            Syntax.ListOpen => PushComposite(ref reader, PaktTypeKind.List, PaktEvent.Kind.ListTypeStart),
            Syntax.MapOpen => PushComposite(ref reader, PaktTypeKind.Map, PaktEvent.Kind.MapTypeStart),
            Syntax.AtomSetOpen => PushComposite(ref reader, PaktTypeKind.AtomSet, PaktEvent.Kind.AtomSetStart),
            _ => ParseScalarType(ref reader, isFinal),
        };
    }

    private TypeStepResult PushComposite(
        scoped ref SequenceReader<byte> reader, PaktTypeKind kind,
        PaktEvent.Kind eventKind)
    {
        if (_stackDepth >= _stack.Length)
        {
            return TypeStepResult.Error(PaktParseError.NestingDepthExceeded(_cursor.ToPosition()));
        }

        reader.Advance(1);
        _cursor.Offset++;
        _cursor.Column++;

        _stack[_stackDepth++] = new TypeFrame
        {
            CompositeKind = kind,
            SubState = SubState.LayoutAndCheckEmpty,
            MemberScratchStart = _scratchUsed,
            MemberCount = 0,
        };

        return TypeStepResult.Event(new PaktEvent(eventKind, _cursor.Offset, kind, default));
    }

    private TypeStepResult ParseScalarType(
        scoped ref SequenceReader<byte> reader, bool isFinal)
    {
        SequencePosition startPos = reader.Position;
        if (!TryReadIdent(ref reader, isFinal, out ReadOnlySequence<byte> token))
        {
            if (reader.End && !isFinal)
                return TypeStepResult.MoreData();
            return TypeStepResult.Error(reader.End
                ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                : PaktParseError.InvalidHeader(_cursor.ToPosition()));
        }

        if (!TryMapScalarType(token, out PaktTypeKind kind))
        {
            return TypeStepResult.Error(PaktParseError.InvalidHeader(_cursor.ToPosition()));
        }

        PaktTypeRef typeRef = _types.Add(new PaktTypeNode { Kind = kind });
        CompleteChildType(typeRef);

        _checkNullable = true;

        // Inside struct/tuple, the type info is carried by FieldDecl/ElementDecl;
        // suppress the separate ScalarType event.
        if (_stackDepth > 0)
        {
            PaktTypeKind parentKind = _stack[_stackDepth - 1].CompositeKind;
            if (parentKind == PaktTypeKind.Struct || parentKind == PaktTypeKind.Tuple)
                return TypeStepResult.Continue();
        }

        return TypeStepResult.Event(
            new PaktEvent(PaktEvent.Kind.ScalarType, _cursor.Offset, kind, default));
    }

    // ── Post-type / nullable ────────────────────────────────────────

    private TypeStepResult PostTypeComplete(
        scoped ref SequenceReader<byte> reader,
        bool isFinal)
    {
        if (_stackDepth == 0)
        {
            // Root type fully parsed
            return TypeStepResult.Complete();
        }

        // Return to parent frame — continue its state machine
        return TypeStepResult.Continue();
    }

    // ── Struct ──────────────────────────────────────────────────────
    // SubState: 0=SkipLayout+CheckEmpty, 1=FieldName, 2=Colon, 3=ChildType, 4=PostField

    private TypeStepResult StepStruct(
        scoped ref SequenceReader<byte> reader,
        ref TypeFrame frame,
        bool isFinal)
    {
        switch (frame.SubState)
        {
            case SubState.LayoutAndCheckEmpty:
                SkipLayout(ref reader);
                if (TryReadClose(ref reader, Syntax.StructClose))
                    return FinishComposite(ref frame, PaktEvent.Kind.StructTypeEnd, PaktTypeKind.Struct);
                frame.SubState = SubState.StructFieldName;
                return TypeStepResult.Continue();

            case SubState.StructFieldName:
                return StepStructFieldName(ref reader, ref frame, isFinal);

            case SubState.StructFieldColon:
                return StepStructFieldColon(ref reader, ref frame, isFinal);

            case SubState.StructFieldType:
                frame.SubState = SubState.StructPostField;
                return BeginType(ref reader, isFinal);

            case SubState.StructPostField:
                return StepStructPostField(ref reader, ref frame, isFinal);

            case SubState.StructClose:
                return FinishComposite(ref frame, PaktEvent.Kind.StructTypeEnd, PaktTypeKind.Struct);

            default:
                return TypeStepResult.Error(PaktParseError.Syntax(_cursor.ToPosition()));
        }
    }

    private TypeStepResult StepStructFieldName(
        scoped ref SequenceReader<byte> reader,
        ref TypeFrame frame,
        bool isFinal)
    {
        if (!TryReadIdent(ref reader, isFinal, out ReadOnlySequence<byte> fieldName))
        {
            if (reader.End && !isFinal)
                return TypeStepResult.MoreData();
            return TypeStepResult.Error(reader.End
                ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                : PaktParseError.InvalidHeader(_cursor.ToPosition()));
        }

        frame.FieldNameStart = _types.AppendName(in fieldName);
        frame.FieldNameLength = (int)fieldName.Length;
        frame.SubState = SubState.StructFieldColon;
        return TypeStepResult.Continue();
    }

    private TypeStepResult StepStructFieldColon(
        scoped ref SequenceReader<byte> reader, ref TypeFrame frame,
        bool isFinal)
    {
        if (!TryReadByte(ref reader, Syntax.TypeAscription, isFinal))
        {
            if (reader.End && !isFinal)
                return TypeStepResult.MoreData();
            return TypeStepResult.Error(reader.End
                ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                : PaktParseError.InvalidHeader(_cursor.ToPosition()));
        }

        frame.SubState = SubState.StructFieldType;
        return TypeStepResult.Continue();
    }

    private TypeStepResult StepStructPostField(
        scoped ref SequenceReader<byte> reader, ref TypeFrame frame, bool isFinal)
    {
        PaktEvent evt = new(
            PaktEvent.Kind.FieldDecl,
            _cursor.Offset,
            _lastCompletedTypeKind,
            new ReadOnlySequence<byte>(_types.NameBuffer, frame.FieldNameStart, frame.FieldNameLength));
        frame.MemberCount++;
        bool hadLayout = SkipLayout(ref reader);
        if (reader.End && !isFinal)
            return TypeStepResult.MoreData();
        if (TryReadClose(ref reader, Syntax.StructClose))
        {
            frame.SubState = SubState.StructClose;
        }
        else
        {
            if (!hadLayout)
                return TypeStepResult.Error(PaktParseError.MissingLayout(_cursor.ToPosition(), "expected layout between struct fields"));
            frame.SubState = SubState.StructFieldName;
        }
        return TypeStepResult.Event(evt);
    }

    // ── Tuple ───────────────────────────────────────────────────────
    // SubState: 0=SkipLayout+CheckEmpty, 1=ChildType, 2=PostElement

    private TypeStepResult StepTuple(
        scoped ref SequenceReader<byte> reader, ref TypeFrame frame,
        bool isFinal)
    {
        switch (frame.SubState)
        {
            case SubState.LayoutAndCheckEmpty:
                SkipLayout(ref reader);
                if (TryReadClose(ref reader, Syntax.TupleClose))
                    return FinishComposite(ref frame, PaktEvent.Kind.TupleTypeEnd, PaktTypeKind.Tuple);
                frame.SubState = SubState.TupleElementType;
                return TypeStepResult.Continue();

            case SubState.TupleElementType:
                frame.SubState = SubState.TuplePostElement;
                return BeginType(ref reader, isFinal);

            case SubState.TuplePostElement: // PostElement — emit ElementDecl
                {
                    PaktTypeKind childKind = _lastCompletedTypeKind;
                    PaktEvent evt = new(
                        PaktEvent.Kind.ElementDecl, _cursor.Offset, childKind, default);
                    frame.MemberCount++;
                    bool hadLayout = SkipLayout(ref reader);
                    if (reader.End && !isFinal)
                        return TypeStepResult.MoreData();
                    if (TryReadClose(ref reader, Syntax.TupleClose))
                    {
                        frame.SubState = SubState.TupleClose;
                    }
                    else
                    {
                        if (!hadLayout)
                            return TypeStepResult.Error(PaktParseError.MissingLayout(_cursor.ToPosition(), "expected layout between tuple elements"));
                        frame.SubState = SubState.TupleElementType;
                    }
                    return TypeStepResult.Event(evt);
                }

            case SubState.TupleClose:
                return FinishComposite(ref frame, PaktEvent.Kind.TupleTypeEnd, PaktTypeKind.Tuple);

            default:
                return TypeStepResult.Error(PaktParseError.Syntax(_cursor.ToPosition()));
        }
    }

    // ── List ────────────────────────────────────────────────────────
    // SubState: 0=SkipLayout, 1=ChildType, 2=PostElement+SkipLayout+Close

    private TypeStepResult StepList(
        scoped ref SequenceReader<byte> reader,
        ref TypeFrame frame,
        bool isFinal)
    {
        switch (frame.SubState)
        {
            case SubState.LayoutAndCheckEmpty:
                SkipLayout(ref reader);
                frame.SubState = SubState.ListElementType;
                return TypeStepResult.Continue();

            case SubState.ListElementType:
                frame.SubState = SubState.ListClose;
                return BeginType(ref reader, isFinal);

            case SubState.ListClose:
                SkipLayout(ref reader);
                if (!TryReadByte(ref reader, Syntax.ListClose, isFinal))
                {
                    if (reader.End && !isFinal)
                        return TypeStepResult.MoreData();
                    return TypeStepResult.Error(reader.End
                        ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                        : PaktParseError.InvalidHeader(_cursor.ToPosition()));
                }
                return FinishList(ref frame);

            default:
                return TypeStepResult.Error(PaktParseError.Syntax(_cursor.ToPosition()));
        }
    }

    // ── Map ─────────────────────────────────────────────────────────
    // SubState: 0=SkipLayout, 1=KeyType, 2=RequireLayout, 3=Bind, 4=RequireLayout, 5=ValueType, 6=SkipLayout+Close

    private TypeStepResult StepMap(
        scoped ref SequenceReader<byte> reader,
        ref TypeFrame frame,
        bool isFinal)
    {
        switch (frame.SubState)
        {
            case SubState.LayoutAndCheckEmpty:
                SkipLayout(ref reader);
                frame.SubState = SubState.MapKeyType;
                return TypeStepResult.Continue();

            case SubState.MapKeyType:
                frame.SubState = SubState.MapPreBind;
                return BeginType(ref reader, isFinal);

            case SubState.MapPreBind:
                // Key type completed — advance scratch index so value writes to [start + 1]
                frame.MemberCount++;
                return StepMapBind(ref reader, ref frame, isFinal, 2);

            case SubState.MapBind:
                return StepMapBind(ref reader, ref frame, isFinal, 3);

            case SubState.MapPostBind:
                return StepMapBind(ref reader, ref frame, isFinal, 4);

            case SubState.MapValueType:
                frame.SubState = SubState.MapClose;
                return BeginType(ref reader, isFinal);

            case SubState.MapClose:
                frame.MemberCount++;
                return StepMapClose(ref reader, ref frame, isFinal);

            default:
                return TypeStepResult.Error(PaktParseError.Syntax(_cursor.ToPosition()));
        }
    }

    private TypeStepResult StepMapBind(
        scoped ref SequenceReader<byte> reader, ref TypeFrame frame,
        bool isFinal, byte subState)
    {
        if (subState == 2 || subState == 4)
        {
            if (!RequireLayout(ref reader, isFinal))
            {
                if (reader.End && !isFinal)
                    return TypeStepResult.MoreData();
                return TypeStepResult.Error(reader.End
                    ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                    : PaktParseError.MissingLayout(_cursor.ToPosition(), "expected layout around '=>'"));
            }
            frame.SubState = (byte)(subState + 1);
            return TypeStepResult.Continue();
        }

        // subState == 3: read '=>'
        if (!TryReadDigraph(ref reader, Syntax.MapBind, isFinal))
        {
            if (reader.End && !isFinal)
                return TypeStepResult.MoreData();
            return TypeStepResult.Error(reader.End
                ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                : PaktParseError.InvalidHeader(_cursor.ToPosition(), "expected '=>'"));
        }

        frame.SubState = SubState.MapPostBind;
        return TypeStepResult.Continue();
    }

    private TypeStepResult StepMapClose(
        scoped ref SequenceReader<byte> reader, ref TypeFrame frame,
        bool isFinal)
    {
        SkipLayout(ref reader);
        if (!TryReadByte(ref reader, Syntax.MapClose, isFinal))
        {
            if (reader.End && !isFinal)
                return TypeStepResult.MoreData();
            return TypeStepResult.Error(reader.End
                ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                : PaktParseError.InvalidHeader(_cursor.ToPosition()));
        }
        return FinishMap(ref frame);
    }

    // ── AtomSet ─────────────────────────────────────────────────────
    // SubState: 0=SkipLayout+CheckEmpty, 1=AtomName, 2=PostAtom

    private TypeStepResult StepAtomSet(
        scoped ref SequenceReader<byte> reader, ref TypeFrame frame,
        bool isFinal)
    {
        switch (frame.SubState)
        {
            case SubState.LayoutAndCheckEmpty:
                SkipLayout(ref reader);
                if (TryReadClose(ref reader, Syntax.AtomSetClose))
                    return TypeStepResult.Error(PaktParseError.InvalidHeader(_cursor.ToPosition()));
                frame.SubState = SubState.AtomName;
                return TypeStepResult.Continue();

            case SubState.AtomName: // AtomName
                {
                    if (!TryReadIdent(ref reader, isFinal, out ReadOnlySequence<byte> atomName))
                    {
                        if (reader.End && !isFinal)
                            return TypeStepResult.MoreData();
                        return TypeStepResult.Error(reader.End
                            ? PaktParseError.UnexpectedEndOfInput(_cursor.ToPosition())
                            : PaktParseError.InvalidHeader(_cursor.ToPosition()));
                    }

                    // Reject reserved atom names
                    if (TokenEquals(atomName, "true"u8) || TokenEquals(atomName, "false"u8) || TokenEquals(atomName, "nil"u8))
                        return TypeStepResult.Error(PaktParseError.ReservedToken(_cursor.ToPosition(), "reserved keyword cannot be an atom name"));

                    int nameStart = _types.AppendName(in atomName);
                    PaktEvent evt = new(
                        PaktEvent.Kind.AtomDecl, _cursor.Offset, PaktTypeKind.AtomSet,
                        new ReadOnlySequence<byte>(_types.NameBuffer, nameStart, (int)atomName.Length));
                    frame.MemberCount++;
                    bool hadLayout = SkipLayout(ref reader);
                    if (reader.End && !isFinal)
                        return TypeStepResult.MoreData();
                    if (TryReadClose(ref reader, Syntax.AtomSetClose))
                    {
                        frame.SubState = SubState.AtomSetClose;
                    }
                    else
                    {
                        if (!hadLayout)
                            return TypeStepResult.Error(PaktParseError.MissingLayout(_cursor.ToPosition(), "expected layout between atom names"));
                        frame.SubState = SubState.AtomName;
                    }
                    return TypeStepResult.Event(evt);
                }

            case SubState.AtomSetClose: // Close
                return FinishAtomSet(ref frame);

            default:
                return TypeStepResult.Error(PaktParseError.Syntax(_cursor.ToPosition()));
        }
    }

    // ── Composite finish helpers ────────────────────────────────────

    private TypeStepResult FinishComposite(
        ref TypeFrame frame, PaktEvent.Kind endKind, PaktTypeKind kind)
    {
        ReadOnlySpan<PaktTypeRef> members = _memberScratch.AsSpan(frame.MemberScratchStart, frame.MemberCount);
        PaktTypeRef typeRef = kind switch
        {
            PaktTypeKind.Struct => _types.AddStruct(members),
            PaktTypeKind.Tuple => _types.AddTuple(members),
            _ => _types.Add(new PaktTypeNode { Kind = kind, MemberCount = frame.MemberCount }),
        };

        _scratchUsed = frame.MemberScratchStart;
        _stackDepth--;
        CompleteChildType(typeRef);

        _checkNullable = true;
        return TypeStepResult.Event(new PaktEvent(endKind, _cursor.Offset, kind, default));
    }

    private TypeStepResult FinishList(ref TypeFrame frame)
    {
        // List element type is the last completed child
        PaktTypeRef typeRef = _types.Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.List,
            ElementType = _lastCompletedTypeRef,
        });

        _scratchUsed = frame.MemberScratchStart;
        _stackDepth--;
        CompleteChildType(typeRef);

        _checkNullable = true;
        return TypeStepResult.Event(
            new PaktEvent(PaktEvent.Kind.ListTypeEnd, _cursor.Offset, PaktTypeKind.List, default));
    }

    private TypeStepResult FinishMap(ref TypeFrame frame)
    {
        System.Diagnostics.Debug.Assert(frame.MemberCount == 2, $"Map must have exactly 2 members (key + value), got {frame.MemberCount}");
        // Members scratch has [keyTypeRef, valueTypeRef]
        PaktTypeRef keyType = _memberScratch[frame.MemberScratchStart];
        PaktTypeRef valueType = _memberScratch[frame.MemberScratchStart + 1];

        PaktTypeRef typeRef = _types.Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.Map,
            KeyType = keyType,
            ValueType = valueType,
        });

        _scratchUsed = frame.MemberScratchStart;
        _stackDepth--;
        CompleteChildType(typeRef);

        _checkNullable = true;
        return TypeStepResult.Event(
            new PaktEvent(PaktEvent.Kind.MapTypeEnd, _cursor.Offset, PaktTypeKind.Map, default));
    }

    private TypeStepResult FinishAtomSet(ref TypeFrame frame)
    {
        PaktTypeRef typeRef = _types.AddAtomSet(frame.MemberCount);

        _scratchUsed = frame.MemberScratchStart;
        _stackDepth--;
        CompleteChildType(typeRef);

        _checkNullable = true;
        return TypeStepResult.Event(
            new PaktEvent(PaktEvent.Kind.AtomSetEnd, _cursor.Offset, PaktTypeKind.AtomSet, default));
    }

    // ── Child type tracking ─────────────────────────────────────────

    private PaktTypeRef _lastCompletedTypeRef;
    private PaktTypeKind _lastCompletedTypeKind;

    private void CompleteChildType(PaktTypeRef typeRef)
    {
        _lastCompletedTypeRef = typeRef;
        _lastCompletedTypeKind = _types.Get(typeRef).Kind;

        if (_stackDepth > 0)
        {
            ref TypeFrame parent = ref _stack[_stackDepth - 1];
            int writeIndex = parent.MemberScratchStart + parent.MemberCount;
            _memberScratch[writeIndex] = typeRef;

            // Keep _scratchUsed past all parent member slots so the next
            // child composite's MemberScratchStart won't overlap.
            if (_scratchUsed <= writeIndex)
                _scratchUsed = writeIndex + 1;
        }
        else
        {
            _rootTypeRef = typeRef;
        }
    }

    private void ApplyNullable()
    {
        PaktTypeNode node = _types.Get(_lastCompletedTypeRef);
        if (!node.IsNullable)
        {
            _lastCompletedTypeRef = _types.Add(new PaktTypeNode
            {
                Kind = node.Kind,
                ElementType = node.ElementType,
                KeyType = node.KeyType,
                ValueType = node.ValueType,
                IsNullable = true,
                FirstMemberIndex = node.FirstMemberIndex,
                MemberCount = node.MemberCount,
            });

            // Update in parent's scratch if applicable
            if (_stackDepth > 0)
            {
                ref TypeFrame parent = ref _stack[_stackDepth - 1];
                _memberScratch[parent.MemberScratchStart + parent.MemberCount] = _lastCompletedTypeRef;
            }
            else
            {
                _rootTypeRef = _lastCompletedTypeRef;
            }
        }
    }

    // ── Token reading (self-contained, uses local SequenceReader) ───

    private bool TryReadIdent(
        scoped ref SequenceReader<byte> reader, bool isFinal,
        out ReadOnlySequence<byte> token)
    {
        long startConsumed = reader.Consumed;
        ReadOnlySpan<byte> span = reader.UnreadSpan;
        if (span.IsEmpty || !Lexical.IsIdentifierStart(span[0]))
        {
            token = default;
            return false;
        }

        SequencePosition startPos = reader.Position;
        int i = 1;
        int limit = Math.Min(span.Length, _options.MaxTokenBytes);
        while (i < limit && Lexical.IsIdentifierPart(span[i]))
            i++;

        reader.Advance(i);

        if (i == span.Length)
        {
            long consumed = i;
            while (consumed < _options.MaxTokenBytes
                && reader.TryPeek(out byte b)
                && Lexical.IsIdentifierPart(b))
            {
                reader.Advance(1);
                consumed++;
            }
        }

        if (reader.End && !isFinal)
        {
            // Rewind so partial bytes are not reported as consumed
            reader.Rewind(reader.Consumed - startConsumed);
            token = default;
            return false;
        }

        token = reader.Sequence.Slice(startPos, reader.Position);
        long len = token.Length;
        _cursor.Offset += len;
        _cursor.Column += len;
        return true;
    }

    private bool TryReadByte(scoped ref SequenceReader<byte> reader, byte expected, bool isFinal)
    {
        if (!reader.TryPeek(out byte b) || b != expected)
            return false;

        reader.Advance(1);
        _cursor.Offset++;
        _cursor.Column++;
        return true;
    }

    private bool TryReadClose(scoped ref SequenceReader<byte> reader, byte terminator)
    {
        if (!reader.TryPeek(out byte b) || b != terminator)
            return false;

        reader.Advance(1);
        _cursor.Offset++;
        _cursor.Column++;
        return true;
    }

    private bool TryReadDigraph(scoped ref SequenceReader<byte> reader, Digraph expected, bool isFinal)
    {
        if (!reader.TryPeek(out byte b0) || b0 != expected.First)
            return false;
        if (!reader.TryPeek(1, out byte b1) || b1 != expected.Second)
            return false;

        reader.Advance(2);
        _cursor.Offset += 2;
        _cursor.Column += 2;
        return true;
    }

    private bool SkipLayout(scoped ref SequenceReader<byte> reader)
    {
        bool consumed = false;
        while (true)
        {
            ReadOnlySpan<byte> span = reader.UnreadSpan;
            int i = 0;
            while (i < span.Length && Lexical.IsLayoutChar(span[i]))
                i++;

            if (i > 0)
            {
                consumed = true;
                ReadOnlySpan<byte> chars = span.Slice(0, i);
                int nlIndex = chars.IndexOf(Lexical.Newline);
                if (nlIndex < 0)
                {
                    _cursor.AdvanceColumns(i);
                }
                else
                {
                    for (int j = 0; j < i; j++)
                        _cursor.Advance(chars[j]);
                }
                reader.Advance(i);
            }

            if (i < span.Length)
            {
                if (span[i] == Syntax.CommentStart)
                {
                    SkipComment(ref reader);
                    consumed = true;
                    continue;
                }
                break;
            }

            if (reader.End)
                break;
        }
        return consumed;
    }

    private void SkipComment(scoped ref SequenceReader<byte> reader)
    {
        // Consume the '#'
        reader.Advance(1);
        _cursor.Advance(Lexical.Hash);

        // §3.2: consume everything until newline (but not the newline itself)
        while (true)
        {
            ReadOnlySpan<byte> span = reader.UnreadSpan;
            int nlPos = span.IndexOfAny(Lexical.Newline, Lexical.CarriageReturn);
            int consumed = nlPos < 0 ? span.Length : nlPos;
            if (consumed > 0)
            {
                _cursor.AdvanceColumns(consumed);
                reader.Advance(consumed);
            }
            if (nlPos >= 0 || reader.End)
                break;
        }
    }

    private bool RequireLayout(scoped ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!reader.TryPeek(out byte b))
            return false;

        if (!Lexical.IsLayoutChar(b) && b != Syntax.CommentStart)
            return false;

        SkipLayout(ref reader);
        return true;
    }

    private static bool TryMapScalarType(ReadOnlySequence<byte> token, out PaktTypeKind kind)
    {
        if (token.IsSingleSegment)
            return TryMapScalarType(token.FirstSpan, out kind);

        Span<byte> scratch = stackalloc byte[5];
        if (token.Length > scratch.Length)
        {
            kind = default;
            return false;
        }

        int length = (int)token.Length;
        token.CopyTo(scratch[..length]);
        return TryMapScalarType(scratch[..length], out kind);
    }

    private static bool TryMapScalarType(ReadOnlySpan<byte> span, out PaktTypeKind kind)
    {
        kind = span.Length switch
        {
            2 when span.SequenceEqual("ts"u8) => PaktTypeKind.Timestamp,
            3 when span.SequenceEqual("str"u8) => PaktTypeKind.String,
            3 when span.SequenceEqual("int"u8) => PaktTypeKind.Int,
            3 when span.SequenceEqual("dec"u8) => PaktTypeKind.Decimal,
            3 when span.SequenceEqual("bin"u8) => PaktTypeKind.Binary,
            4 when span.SequenceEqual("bool"u8) => PaktTypeKind.Bool,
            4 when span.SequenceEqual("uuid"u8) => PaktTypeKind.Uuid,
            4 when span.SequenceEqual("date"u8) => PaktTypeKind.Date,
            5 when span.SequenceEqual("float"u8) => PaktTypeKind.Float,
            _ => default,
        };

        return kind.IsScalar();
    }

    private static bool TokenEquals(ReadOnlySequence<byte> token, ReadOnlySpan<byte> expected)
    {
        if (token.Length != expected.Length)
            return false;

        int i = 0;
        foreach (var segment in token)
        {
            for (int j = 0; j < segment.Length; j++, i++)
            {
                if (segment.Span[j] != expected[i])
                    return false;
            }
        }
        return true;
    }
}
