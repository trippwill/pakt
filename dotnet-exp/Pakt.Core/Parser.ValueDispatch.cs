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

        // Pack body: undelimited value sequence
        if ((frame.Flags & FrameFlags.Pack) != FrameFlags.None)
        {
            return node.Kind switch
            {
                PaktTypeKind.List => StepListPackBody(ref reader, ref frame, node, isFinal),
                PaktTypeKind.Map => StepMapPackBody(ref reader, ref frame, node, isFinal),
                _ => StepResult.Error(PaktParseError.Syntax(CurrentPosition, "pack type must be list or map")),
            };
        }

        return node.Kind switch
        {
            PaktTypeKind.Struct => StepStructValue(ref reader, ref frame, node, isFinal),
            PaktTypeKind.Tuple => StepTupleValue(ref reader, ref frame, node, isFinal),
            PaktTypeKind.List => StepListValue(ref reader, ref frame, node, isFinal),
            PaktTypeKind.Map => StepMapValue(ref reader, ref frame, node, isFinal),
            _ => StepScalarValue(ref reader, ref frame, node, isFinal),
        };
    }

    // Composite value handlers

    private StepResult StepStructValue(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        if ((frame.Flags & FrameFlags.Opened) == FrameFlags.None)
        {
            SkipLayout(ref reader);
            if (!TryReadExpected(ref reader, Syntax.StructOpen, isFinal, out StepResult r))
                return r;
            frame.Flags |= FrameFlags.Opened;
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.StructValueStart, _cursor.Offset, PaktTypeKind.Struct, default));
        }

        SkipLayout(ref reader);

        if (TryReadEmptyComposite(ref reader, Syntax.StructClose))
        {
            if (frame.Index != node.MemberCount)
                return StepResult.Error(PaktParseError.ArityMismatch(CurrentPosition));
            _valueStack.Pop();
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.StructValueEnd, _cursor.Offset, PaktTypeKind.Struct, default));
        }

        if (frame.Index >= node.MemberCount)
            return StepResult.Error(PaktParseError.ArityMismatch(CurrentPosition));

        ReadOnlySpan<PaktTypeRef> members = _types.GetMembers(node);
        PaktTypeRef childType = members[frame.Index];
        frame.Index++;

        if (!_valueStack.TryPush(new ValueFrame { TypeRef = childType, Index = 0, Flags = FrameFlags.None }))
            return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));

        return StepResult.Continue();
    }

    private StepResult StepTupleValue(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        if ((frame.Flags & FrameFlags.Opened) == FrameFlags.None)
        {
            SkipLayout(ref reader);
            if (!TryReadExpected(ref reader, Syntax.TupleOpen, isFinal, out StepResult r))
                return r;
            frame.Flags |= FrameFlags.Opened;
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.TupleValueStart, _cursor.Offset, PaktTypeKind.Tuple, default));
        }

        SkipLayout(ref reader);

        if (TryReadEmptyComposite(ref reader, Syntax.TupleClose))
        {
            if (frame.Index != node.MemberCount)
                return StepResult.Error(PaktParseError.ArityMismatch(CurrentPosition));
            _valueStack.Pop();
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.TupleValueEnd, _cursor.Offset, PaktTypeKind.Tuple, default));
        }

        if (frame.Index >= node.MemberCount)
            return StepResult.Error(PaktParseError.ArityMismatch(CurrentPosition));

        ReadOnlySpan<PaktTypeRef> members = _types.GetMembers(node);
        PaktTypeRef childType = members[frame.Index];
        frame.Index++;

        if (!_valueStack.TryPush(new ValueFrame { TypeRef = childType, Index = 0, Flags = FrameFlags.None }))
            return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));

        return StepResult.Continue();
    }

    private StepResult StepListValue(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        if ((frame.Flags & FrameFlags.Opened) == FrameFlags.None)
        {
            SkipLayout(ref reader);
            if (!TryReadExpected(ref reader, Syntax.ListOpen, isFinal, out StepResult r))
                return r;
            frame.Flags |= FrameFlags.Opened;
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.ListValueStart, _cursor.Offset, PaktTypeKind.List, default));
        }

        SkipLayout(ref reader);

        if (TryReadEmptyComposite(ref reader, Syntax.ListClose))
        {
            _valueStack.Pop();
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.ListValueEnd, _cursor.Offset, PaktTypeKind.List, default));
        }

        if (!_valueStack.TryPush(new ValueFrame { TypeRef = node.ElementType, Index = 0, Flags = FrameFlags.None }))
            return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));

        return StepResult.Continue();
    }

    private StepResult StepMapValue(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        if ((frame.Flags & FrameFlags.Opened) == FrameFlags.None)
        {
            SkipLayout(ref reader);
            if (!TryReadExpected(ref reader, Syntax.MapOpen, isFinal, out StepResult r))
                return r;
            frame.Flags |= FrameFlags.Opened;
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.MapValueStart, _cursor.Offset, PaktTypeKind.Map, default));
        }

        // Mid-entry: let the entry handler manage its own layout
        if ((frame.Flags & FrameFlags.ExpectKey) != FrameFlags.None)
            return StepMapEntryValue(ref reader, ref frame, node, isFinal);

        SkipLayout(ref reader);

        if (TryReadEmptyComposite(ref reader, Syntax.MapClose))
        {
            _valueStack.Pop();
            return StepResult.Event(new PaktEvent(
                PaktEvent.Kind.MapValueEnd, _cursor.Offset, PaktTypeKind.Map, default));
        }

        // Start a new entry — emit MapEntryStart, set ExpectKey, push key child
        frame.Flags |= FrameFlags.ExpectKey;
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.MapEntryStart, _cursor.Offset, PaktTypeKind.Map, default));
    }

    private StepResult StepMapEntryValue(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        // We just emitted MapEntryStart + key was pushed. If we're back here,
        // the key value completed. Now read => and push value.
        // But wait — we need to distinguish "key just pushed" vs "key completed".
        // Use frame.Index: 0 = need to push key, 1 = key done need bind, 2 = value done need entry end

        // This is called when ExpectKey is set. Dispatch on Index:
        // Index 0: push key type
        // Index 1: bind was read, push value type
        // Index 2: value done — this shouldn't happen here

        if (frame.Index == 0)
        {
            // Push key child
            frame.Index = 1;
            if (!_valueStack.TryPush(new ValueFrame { TypeRef = node.KeyType, Index = 0, Flags = FrameFlags.None }))
                return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));
            return StepResult.Continue();
        }

        if (frame.Index == 1)
        {
            // Key completed — read LAYOUT => LAYOUT
            if (!TryRequireLayout(ref reader, isFinal, out StepResult layoutResult))
                return layoutResult;
            if (!TryReadDigraph(ref reader, Syntax.MapBind, isFinal, out StepResult bindResult))
                return bindResult;
            if (!TryRequireLayout(ref reader, isFinal, out StepResult postResult))
                return postResult;

            // Push value child
            frame.Index = 2;
            if (!_valueStack.TryPush(new ValueFrame { TypeRef = node.ValueType, Index = 0, Flags = FrameFlags.None }))
                return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));
            return StepResult.Continue();
        }

        // Index 2: value completed — emit MapEntryEnd, reset for next entry
        frame.Index = 0;
        frame.Flags &= ~FrameFlags.ExpectKey;
        return StepResult.Event(new PaktEvent(
            PaktEvent.Kind.MapEntryEnd, _cursor.Offset, PaktTypeKind.Map, default));
    }

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

    // ── Pack body handlers ──────────────────────────────────────────

    private StepResult StepListPackBody(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        SkipLayout(ref reader);

        if (IsPackTerminated(ref reader, isFinal))
        {
            _valueStack.Pop();
            return StepResult.Continue();
        }

        if (reader.End && !isFinal)
            return StepResult.MoreData();

        if (!_valueStack.TryPush(new ValueFrame { TypeRef = node.ElementType, Index = 0, Flags = FrameFlags.None }))
            return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));

        return StepResult.Continue();
    }

    private StepResult StepMapPackBody(
        ref SequenceReader<byte> reader, ref ValueFrame frame, PaktTypeNode node, bool isFinal)
    {
        SkipLayout(ref reader);

        if (IsPackTerminated(ref reader, isFinal))
        {
            _valueStack.Pop();
            return StepResult.Continue();
        }

        if (reader.End && !isFinal)
            return StepResult.MoreData();

        // Map pack entry lifecycle uses Index:
        // 0 = push key, 1 = read bind + push value, 2 = entry done
        if (frame.Index == 0)
        {
            frame.Index = 1;
            if (!_valueStack.TryPush(new ValueFrame { TypeRef = node.KeyType, Index = 0, Flags = FrameFlags.None }))
                return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));
            return StepResult.Continue();
        }

        if (frame.Index == 1)
        {
            if (!TryRequireLayout(ref reader, isFinal, out StepResult lr))
                return lr;
            if (!TryReadDigraph(ref reader, Syntax.MapBind, isFinal, out StepResult br))
                return br;
            if (!TryRequireLayout(ref reader, isFinal, out StepResult pr))
                return pr;

            frame.Index = 2;
            if (!_valueStack.TryPush(new ValueFrame { TypeRef = node.ValueType, Index = 0, Flags = FrameFlags.None }))
                return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));
            return StepResult.Continue();
        }

        // Index 2: entry complete, reset for next
        frame.Index = 0;
        return StepResult.Continue();
    }

    /// <summary>
    /// Checks if a pack body is terminated. A pack ends at:
    /// - EOF (isFinal and reader.End)
    /// - NUL byte (§10.1)
    /// - Start of next statement: ident-start char that is NOT a value keyword/prefix.
    ///   At most 2 bytes of lookahead needed.
    /// </summary>
    private bool IsPackTerminated(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (reader.End)
            return isFinal;

        if (!reader.TryPeek(out byte b))
            return false;

        if (b == Lexical.Nul)
            return true;

        if (!Lexical.IsIdentifierStart(b))
            return false;

        // Ident-start: could be keyword value or statement header.
        // Check second byte to disambiguate.
        if (!reader.TryPeek(1, out byte b2))
            return false; // need more data to decide — not terminated yet

        return b switch
        {
            // r/x/b + quote = value literal (raw string, binary)
            Lexical.LowerR or Lexical.LowerX or Lexical.LowerB
                => b2 != Lexical.SingleQuote,
            // n + i = "ni..." → nil keyword
            Lexical.LowerN => b2 != (byte)'i',
            // t + r = "tr..." → true keyword
            _ when b == (byte)'t' => b2 != (byte)'r',
            // f + a = "fa..." → false keyword
            _ when b == (byte)'f' => b2 != (byte)'a',
            // Any other ident-start = statement header
            _ => true,
        };
    }
}