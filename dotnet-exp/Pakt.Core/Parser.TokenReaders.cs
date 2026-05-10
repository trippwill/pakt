using System.Buffers;

namespace Pakt;

sealed partial class Parser
{
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

    /// <summary>
    /// Reads a string literal: '…', '''…'''. Does not read prefix (r/x/b).
    /// Handles escape sequences by skipping \X without interpretation.
    /// </summary>
    private bool TryReadStringLiteral(
        ref SequenceReader<byte> reader, bool isFinal,
        out ReadOnlySequence<byte> payload, out StepResult result)
    {
        SequencePosition startPos = reader.Position;

        if (!reader.TryRead(out byte open) || open != Lexical.SingleQuote)
        {
            payload = default;
            result = StepResult.Error(PaktParseError.Syntax(CurrentPosition));
            return false;
        }

        _cursor.Offset++;
        _cursor.Column++;

        // Check for triple-quote (multi-line)
        bool isMultiLine = reader.TryPeek(out byte q1) && q1 == Lexical.SingleQuote
            && reader.TryPeek(1, out byte q2) && q2 == Lexical.SingleQuote;

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
            if (b == Lexical.SingleQuote)
            {
                payload = reader.Sequence.Slice(startPos, reader.Position);
                result = default;
                return true;
            }

            if (b == Lexical.Backslash)
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
            if (b == Lexical.SingleQuote)
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
            if (b == Lexical.Backslash)
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

            if (b == Syntax.CommentStart)
            {
                SkipComment(ref reader);
                continue;
            }

            break;
        }
    }

    private void SkipComment(ref SequenceReader<byte> reader)
    {
        // Consume the '#'
        reader.Advance(1);
        _cursor.Advance(Lexical.Hash);

        // §3.2: consume everything until newline (but not the newline itself)
        while (reader.TryPeek(out byte b))
        {
            if (b == Lexical.Newline || b == Lexical.CarriageReturn)
                break;
            reader.Advance(1);
            _cursor.Advance(b);
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

        if (!Lexical.IsLayoutChar(b) && b != Syntax.CommentStart)
        {
            result = StepResult.Error(PaktParseError.Syntax(CurrentPosition, "expected layout"));
            return false;
        }

        SkipLayout(ref reader);
        result = default;
        return true;
    }

    /// <summary>
    /// §5.2: Requires whitespace (space/tab) or comments within a statement header.
    /// Newlines are not permitted inside a statement header.
    /// </summary>
    private bool TryRequireHeaderLayout(ref SequenceReader<byte> reader, bool isFinal, out StepResult result)
    {
        if (!reader.TryPeek(out byte b))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.UnexpectedEndOfInput(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (!Lexical.IsWhitespace(b) && b != Syntax.CommentStart)
        {
            if (b == Lexical.Newline || b == Lexical.CarriageReturn)
            {
                result = StepResult.Error(PaktParseError.Syntax(CurrentPosition, "newline not permitted in statement header"));
                return false;
            }

            result = StepResult.Error(PaktParseError.MissingLayout(CurrentPosition, "expected whitespace in statement header"));
            return false;
        }

        SkipHeaderLayout(ref reader);
        result = default;
        return true;
    }

    /// <summary>
    /// Skips whitespace (space/tab) and comments within a statement header.
    /// Does not consume newlines.
    /// </summary>
    private void SkipHeaderLayout(ref SequenceReader<byte> reader)
    {
        while (reader.TryPeek(out byte b))
        {
            if (Lexical.IsWhitespace(b))
            {
                reader.Advance(1);
                _cursor.Advance(b);
                continue;
            }

            if (b == Syntax.CommentStart)
            {
                SkipComment(ref reader);
                continue;
            }

            break;
        }
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

    /// <summary>
    /// Compares a token to an expected byte sequence.
    /// Byte-by-byte loop — optimal for short keywords (nil, true, false).
    /// Correct across segment boundaries. Short-circuits on first mismatch.
    /// </summary>
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