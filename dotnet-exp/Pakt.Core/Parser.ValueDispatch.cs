using System.Buffers;

namespace Pakt;

sealed partial class Parser
{
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

        if (!TokenEquals(token, "nil"u8))
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
        if (!TryReadTokenByCharSet(ref reader, isFinal, Lexical.IsIntChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Int, payload));
    }

    private StepResult ReadDecValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, Lexical.IsDecChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Decimal, payload));
    }

    private StepResult ReadFloatValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, Lexical.IsFloatChar, out ReadOnlySequence<byte> payload, out StepResult result))
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

        bool isTrueOrFalse = TokenEquals(token, "true"u8)
            || TokenEquals(token, "false"u8);

        if (!isTrueOrFalse)
            return StepResult.Error(PaktParseError.TypeMismatch(CurrentPosition, "bool requires 'true' or 'false'"));

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Bool, token));
    }

    private StepResult ReadUuidValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, Lexical.IsUuidChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Uuid, payload));
    }

    private StepResult ReadDateValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, Lexical.IsDateChar, out ReadOnlySequence<byte> payload, out StepResult result))
            return result;

        _valueStack.Pop();
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.ScalarValue, _cursor.Offset, PaktTypeKind.Date, payload));
    }

    private StepResult ReadTsValueAndEmit(
        ref SequenceReader<byte> reader, ref ValueFrame frame, bool isFinal)
    {
        if (!TryReadTokenByCharSet(ref reader, isFinal, Lexical.IsTsChar, out ReadOnlySequence<byte> payload, out StepResult result))
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

        if (!reader.TryPeek(1, out byte q) || q != Lexical.SingleQuote)
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
}