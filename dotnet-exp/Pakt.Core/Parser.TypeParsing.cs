using System.Buffers;
using System.Runtime.InteropServices;

namespace Pakt;

sealed partial class Parser
{
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
}