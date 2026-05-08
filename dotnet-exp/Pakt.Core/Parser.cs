using System.Buffers;
using System.Runtime.InteropServices;

namespace Pakt;

sealed class Parser
{
    internal enum StepStatus
    {
        Continue,
        Event,
        MoreData,
        Complete,
        Error,
    }

    enum ParserPhase
    {
        UnitStart,
        BetweenStatements,
        StatementName,
        TypeAnnotation,
        TypeEventDrain,
        StatementOperator,
        StatementValue,
        Complete,
        Error,
    }

    internal readonly ref struct StepResult
    {
        public readonly StepStatus Status;
        public readonly PaktEvent PaktEvent;
        public readonly PaktParseError? ParseError;

        private StepResult(StepStatus status, PaktEvent evt, PaktParseError? error)
        {
            Status = status;
            PaktEvent = evt;
            ParseError = error;
        }

        public static StepResult Event(PaktEvent evt)
            => new(StepStatus.Event, evt, error: null);

        public static StepResult Continue()
            => new(StepStatus.Continue, evt: default, error: null);

        public static StepResult MoreData()
            => new(StepStatus.MoreData, evt: default, error: null);

        public static StepResult Complete()
            => new(StepStatus.Complete, evt: default, error: null);

        public static StepResult Error(PaktParseError error)
            => new(StepStatus.Error, evt: default, error);
    }

    [StructLayout(LayoutKind.Auto)]
    struct PendingTypeEvent
    {
        public PaktEvent.Kind Kind;
        public long Offset;
        public PaktTypeKind TypeKind;
        public int NameStart;
        public int NameLength;

        public static PendingTypeEvent Simple(PaktEvent.Kind kind, long offset)
            => new() { Kind = kind, Offset = offset, NameStart = -1 };

        public static PendingTypeEvent Typed(
            PaktEvent.Kind kind, long offset, PaktTypeKind typeKind)
            => new() { Kind = kind, Offset = offset, TypeKind = typeKind, NameStart = -1 };

        public static PendingTypeEvent Named(
            PaktEvent.Kind kind, long offset, PaktTypeKind typeKind,
            int nameStart, int nameLength)
            => new() { Kind = kind, Offset = offset, TypeKind = typeKind, NameStart = nameStart, NameLength = nameLength };
    }

    readonly PaktReaderOptions _options;
    readonly PaktTypeArena _types;
    readonly ValueStack _valueStack;
    readonly List<PendingTypeEvent> _pendingTypeEvents = [];

    ParserPhase _phase;
    SourceCursor _cursor;
    int _pendingDrainIndex;

    // Statement-level state (does not nest)
    ReadOnlySequence<byte> _statementName;
    PaktTypeRef _statementType;
    bool _isPack;

    public Parser(PaktReaderOptions options)
    {
        _options = options;
        _types = new PaktTypeArena();
        _valueStack = new ValueStack(options.MaxNestingDepth);

        _phase = ParserPhase.UnitStart;
        _cursor = SourceCursor.Start;
    }

    public SourcePosition CurrentPosition => _cursor.ToPosition();

    public StepResult Step(ref SequenceReader<byte> reader, bool isFinal)
    {
        return _phase switch
        {
            ParserPhase.UnitStart => StepUnitStart(ref reader, isFinal),
            ParserPhase.BetweenStatements => StepBetweenStatements(ref reader, isFinal),
            ParserPhase.StatementName => StepStatementName(ref reader, isFinal),
            ParserPhase.TypeAnnotation => StepTypeAnnotation(ref reader, isFinal),
            ParserPhase.TypeEventDrain => StepTypeEventDrain(),
            ParserPhase.StatementOperator => StepStatementOperator(ref reader, isFinal),
            ParserPhase.StatementValue => StepValue(ref reader, isFinal),
            ParserPhase.Complete => StepResult.Complete(),
            _ => StepResult.Error(PaktParseError.Syntax(CurrentPosition)),
        };
    }

    private StepResult StepUnitStart(ref SequenceReader<byte> reader, bool isFinal)
    {
        // TODO: Skip UTF-8 BOM if present
        // TODO: Layout/whitespace/comments before first statement
        _phase = ParserPhase.BetweenStatements;
        return StepResult.Event(PaktEvent.UnitStart(_cursor.Offset));
    }

    private StepResult StepBetweenStatements(ref SequenceReader<byte> reader, bool isFinal)
    {
        // TODO: Layout/whitespace/comments

        if (reader.End)
        {
            if (isFinal)
            {
                _phase = ParserPhase.Complete;
                return StepResult.Event(PaktEvent.UnitEnd(_cursor.Offset));
            }

            return StepResult.MoreData();
        }

        _phase = ParserPhase.StatementName;
        return StepResult.Continue();
    }

    private StepResult StepValue(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (_valueStack.IsEmpty)
        {
            // Value complete — return to between-statements
            _phase = ParserPhase.BetweenStatements;
            return StepResult.Event(
                _isPack
                    ? PaktEvent.PackEnd(_cursor.Offset)
                    : PaktEvent.AssignEnd(_cursor.Offset));
        }

        ref ValueFrame frame = ref _valueStack.Top;
        PaktTypeNode node = _types.Get(frame.TypeRef);
        return node.Kind switch
        {
            PaktTypeKind.Struct => StepStructValue(ref reader, ref frame, node, isFinal),
            PaktTypeKind.Tuple => StepTupleValue(ref reader, ref frame, node, isFinal),
            PaktTypeKind.List => StepListValue(ref reader, ref frame, node, isFinal),
            PaktTypeKind.Map => StepMapValue(ref reader, ref frame, node, isFinal),
            _ => StepScalarValue(ref reader, ref frame, node, isFinal),
        };
    }

    // Stub value handlers — to be implemented in a future effort
    private StepResult StepStructValue(ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
        => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "struct value parsing not yet implemented"));

    private StepResult StepTupleValue(ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
        => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "tuple value parsing not yet implemented"));

    private StepResult StepListValue(ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
        => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "list value parsing not yet implemented"));

    private StepResult StepMapValue(ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
        => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "map value parsing not yet implemented"));

    private StepResult StepScalarValue(ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
        => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "scalar value parsing not yet implemented"));

    private StepResult StepStatementName(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> name, out StepResult nameResult))
            return nameResult;

        _statementName = name;
        _phase = ParserPhase.TypeAnnotation;
        return StepResult.Event(PaktEvent.StatementStart(_cursor.Offset, name));
    }

    private StepResult StepTypeAnnotation(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!TryReadExpected(ref reader, Lexical.TypeSeparator, isFinal, out StepResult separatorResult))
            return separatorResult;

        _pendingTypeEvents.Clear();
        _pendingDrainIndex = 0;
        _types.ClearNames();

        if (!TryParseTypeReference(ref reader, isFinal, depth: 0, out PaktTypeRef typeRef, out StepResult typeResult))
            return typeResult;

        _statementType = typeRef;
        _phase = ParserPhase.TypeEventDrain;
        return StepResult.Continue();
    }

    private StepResult StepTypeEventDrain()
    {
        if (_pendingDrainIndex >= _pendingTypeEvents.Count)
        {
            _pendingTypeEvents.Clear();
            _pendingDrainIndex = 0;
            _phase = ParserPhase.StatementOperator;
            return StepResult.Continue();
        }

        var pe = _pendingTypeEvents[_pendingDrainIndex++];
        ReadOnlySequence<byte> payload = pe.NameStart >= 0
            ? new ReadOnlySequence<byte>(_types.NameBuffer, pe.NameStart, pe.NameLength)
            : default;
        return StepResult.Event(new PaktEvent(pe.Kind, pe.Offset, pe.TypeKind, payload));
    }

    private StepResult StepStatementOperator(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!TryReadStatementOperator(ref reader, isFinal, out StepResult opResult))
            return opResult;

        if (!_valueStack.TryPush(new ValueFrame { TypeRef = _statementType, Index = 0, Flags = FrameFlags.None }))
            return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));

        _phase = ParserPhase.StatementValue;
        return StepResult.Event(
            _isPack ? PaktEvent.PackStart(_cursor.Offset) : PaktEvent.AssignStart(_cursor.Offset));
    }

    private bool TryReadStatementOperator(ref SequenceReader<byte> reader, bool isFinal, out StepResult result)
    {
        if (!reader.TryPeek(out byte op))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (op == (byte)'=')
        {
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;
            _isPack = false;
            result = default;
            return true;
        }

        if (op == (byte)'<')
        {
            if (!reader.TryPeek(1, out byte op2) || op2 != (byte)'<')
            {
                if (reader.Remaining < 2 && !isFinal)
                {
                    result = StepResult.MoreData();
                    return false;
                }

                result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
                return false;
            }

            reader.Advance(2);
            _cursor.Offset += 2;
            _cursor.Column += 2;
            _isPack = true;
            result = default;
            return true;
        }

        result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
        return false;
    }

    private bool TryParseTypeReference(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (depth >= _options.MaxNestingDepth)
        {
            typeRef = default;
            result = StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));
            return false;
        }

        if (!reader.TryPeek(out byte b))
        {
            typeRef = default;
            result = isFinal
                ? StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        bool parsed = b switch
        {
            (byte)'{' => TryParseStructType(ref reader, isFinal, depth, out typeRef, out result),
            (byte)'(' => TryParseTupleType(ref reader, isFinal, depth, out typeRef, out result),
            (byte)'[' => TryParseListType(ref reader, isFinal, depth, out typeRef, out result),
            (byte)'<' => TryParseMapType(ref reader, isFinal, depth, out typeRef, out result),
            (byte)'|' => TryParseAtomSetType(ref reader, isFinal, out typeRef, out result),
            _ => TryParseScalarType(ref reader, isFinal, out typeRef, out result),
        };

        if (!parsed)
            return false;

        if (reader.TryPeek(out b) && b == (byte)'?')
        {
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;
            _pendingTypeEvents.Add(PendingTypeEvent.Simple(
                PaktEvent.Kind.NullableModifier, _cursor.Offset));
            typeRef = AddNullableType(typeRef);
        }

        result = default;
        return true;
    }

    private bool TryParseScalarType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> token, out result))
        {
            typeRef = default;
            return false;
        }

        if (!TryMapScalarType(token, out PaktTypeKind kind))
        {
            typeRef = default;
            result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Typed(
            PaktEvent.Kind.ScalarType, _cursor.Offset, kind));

        typeRef = _types.Add(new PaktTypeNode { Kind = kind });
        result = default;
        return true;
    }

    private bool TryParseStructType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, (byte)'{', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.StructTypeStart, _cursor.Offset));

        var memberTypes = new List<PaktTypeRef>(4);
        if (TryReadEmptyComposite(ref reader, (byte)'}'))
        {
            _pendingTypeEvents.Add(PendingTypeEvent.Simple(
                PaktEvent.Kind.StructTypeEnd, _cursor.Offset));

            typeRef = _types.AddStruct(CollectionsMarshal.AsSpan(memberTypes));
            result = default;
            return true;
        }

        while (true)
        {
            if (!TryParseStructField(ref reader, isFinal, depth, memberTypes, out result))
            {
                typeRef = default;
                return false;
            }

            if (!TryReadSeparatorOrTerminator(ref reader, isFinal, (byte)',', (byte)'}', out bool hasMore, out result))
            {
                typeRef = default;
                return false;
            }

            if (hasMore)
                continue;

            _pendingTypeEvents.Add(PendingTypeEvent.Simple(
                PaktEvent.Kind.StructTypeEnd, _cursor.Offset));

            typeRef = _types.AddStruct(CollectionsMarshal.AsSpan(memberTypes));
            result = default;
            return true;
        }
    }

    private bool TryParseStructField(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        List<PaktTypeRef> memberTypes,
        out StepResult result)
    {
        if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> fieldName, out result))
            return false;

        if (!TryReadExpected(ref reader, Lexical.TypeSeparator, isFinal, out result))
            return false;

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef fieldType, out result))
            return false;

        int nameStart = _types.AppendName(in fieldName);
        PaktTypeKind fieldKind = _types.Get(fieldType).Kind;
        _pendingTypeEvents.Add(PendingTypeEvent.Named(
            PaktEvent.Kind.FieldDecl, _cursor.Offset, fieldKind,
            nameStart, (int)fieldName.Length));

        memberTypes.Add(fieldType);
        result = default;
        return true;
    }

    private bool TryParseTupleType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, (byte)'(', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.TupleTypeStart, _cursor.Offset));

        var memberTypes = new List<PaktTypeRef>(4);
        if (TryReadEmptyComposite(ref reader, (byte)')'))
        {
            _pendingTypeEvents.Add(PendingTypeEvent.Simple(
                PaktEvent.Kind.TupleTypeEnd, _cursor.Offset));

            typeRef = _types.AddTuple(CollectionsMarshal.AsSpan(memberTypes));
            result = default;
            return true;
        }

        while (true)
        {
            if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef itemType, out result))
            {
                typeRef = default;
                return false;
            }

            PaktTypeKind itemKind = _types.Get(itemType).Kind;
            _pendingTypeEvents.Add(PendingTypeEvent.Typed(
                PaktEvent.Kind.ElementDecl, _cursor.Offset, itemKind));

            memberTypes.Add(itemType);

            if (!TryReadSeparatorOrTerminator(ref reader, isFinal, (byte)',', (byte)')', out bool hasMore, out result))
            {
                typeRef = default;
                return false;
            }

            if (hasMore)
                continue;

            _pendingTypeEvents.Add(PendingTypeEvent.Simple(
                PaktEvent.Kind.TupleTypeEnd, _cursor.Offset));

            typeRef = _types.AddTuple(CollectionsMarshal.AsSpan(memberTypes));
            result = default;
            return true;
        }
    }

    private bool TryParseListType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, (byte)'[', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.ListTypeStart, _cursor.Offset));

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef elementType, out result))
        {
            typeRef = default;
            return false;
        }

        if (!TryReadExpected(ref reader, (byte)']', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.ListTypeEnd, _cursor.Offset));

        typeRef = _types.Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.List,
            ElementType = elementType,
        });
        result = default;
        return true;
    }

    private bool TryParseMapType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, (byte)'<', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.MapTypeStart, _cursor.Offset));

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef keyType, out result))
        {
            typeRef = default;
            return false;
        }

        // Map binding: currently ';', spec 0.1a uses '=>'
        if (!TryReadExpected(ref reader, (byte)';', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef valueType, out result))
        {
            typeRef = default;
            return false;
        }

        if (!TryReadExpected(ref reader, (byte)'>', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.MapTypeEnd, _cursor.Offset));

        typeRef = _types.Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.Map,
            KeyType = keyType,
            ValueType = valueType,
        });
        result = default;
        return true;
    }

    private bool TryParseAtomSetType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, (byte)'|', isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.AtomSetStart, _cursor.Offset));

        if (TryReadEmptyComposite(ref reader, (byte)'|'))
        {
            typeRef = default;
            result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
            return false;
        }

        int atomCount = 0;
        while (true)
        {
            if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> atomName, out result))
            {
                typeRef = default;
                return false;
            }

            int nameStart = _types.AppendName(in atomName);
            _pendingTypeEvents.Add(PendingTypeEvent.Named(
                PaktEvent.Kind.AtomDecl, _cursor.Offset, PaktTypeKind.AtomSet,
                nameStart, (int)atomName.Length));
            atomCount++;

            if (!TryReadSeparatorOrTerminator(ref reader, isFinal, (byte)',', (byte)'|', out bool hasMore, out result))
            {
                typeRef = default;
                return false;
            }

            if (hasMore)
                continue;

            _pendingTypeEvents.Add(PendingTypeEvent.Simple(
                PaktEvent.Kind.AtomSetEnd, _cursor.Offset));

            typeRef = _types.AddAtomSet(atomCount);
            result = default;
            return true;
        }
    }

    private bool TryReadIdentifier(
        ref SequenceReader<byte> reader,
        bool isFinal,
        out ReadOnlySequence<byte> token,
        out StepResult result)
    {
        SourcePosition start = CurrentPosition;
        ReadOnlySpan<byte> span = reader.UnreadSpan;
        if (span.IsEmpty)
        {
            token = default;
            result = isFinal
                ? StepResult.Error(PaktParseError.InvalidHeader(start))
                : StepResult.MoreData();
            return false;
        }

        byte first = span[0];
        if (!Lexical.IsIdentifierStart(first))
        {
            token = default;
            result = StepResult.Error(PaktParseError.InvalidHeader(start, $"Expected identifier start, got '{(char)first}'"));
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
            token = default;
            result = StepResult.MoreData();
            return false;
        }

        token = reader.Sequence.Slice(startPos, reader.Position);
        long len = token.Length;
        _cursor.Offset += len;
        _cursor.Column += len;
        result = default;
        return true;
    }

    private bool TryReadExpected(
        ref SequenceReader<byte> reader,
        byte expected,
        bool isFinal,
        out StepResult result)
    {
        if (!reader.TryRead(out byte actual))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (actual != expected)
        {
            result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
            return false;
        }

        _cursor.Offset++;
        _cursor.Column++;
        result = default;
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
            2 when span[0] == (byte)'t' && span[1] == (byte)'s' => PaktTypeKind.Timestamp,
            3 when span[0] == (byte)'s' && span[1] == (byte)'t' && span[2] == (byte)'r' => PaktTypeKind.String,
            3 when span[0] == (byte)'i' && span[1] == (byte)'n' && span[2] == (byte)'t' => PaktTypeKind.Int,
            3 when span[0] == (byte)'d' && span[1] == (byte)'e' && span[2] == (byte)'c' => PaktTypeKind.Decimal,
            3 when span[0] == (byte)'b' && span[1] == (byte)'i' && span[2] == (byte)'n' => PaktTypeKind.Binary,
            4 when span[0] == (byte)'b' && span[1] == (byte)'o' && span[2] == (byte)'o' && span[3] == (byte)'l' => PaktTypeKind.Bool,
            4 when span[0] == (byte)'u' && span[1] == (byte)'u' && span[2] == (byte)'i' && span[3] == (byte)'d' => PaktTypeKind.Uuid,
            4 when span[0] == (byte)'d' && span[1] == (byte)'a' && span[2] == (byte)'t' && span[3] == (byte)'e' => PaktTypeKind.Date,
            5 when span[0] == (byte)'f' && span[1] == (byte)'l' && span[2] == (byte)'o' && span[3] == (byte)'a' && span[4] == (byte)'t' => PaktTypeKind.Float,
            _ => default,
        };

        return kind is PaktTypeKind.Timestamp or PaktTypeKind.String or PaktTypeKind.Int
            or PaktTypeKind.Decimal or PaktTypeKind.Binary or PaktTypeKind.Bool
            or PaktTypeKind.Uuid or PaktTypeKind.Date or PaktTypeKind.Float;
    }

    private PaktTypeRef AddNullableType(PaktTypeRef typeRef)
    {
        PaktTypeNode node = _types.Get(typeRef);
        if (node.IsNullable)
            return typeRef;

        return _types.Add(new PaktTypeNode
        {
            Kind = node.Kind,
            ElementType = node.ElementType,
            KeyType = node.KeyType,
            ValueType = node.ValueType,
            IsNullable = true,
            FirstMemberIndex = node.FirstMemberIndex,
            MemberCount = node.MemberCount,
        });
    }

    private bool TryReadEmptyComposite(ref SequenceReader<byte> reader, byte terminator)
    {
        if (!reader.TryPeek(out byte b) || b != terminator)
            return false;

        reader.Advance(1);
        _cursor.Offset++;
        _cursor.Column++;
        return true;
    }

    private bool TryReadSeparatorOrTerminator(
        ref SequenceReader<byte> reader,
        bool isFinal,
        byte separator,
        byte terminator,
        out bool hasMore,
        out StepResult result)
    {
        if (!reader.TryPeek(out byte b))
        {
            hasMore = false;
            result = isFinal
                ? StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (b == separator)
        {
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;
            hasMore = true;
            result = default;
            return true;
        }

        if (b == terminator)
        {
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;
            hasMore = false;
            result = default;
            return true;
        }

        hasMore = false;
        result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
        return false;
    }

}
