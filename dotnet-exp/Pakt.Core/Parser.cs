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
        SkipBom(ref reader);
        SkipLayout(ref reader);
        _phase = ParserPhase.BetweenStatements;
        return StepResult.Event(PaktEvent.UnitStart(_cursor.Offset));
    }

    private StepResult StepBetweenStatements(ref SequenceReader<byte> reader, bool isFinal)
    {
        SkipLayout(ref reader);

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

    private StepResult StepScalarValue(
        ref SequenceReader<byte> reader,
        ref ValueFrame frame,
        PaktTypeNode node,
        bool isFinal)
    {
        SkipLayout(ref reader);

        if (!reader.TryPeek(out byte first))
            return isFinal
            ? StepResult.Error(PaktParseError.Syntax(CurrentPosition))
            : StepResult.MoreData();

        // Atom values: |ident
        if (node.Kind == PaktTypeKind.AtomSet)
            return StepAtomValue(ref reader, ref frame, isFinal);

        // nil — any nullable type
        if (first == Syntax.NilKeywordStart && node.IsNullable)
            return ReadNilAndEmit(ref reader, ref frame, node, isFinal);

        return node.Kind switch
        {
            PaktTypeKind.String => ReadStringValueAndEmit(ref reader, ref frame, isFinal, first),
            PaktTypeKind.Int => ReadIntValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Decimal => ReadDecValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Float => ReadFloatValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Bool => ReadBoolValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Uuid => ReadUuidValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Date => ReadDateValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Timestamp => ReadTsValueAndEmit(ref reader, ref frame, isFinal),
            PaktTypeKind.Binary => ReadBinaryValueAndEmit(ref reader, ref frame, isFinal),
            _ => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "unsupported scalar type")),
        };
    }

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
        if (!TryReadExpected(ref reader, Syntax.TypeAscription, isFinal, out StepResult separatorResult))
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
        // §7: Layout is required around '=' and '<<'
        if (!TryRequireLayout(ref reader, isFinal, out StepResult layoutResult))
            return layoutResult;

        if (!TryReadStatementOperator(ref reader, isFinal, out StepResult opResult))
            return opResult;

        if (!TryRequireLayout(ref reader, isFinal, out StepResult postResult))
            return postResult;

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

        if (op == Syntax.AssignOp)
        {
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;
            _isPack = false;
            result = default;
            return true;
        }

        if (TryReadDigraph(ref reader, Syntax.PackOp, isFinal, out result))
        {
            _isPack = true;
            return true;
        }

        if (result.Status == Parser.StepStatus.MoreData)
            return false;

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
            Syntax.StructOpen => TryParseStructType(ref reader, isFinal, depth, out typeRef, out result),
            Syntax.TupleOpen => TryParseTupleType(ref reader, isFinal, depth, out typeRef, out result),
            Syntax.ListOpen => TryParseListType(ref reader, isFinal, depth, out typeRef, out result),
            Syntax.MapOpen => TryParseMapType(ref reader, isFinal, depth, out typeRef, out result),
            Syntax.AtomSetOpen => TryParseAtomSetType(ref reader, isFinal, out typeRef, out result),
            _ => TryParseScalarType(ref reader, isFinal, out typeRef, out result),
        };

        if (!parsed)
            return false;

        if (reader.TryPeek(out b) && b == Syntax.NullableModifier)
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
        if (!TryReadExpected(ref reader, Syntax.StructOpen, isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.StructTypeStart, _cursor.Offset));

        // §5.6: struct_type = LBRACE layout_opt (field (LAYOUT field)*)? layout_opt RBRACE
        SkipLayout(ref reader);
        var memberTypes = new List<PaktTypeRef>(4);
        if (TryReadEmptyComposite(ref reader, Syntax.StructClose))
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

            SkipLayout(ref reader);
            if (TryReadEmptyComposite(ref reader, Syntax.StructClose))
                break;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.StructTypeEnd, _cursor.Offset));

        typeRef = _types.AddStruct(CollectionsMarshal.AsSpan(memberTypes));
        result = default;
        return true;
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

        if (!TryReadExpected(ref reader, Syntax.TypeAscription, isFinal, out result))
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
        if (!TryReadExpected(ref reader, Syntax.TupleOpen, isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.TupleTypeStart, _cursor.Offset));

        // §5.6: tuple_type = LPAREN layout_opt (type (LAYOUT type)*)? layout_opt RPAREN
        SkipLayout(ref reader);
        var memberTypes = new List<PaktTypeRef>(4);
        if (TryReadEmptyComposite(ref reader, Syntax.TupleClose))
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

            SkipLayout(ref reader);
            if (TryReadEmptyComposite(ref reader, Syntax.TupleClose))
                break;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.TupleTypeEnd, _cursor.Offset));

        typeRef = _types.AddTuple(CollectionsMarshal.AsSpan(memberTypes));
        result = default;
        return true;
    }

    private bool TryParseListType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        int depth,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, Syntax.ListOpen, isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.ListTypeStart, _cursor.Offset));

        // §5.6: list_type = LBRACK layout_opt type layout_opt RBRACK
        SkipLayout(ref reader);

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef elementType, out result))
        {
            typeRef = default;
            return false;
        }

        SkipLayout(ref reader);

        if (!TryReadExpected(ref reader, Syntax.ListClose, isFinal, out result))
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
        if (!TryReadExpected(ref reader, Syntax.MapOpen, isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.MapTypeStart, _cursor.Offset));

        // §5.6: map_type = LANGLE layout_opt type LAYOUT BIND LAYOUT type layout_opt RANGLE
        SkipLayout(ref reader);

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef keyType, out result))
        {
            typeRef = default;
            return false;
        }

        if (!TryReadLayoutBindLayout(ref reader, isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        if (!TryParseTypeReference(ref reader, isFinal, depth + 1, out PaktTypeRef valueType, out result))
        {
            typeRef = default;
            return false;
        }

        SkipLayout(ref reader);

        if (!TryReadExpected(ref reader, Syntax.MapClose, isFinal, out result))
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

    /// <summary>
    /// Reads LAYOUT '=>' LAYOUT (§7: layout required around '=>').
    /// </summary>
    private bool TryReadLayoutBindLayout(
        ref SequenceReader<byte> reader, bool isFinal, out StepResult result)
    {
        if (!TryRequireLayout(ref reader, isFinal, out result))
            return false;
        if (!TryReadDigraph(ref reader, Syntax.MapBind, isFinal, out result))
            return false;
        if (!TryRequireLayout(ref reader, isFinal, out result))
            return false;
        return true;
    }

    private bool TryParseAtomSetType(
        ref SequenceReader<byte> reader,
        bool isFinal,
        out PaktTypeRef typeRef,
        out StepResult result)
    {
        if (!TryReadExpected(ref reader, Syntax.AtomSetOpen, isFinal, out result))
        {
            typeRef = default;
            return false;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.AtomSetStart, _cursor.Offset));

        // §5.6: atom_set = PIPE layout_opt IDENT (LAYOUT IDENT)* layout_opt PIPE
        SkipLayout(ref reader);

        if (TryReadEmptyComposite(ref reader, Syntax.AtomSetClose))
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

            SkipLayout(ref reader);
            if (TryReadEmptyComposite(ref reader, Syntax.AtomSetClose))
                break;
        }

        _pendingTypeEvents.Add(PendingTypeEvent.Simple(
            PaktEvent.Kind.AtomSetEnd, _cursor.Offset));

        typeRef = _types.AddAtomSet(atomCount);
        result = default;
        return true;
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
            2 when span == "ts"u8 => PaktTypeKind.Timestamp,
            3 when span == "str"u8 => PaktTypeKind.String,
            3 when span == "int"u8 => PaktTypeKind.Int,
            3 when span == "dec"u8 => PaktTypeKind.Decimal,
            3 when span == "bin"u8 => PaktTypeKind.Binary,
            4 when span == "bool"u8 => PaktTypeKind.Bool,
            4 when span == "uuid"u8 => PaktTypeKind.Uuid,
            4 when span == "date"u8 => PaktTypeKind.Date,
            5 when span == "float"u8 => PaktTypeKind.Float,
            _ => default,
        };

        return kind.IsScalar();
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

    /// <summary>
    /// Skips layout: whitespace, newlines, and comments.
    /// LAYOUT = (LAYOUT_CHAR | COMMENT)+
    /// </summary>
    private void SkipLayout(ref SequenceReader<byte> reader)
    {
        while (reader.TryPeek(out byte b))
        {
            if (Lexical.IsLayoutChar(b))
            {
                reader.Advance(1);
                _cursor.Advance(b);
                continue;
            }

            if (b == Lexical.CommentStart)
            {
                SkipComment(ref reader);
                continue;
            }

            break;
        }
    }

    private void SkipComment(ref SequenceReader<byte> reader)
    {
        // §3.2: COMMENT = '#' (any char except NL)*
        // Comment does not consume the newline.
        while (reader.TryRead(out byte b))
        {
            _cursor.Advance(b);
            if (b == Lexical.Newline)
                break;
            if (b == Lexical.CarriageReturn)
            {
                if (reader.TryPeek(out byte next) && next == Lexical.Newline)
                {
                    reader.Advance(1);
                    _cursor.Advance(Lexical.Newline);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Requires at least one layout character. Returns false with error if no layout found.
    /// </summary>
    private bool TryRequireLayout(ref SequenceReader<byte> reader, bool isFinal, out StepResult result)
    {
        if (!reader.TryPeek(out byte b))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.Syntax(CurrentPosition, "expected layout"))
                : StepResult.MoreData();
            return false;
        }

        if (!Lexical.IsLayoutChar(b) && b != Lexical.CommentStart)
        {
            result = StepResult.Error(PaktParseError.Syntax(CurrentPosition, "expected layout"));
            return false;
        }

        SkipLayout(ref reader);
        result = default;
        return true;
    }

    /// <summary>
    /// Reads a two-byte token (digraph). Returns false with MoreData or Error.
    /// </summary>
    private bool TryReadDigraph(
        ref SequenceReader<byte> reader, Digraph expected, bool isFinal, out StepResult result)
    {
        if (!reader.TryPeek(out byte b0))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.Syntax(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (b0 != expected.First)
        {
            result = StepResult.Error(PaktParseError.Syntax(CurrentPosition));
            return false;
        }

        if (!reader.TryPeek(1, out byte b1))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.Syntax(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (b1 != expected.Second)
        {
            result = StepResult.Error(PaktParseError.Syntax(CurrentPosition));
            return false;
        }

        reader.Advance(2);
        _cursor.Offset += 2;
        _cursor.Column += 2;
        result = default;
        return true;
    }

    private void SkipBom(ref SequenceReader<byte> reader)
    {
        // §2: UTF-8 BOM (EF BB BF) accepted and ignored
        if (reader.Remaining >= 3
            && reader.TryPeek(out byte b0) && b0 == 0xEF
            && reader.TryPeek(1, out byte b1) && b1 == 0xBB
            && reader.TryPeek(2, out byte b2) && b2 == 0xBF)
        {
            reader.Advance(3);
            _cursor.Offset += 3;
        }
    }

    // ── Scalar value helpers ──────────────────────────────────────

    private StepResult StepAtomValue(
        ref SequenceReader<byte> reader,
        ref ValueFrame frame,
        bool isFinal)
    {
        if (!TryReadAtomValue(ref reader, isFinal, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(
            new PaktEvent(
                PaktEvent.Kind.AtomValue,
                _cursor.Offset,
                PaktTypeKind.AtomSet,
                payload));
    }

    private StepResult ReadNilAndEmit(
        ref SequenceReader<byte> reader,
        ref ValueFrame frame,
        PaktTypeNode node,
        bool isFinal)
    {
        if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> token, out StepResult result))
            return result;

        if (!IsToken(token, "nil"u8))
            return StepResult.Error(PaktParseError.Syntax(CurrentPosition, "expected 'nil'"));

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.NilValue, _cursor.Offset, node.Kind, default));
    }

    private StepResult ReadStringValueAndEmit(
        ref SequenceReader<byte> reader,
        ref ValueFrame frame,
        bool isFinal,
        byte first)
    {
        // str accepts: '…', r'…', '''…''', r'''…'''
        if (first == Syntax.RawStringPrefix)
        {
            SequencePosition startPos = reader.Position;
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;

            if (!TryReadStringLiteral(ref reader, isFinal, out _, out StepResult innerResult))
                return innerResult;

            ReadOnlySequence<byte> payload = reader.Sequence.Slice(startPos, reader.Position);
            _valueStack.Pop();
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.String, payload));
        }

        if (first != Syntax.StringOpen)
            return StepResult.Error(PaktParseError.TypeMismatch(CurrentPosition, "str requires quoted string"));

        if (!TryReadStringLiteral(ref reader, isFinal, out ReadOnlySequence<byte> strPayload, out StepResult strResult))
            return strResult;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.String, strPayload));
    }

    private StepResult ReadIntValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, IsIntChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Int, payload));
    }

    private StepResult ReadDecValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, IsDecChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Decimal, payload));
    }

    private StepResult ReadFloatValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, IsFloatChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Float, payload));
    }

    private StepResult ReadBoolValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> token, out StepResult result))
            return result;

        if (!IsToken(token, "true"u8) && !IsToken(token, "false"u8))
            return StepResult.Error(PaktParseError.TypeMismatch(CurrentPosition, "bool requires 'true' or 'false'"));

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Bool, token));
    }

    private StepResult ReadUuidValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, IsUuidChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Uuid, payload));
    }

    private StepResult ReadDateValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, IsDateChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Date, payload));
    }

    private StepResult ReadTsValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, IsTsChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Timestamp, payload));
    }

    private StepResult ReadBinaryValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        // bin requires x'…' or b'…'
        if (!reader.TryPeek(out byte prefix) || (prefix != Syntax.HexBinaryPrefix && prefix != Syntax.Base64BinaryPrefix))
            return StepResult.Error(PaktParseError.TypeMismatch(CurrentPosition, "bin requires x'…' or b'…'"));

        if (!reader.TryPeek(1, out byte q) || q != Lexical.Quote)
            return StepResult.Error(PaktParseError.TypeMismatch(CurrentPosition, "bin requires x'…' or b'…'"));

        SequencePosition startPos = reader.Position;
        reader.Advance(1);
        _cursor.Offset++;
        _cursor.Column++;

        if (!TryReadStringLiteral(ref reader, isFinal, out _, out StepResult innerResult))
            return innerResult;

        ReadOnlySequence<byte> payload = reader.Sequence.Slice(startPos, reader.Position);
        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Binary, payload));
    }

    // ── Token reading helpers ─────────────────────────────────────

    /// <summary>
    /// Reads a string literal: '…', '''…'''. Does not read prefix (r/x/b).
    /// Handles escape sequences by skipping \X without interpretation.
    /// </summary>
    private bool TryReadStringLiteral(
        ref SequenceReader<byte> reader, bool isFinal,
        out ReadOnlySequence<byte> payload, out StepResult result)
    {
        SequencePosition startPos = reader.Position;

        if (!reader.TryRead(out byte open) || open != Lexical.Quote)
        {
            payload = default;
            result = StepResult.Error(PaktParseError.Syntax(CurrentPosition));
            return false;
        }

        _cursor.Offset++;
        _cursor.Column++;

        // Check for triple-quote (multi-line)
        bool isMultiLine = reader.TryPeek(out byte q1) && q1 == Lexical.Quote
            && reader.TryPeek(1, out byte q2) && q2 == Lexical.Quote;

        if (isMultiLine)
            return TryReadMultiLineString(ref reader, startPos, isFinal, out payload, out result);

        return TryReadSingleLineString(ref reader, startPos, isFinal, out payload, out result);
    }

    private bool TryReadSingleLineString(
        ref SequenceReader<byte> reader, SequencePosition startPos, bool isFinal,
        out ReadOnlySequence<byte> payload, out StepResult result)
    {
        while (reader.TryRead(out byte b))
        {
            _cursor.Advance(b);
            if (b == Lexical.Quote)
            {
                payload = reader.Sequence.Slice(startPos, reader.Position);
                result = default;
                return true;
            }

            if (b == Lexical.Escape)
            {
                if (!reader.TryRead(out byte escaped))
                {
                    payload = default;
                    result = isFinal
                        ? StepResult.Error(PaktParseError.Syntax(CurrentPosition, "unterminated escape"))
                        : StepResult.MoreData();
                    return false;
                }
                _cursor.Advance(escaped);
            }
        }

        payload = default;
        result = isFinal
            ? StepResult.Error(PaktParseError.Syntax(CurrentPosition, "unterminated string"))
            : StepResult.MoreData();
        return false;
    }

    private bool TryReadMultiLineString(
        ref SequenceReader<byte> reader, SequencePosition startPos, bool isFinal,
        out ReadOnlySequence<byte> payload, out StepResult result)
    {
        // Consume the two additional opening quotes
        reader.Advance(2);
        _cursor.Offset += 2;
        _cursor.Column += 2;

        int closeCount = 0;
        while (reader.TryRead(out byte b))
        {
            _cursor.Advance(b);
            if (b == Lexical.Quote)
            {
                closeCount++;
                if (closeCount == 3)
                {
                    payload = reader.Sequence.Slice(startPos, reader.Position);
                    result = default;
                    return true;
                }
                continue;
            }

            closeCount = 0;
            if (b == Lexical.Escape)
            {
                if (!reader.TryRead(out byte escaped))
                {
                    payload = default;
                    result = isFinal
                        ? StepResult.Error(PaktParseError.Syntax(CurrentPosition, "unterminated escape"))
                        : StepResult.MoreData();
                    return false;
                }
                _cursor.Advance(escaped);
            }
        }

        payload = default;
        result = isFinal
            ? StepResult.Error(PaktParseError.Syntax(CurrentPosition, "unterminated multi-line string"))
            : StepResult.MoreData();
        return false;
    }

    /// <summary>
    /// Reads a token from the input using the given character predicate.
    /// Returns the raw bytes as payload.
    /// </summary>
    private bool TryReadTokenByCharSet(
        ref SequenceReader<byte> reader, bool isFinal,
        Func<byte, bool> isValidChar,
        out ReadOnlySequence<byte> payload, out StepResult result)
    {
        SequencePosition startPos = reader.Position;
        long count = 0;

        while (reader.TryPeek(out byte b))
        {
            if (isValidChar(b))
            {
                reader.Advance(1);
                _cursor.Advance(b);
                count++;
                continue;
            }
            break;
        }

        if (count == 0)
        {
            payload = default;
            result = reader.End && !isFinal
                ? StepResult.MoreData()
                : StepResult.Error(PaktParseError.TypeMismatch(CurrentPosition));
            return false;
        }

        if (reader.End && !isFinal)
        {
            payload = default;
            result = StepResult.MoreData();
            return false;
        }

        payload = reader.Sequence.Slice(startPos, reader.Position);
        result = default;
        return true;
    }

    /// <summary>
    /// Reads an atom value: '|' followed by IDENT.
    /// </summary>
    private bool TryReadAtomValue(
        ref SequenceReader<byte> reader, bool isFinal,
        out ReadOnlySequence<byte> payload, out StepResult result)
    {
        SequencePosition startPos = reader.Position;

        if (!TryReadExpected(ref reader, Syntax.AtomValuePrefix, isFinal, out result))
        {
            payload = default;
            return false;
        }

        if (!TryReadIdentifier(ref reader, isFinal, out _, out result))
        {
            payload = default;
            return false;
        }

        payload = reader.Sequence.Slice(startPos, reader.Position);
        result = default;
        return true;
    }

    // §3.3: INT = [-] DIGIT_SEP | [-] '0x' HEX+ | [-] '0b' BIN+ | [-] '0o' OCT+
    private static bool IsIntChar(byte b) =>
        Lexical.IsHexDigit(b)
        || b == Lexical.Minus || b == Lexical.DigitSeparator
        || b == Lexical.HexPrefix || b == Lexical.OctalMarker || b == Lexical.Base64Prefix;

    // §3.3: DEC = [-] DIGIT_SEP? '.' DIGIT_SEP
    private static bool IsDecChar(byte b) =>
        Lexical.IsDigit(b)
        || b == Lexical.Minus || b == Lexical.DigitSeparator || b == Lexical.DecimalPoint;

    // §3.3: FLOAT = [-] DIGIT_SEP? ('.' DIGIT_SEP)? ('e'|'E') [+-]? DIGIT+
    private static bool IsFloatChar(byte b) =>
        Lexical.IsDigit(b)
        || b == Lexical.Minus || b == Lexical.Plus || b == Lexical.DigitSeparator
        || b == Lexical.DecimalPoint || b == Lexical.ExponentLower || b == Lexical.ExponentUpper;

    // §3.3: DATE = DIGIT{4}-DIGIT{2}-DIGIT{2}
    private static bool IsDateChar(byte b) =>
        Lexical.IsDigit(b) || b == Lexical.Minus;

    // §3.3: TS = DATE 'T' time TZ — digits, -, T, :, ., Z, +
    private static bool IsTsChar(byte b) =>
        Lexical.IsDigit(b)
        || b == Lexical.Minus || b == Lexical.Plus || b == Lexical.Colon
        || b == Lexical.DateTimeSep || b == Lexical.UtcMarker || b == Lexical.DecimalPoint;

    // §3.3: UUID = HEX{8}-HEX{4}-HEX{4}-HEX{4}-HEX{12}
    private static bool IsUuidChar(byte b) =>
        Lexical.IsHexDigit(b)
        || b == Lexical.Minus;

    private static bool IsToken(ReadOnlySequence<byte> token, ReadOnlySpan<byte> expected)
    {
        if (token.Length != expected.Length)
            return false;

        if (token.IsSingleSegment)
            return token.FirstSpan.SequenceEqual(expected);

        Span<byte> scratch = stackalloc byte[8];
        if (expected.Length > scratch.Length)
            return false;

        token.CopyTo(scratch[..(int)token.Length]);
        return scratch[..(int)token.Length].SequenceEqual(expected);
    }

}
