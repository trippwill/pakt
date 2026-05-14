using System.Buffers;
using System.Runtime.CompilerServices;

namespace Pakt;

public ref partial struct PaktReader
{
    /// <summary>
    /// Advance to the next token. Returns <c>false</c> when no more tokens
    /// are available (end of input or need more data for streaming).
    /// </summary>
    public bool Read()
    {
        if (!SkipLayout())
        {
            if (IsLastSpan)
            {
                // End of input
                if (_phase is PaktReaderPhase.Start or PaktReaderPhase.ExpectStatementOrEnd
                    or PaktReaderPhase.InPackValue)
                {
                    _tokenType = PaktTokenType.EndOfUnit;
                    _phase = PaktReaderPhase.Done;
                    _valueSequence = default;
                    return true;
                }

                if (_phase == PaktReaderPhase.Done)
                    return false;

                ThrowUnexpectedEndOfInput();
            }

            return false; // need more data
        }

        _tokenStartIndex = _totalConsumed + _consumed;

        return _phase switch
        {
            PaktReaderPhase.Start or PaktReaderPhase.ExpectStatementOrEnd
                => ConsumeStatementOrEnd(),
            PaktReaderPhase.InAnnotation
                => ConsumeAnnotationToken(),
            PaktReaderPhase.ExpectOperator
                => ConsumeOperator(),
            PaktReaderPhase.InAssignValue or PaktReaderPhase.InPackValue
                => ConsumeValue(),
            PaktReaderPhase.Done
                => false,
            _ => false,
        };
    }

    // ───────────────────── Layout Skipping ─────────────────────

    /// <summary>
    /// Skip whitespace, newlines, and comments. Returns <c>true</c> when
    /// a non-layout byte is ready at <c>_buffer[_consumed]</c>.
    /// </summary>
    private bool SkipLayout()
    {
        while (true)
        {
            ReadOnlySpan<byte> local = _buffer;

            while (_consumed < local.Length)
            {
                byte b = local[_consumed];

                if (b == PaktConstants.Space || b == PaktConstants.Tab)
                {
                    _consumed++;
                    _bytePositionInLine++;
                    continue;
                }

                if (b == PaktConstants.LF)
                {
                    _consumed++;
                    _lineNumber++;
                    _bytePositionInLine = 0;
                    continue;
                }

                if (b == PaktConstants.CR)
                {
                    _consumed++;
                    _lineNumber++;
                    _bytePositionInLine = 0;
                    if (_consumed < local.Length && local[_consumed] == PaktConstants.LF)
                    {
                        _consumed++;
                    }
                    continue;
                }

                if (b == PaktConstants.Hash)
                {
                    if (!SkipComment())
                        return false; // need more data (comment at buffer end)
                    continue;
                }

                // Non-layout byte ready
                return true;
            }

            // Exhausted current segment
            if (!GetNextSpan())
                return false;
        }
    }

    /// <summary>
    /// Skip a line comment starting at '#'. Returns false if buffer exhausted
    /// mid-comment on a non-final block.
    /// </summary>
    private bool SkipComment()
    {
        _consumed++; // skip #
        _bytePositionInLine++;

        ReadOnlySpan<byte> local = _buffer;
        while (_consumed < local.Length)
        {
            byte b = local[_consumed];
            if (b == PaktConstants.LF || b == PaktConstants.CR)
                return true; // comment ended, newline will be consumed by SkipLayout

            if (b == PaktConstants.Nul)
                ThrowSyntax("NUL byte inside comment");

            _consumed++;
            _bytePositionInLine++;
        }

        // Hit end of segment inside comment
        if (IsLastSpan)
            return true; // comment at end of input is fine

        // For multi-segment: try next segment
        if (GetNextSpan())
            return SkipComment(); // continue comment in next segment

        return false; // need more data
    }

    // ───────────────────── Statement / End ─────────────────────

    private bool ConsumeStatementOrEnd()
    {
        byte b = _buffer[_consumed];

        // NUL = end of unit
        if (b == PaktConstants.Nul)
        {
            _consumed++;
            _bytePositionInLine++;
            _tokenType = PaktTokenType.EndOfUnit;
            _valueSequence = default;
            _phase = PaktReaderPhase.ExpectStatementOrEnd; // allow more units after NUL
            return true;
        }

        // Semicolon at statement level (trailing from pack)
        if (b == PaktConstants.Semicolon && _isPack)
        {
            _consumed++;
            _bytePositionInLine++;
            // Consume consecutive semicolons
            while (_consumed < _buffer.Length && _buffer[_consumed] == PaktConstants.Semicolon)
            {
                _consumed++;
                _bytePositionInLine++;
            }
            // Re-enter: look for next statement or end
            return Read();
        }

        // Must be an identifier (statement name)
        if (!PaktConstants.IsIdentStart(b))
            ThrowSyntax($"Expected statement name or end of unit, got 0x{b:X2}");

        return ConsumeIdentifier(PaktTokenType.StatementName, PaktReaderPhase.InAnnotation);
    }

    // ───────────────────── Annotation ─────────────────────

    private bool ConsumeAnnotationToken()
    {
        byte b = _buffer[_consumed];

        // First token in annotation must be preceded by ':'
        if (_annotationNesting == 0 && _tokenType == PaktTokenType.StatementName)
        {
            if (b != PaktConstants.Colon)
                ThrowSyntax("Expected ':' after statement name");
            _consumed++;
            _bytePositionInLine++;
            // Skip layout after colon, then read the annotation content
            if (!SkipLayout())
                return false;
            b = _buffer[_consumed];
        }

        // Scan the type annotation as a single span until we find the operator
        return ConsumeTypeAnnotation();
    }

    /// <summary>
    /// Scan the type annotation bytes and emit TypeAnnotationStart.
    /// The annotation is everything from the current position until we find
    /// '=' or '&lt;&lt;' at nesting depth 0.
    /// </summary>
    private bool ConsumeTypeAnnotation()
    {
        long absStart = _totalConsumed + _consumed;
        int nesting = 0;

        while (true)
        {
            ReadOnlySpan<byte> local = _buffer;
            while (_consumed < local.Length)
            {
                byte b = local[_consumed];

            switch (b)
            {
                case PaktConstants.LBrace:
                case PaktConstants.LParen:
                case PaktConstants.LBrack:
                    nesting++;
                    _consumed++;
                    _bytePositionInLine++;
                    break;

                case PaktConstants.RBrace:
                case PaktConstants.RParen:
                case PaktConstants.RBrack:
                case PaktConstants.RAngle:
                    nesting--;
                    _consumed++;
                    _bytePositionInLine++;
                    break;

                case PaktConstants.LAngle:
                    // Could be '<' in map type or '<<' pack operator
                    if (nesting == 0 && TryPeek(1, out byte laNext) && laNext == PaktConstants.LAngle)
                    {
                        // This is '<<' — annotation ends here
                        goto AnnotationEnd;
                    }
                    nesting++;
                    _consumed++;
                    _bytePositionInLine++;
                    break;

                case PaktConstants.EqualsSign:
                    if (TryPeek(1, out byte eqNextA) && eqNextA == PaktConstants.RAngle)
                    {
                        // '=>' — map binding inside annotation (or operator at depth 0)
                        // At nesting 0, this should not appear (operator is '=' not '=>')
                        _consumed += 2;
                        _bytePositionInLine += 2;
                        break;
                    }
                    if (nesting == 0)
                    {
                        // Plain '=' — annotation ends here
                        goto AnnotationEnd;
                    }
                    _consumed++;
                    _bytePositionInLine++;
                    break;

                default:
                    // Skip layout inside annotation, ident chars, '?', '|', ':'
                    _consumed++;
                    if (b == PaktConstants.LF) { _lineNumber++; _bytePositionInLine = 0; }
                    else _bytePositionInLine++;
                    break;
            }
        }

            // Exhausted current segment — try next
            if (!GetNextSpan())
            {
                if (IsLastSpan)
                    ThrowUnexpectedEndOfInput();
                return false; // need more data
            }
        }

    AnnotationEnd:
        long absEnd = _totalConsumed + _consumed;
        long len = absEnd - absStart;
        // Trim trailing layout from the annotation sequence
        // For simplicity, trim from the sequence slice
        ReadOnlySequence<byte> annotSeq = _sequence.Slice(absStart, len);
        // Trim trailing whitespace
        while (len > 0)
        {
            byte last = GetByteAt(absStart + len - 1);
            if (!PaktConstants.IsLayout(last)) break;
            len--;
        }

        _tokenType = PaktTokenType.TypeAnnotationStart;
        _valueSequence = _sequence.Slice(absStart, len);
        _phase = PaktReaderPhase.ExpectOperator;
        return true;
    }

    /// <summary>Get a byte at an absolute position in the sequence.</summary>
    private byte GetByteAt(long absolutePosition)
    {
        var pos = _sequence.GetPosition(absolutePosition);
        foreach (var mem in _sequence.Slice(pos, 1))
        {
            return mem.Span[0];
        }
        return 0;
    }

    // ───────────────────── Operator ─────────────────────

    private bool ConsumeOperator()
    {
        // Skip any layout before operator
        if (!SkipLayout())
            return false;

        byte b = _buffer[_consumed];

        if (b == PaktConstants.EqualsSign)
        {
            if (TryPeek(1, out byte eqNext) && eqNext == PaktConstants.RAngle)
                ThrowSyntax("Unexpected '=>' — expected '=' or '<<'");

            _consumed++;
            _bytePositionInLine++;
            _tokenType = PaktTokenType.AssignOperator;
            _valueSequence = _sequence.Slice(_tokenStartIndex, 1);
            _isPack = false;
            _phase = PaktReaderPhase.InAssignValue;
            return true;
        }

        if (b == PaktConstants.LAngle)
        {
            if (!TryPeek(1, out byte packNext))
            {
                if (IsLastSpan)
                    ThrowSyntax("Unexpected '<' at end of input — expected '<<'");
                return false;
            }

            if (packNext != PaktConstants.LAngle)
                ThrowSyntax("Expected '<<' pack operator");

            // Consume both '<' bytes — may span segments
            _consumed++;
            _bytePositionInLine++;
            if (_consumed >= _buffer.Length) GetNextSpan();
            _consumed++;
            _bytePositionInLine++;
            _tokenType = PaktTokenType.PackOperator;
            _valueSequence = _sequence.Slice(_tokenStartIndex, 2);
            _isPack = true;
            _phase = PaktReaderPhase.InPackValue;
            return true;
        }

        ThrowSyntax($"Expected '=' or '<<', got 0x{b:X2}");
        return false;
    }

    // ───────────────────── Value Dispatch ─────────────────────

    private bool ConsumeValue()
    {
        byte b = _buffer[_consumed];

        // Pack termination checks
        if (_phase == PaktReaderPhase.InPackValue && _containerStack.CurrentDepth == 0)
        {
            if (b == PaktConstants.Semicolon)
            {
                // Consume semicolon run
                while (_consumed < _buffer.Length && _buffer[_consumed] == PaktConstants.Semicolon)
                {
                    _consumed++;
                    _bytePositionInLine++;
                }
                _phase = PaktReaderPhase.ExpectStatementOrEnd;
                return Read(); // continue to next statement or end
            }

            if (b == PaktConstants.Nul)
            {
                _consumed++;
                _bytePositionInLine++;
                _tokenType = PaktTokenType.EndOfUnit;
                _valueSequence = default;
                _phase = PaktReaderPhase.ExpectStatementOrEnd;
                return true;
            }
        }

        // Structural delimiters
        switch (b)
        {
            case PaktConstants.LBrace:
                return OpenContainer(PaktTokenType.StructStart, ContainerKind.Struct);
            case PaktConstants.RBrace:
                return CloseContainer(PaktTokenType.StructEnd);
            case PaktConstants.LParen:
                return OpenContainer(PaktTokenType.TupleStart, ContainerKind.Tuple);
            case PaktConstants.RParen:
                return CloseContainer(PaktTokenType.TupleEnd);
            case PaktConstants.LBrack:
                return OpenContainer(PaktTokenType.ListStart, ContainerKind.List);
            case PaktConstants.RBrack:
                return CloseContainer(PaktTokenType.ListEnd);
            case PaktConstants.LAngle:
                return OpenContainer(PaktTokenType.MapStart, ContainerKind.Map);
            case PaktConstants.RAngle:
                return CloseContainer(PaktTokenType.MapEnd);
        }

        // Map entry bind
        if (b == PaktConstants.EqualsSign && TryPeek(1, out byte next) && next == PaktConstants.RAngle)
        {
            _consumed++;
            _bytePositionInLine++;
            if (_consumed >= _buffer.Length) GetNextSpan();
            _consumed++;
            _bytePositionInLine++;
            _tokenType = PaktTokenType.MapEntryBind;
            _valueSequence = _sequence.Slice(_tokenStartIndex, 2);
            return true;
        }

        // String literals
        if (b == PaktConstants.SingleQuote)
            return ConsumeString();

        // Raw string: r'...'
        if (b == PaktConstants.LowerR && TryPeek(1, out byte rNext) && rNext == PaktConstants.SingleQuote)
            return ConsumeRawString();

        // Binary: x'...' or b'...'
        if ((b == PaktConstants.LowerX || b == PaktConstants.LowerB)
            && TryPeek(1, out byte bNext) && bNext == PaktConstants.SingleQuote)
            return ConsumeBinary();

        // Number (digit or minus)
        if (PaktConstants.IsDigit(b) || b == PaktConstants.Minus)
            return ConsumeNumber();

        // Atom prefix: |ident
        if (b == PaktConstants.Pipe)
            return ConsumeAtom();

        // Identifier: true, false, nil, or ident value
        if (PaktConstants.IsIdentStart(b))
            return ConsumeIdentifierOrKeyword();

        ThrowSyntax($"Unexpected byte 0x{b:X2} in value position");
        return false;
    }

    // ───────────────────── Container Open/Close ─────────────────────

    private bool OpenContainer(PaktTokenType type, ContainerKind kind)
    {
        if (_containerStack.CurrentDepth >= _options.MaxNestingDepth)
            ThrowNestingExceeded();

        _consumed++;
        _bytePositionInLine++;
        _containerStack.Push(kind);
        _tokenType = type;
        _valueSequence = _sequence.Slice(_tokenStartIndex, 1);
        return true;
    }

    private bool CloseContainer(PaktTokenType type)
    {
        _consumed++;
        _bytePositionInLine++;
        _containerStack.Pop();
        _tokenType = type;
        _valueSequence = _sequence.Slice(_tokenStartIndex, 1);

        // After closing at depth 0 in assign mode, statement is done
        if (_containerStack.CurrentDepth == 0 && _phase == PaktReaderPhase.InAssignValue)
        {
            _phase = PaktReaderPhase.ExpectStatementOrEnd;
        }

        return true;
    }

    // ───────────────────── Identifier ─────────────────────

    private bool ConsumeIdentifier(PaktTokenType tokenType, PaktReaderPhase nextPhase)
    {
        long absStart = _totalConsumed + _consumed;
        _consumed++; // consume first ident char
        _bytePositionInLine++;

        int partLen = ScanIdentParts();
        int totalLen = 1 + partLen;

        if (_consumed >= _buffer.Length && !IsLastSpan)
            return false; // ident may continue in next buffer refill

        _tokenType = tokenType;
        _valueSequence = _sequence.Slice(absStart, totalLen);
        _phase = nextPhase;

        if (tokenType == PaktTokenType.StatementName)
        {
            _statementCount++;
            if (_statementCount > _options.MaxStatementCount)
                ThrowSyntax("Statement count limit exceeded");
        }

        return true;
    }

    private bool ConsumeIdentifierOrKeyword()
    {
        long absStart = _totalConsumed + _consumed;
        _consumed++;
        _bytePositionInLine++;

        int partLen = ScanIdentParts();
        int totalLen = 1 + partLen;

        ReadOnlySequence<byte> identSeq = _sequence.Slice(absStart, totalLen);

        // Classify — for single-segment (common case) use span directly
        PaktTokenType identType;
        if (identSeq.IsSingleSegment)
        {
            ReadOnlySpan<byte> ident = identSeq.FirstSpan;
            if (ident.SequenceEqual("true"u8) || ident.SequenceEqual("false"u8))
                identType = PaktTokenType.Bool;
            else if (ident.SequenceEqual("nil"u8))
                identType = PaktTokenType.Nil;
            else
            {
                ThrowSyntax("Unknown identifier in value position");
                return false;
            }
        }
        else
        {
            // Multi-segment: copy to stack for comparison
            Span<byte> tmp = stackalloc byte[(int)identSeq.Length];
            identSeq.CopyTo(tmp);
            if (tmp.SequenceEqual("true"u8) || tmp.SequenceEqual("false"u8))
                identType = PaktTokenType.Bool;
            else if (tmp.SequenceEqual("nil"u8))
                identType = PaktTokenType.Nil;
            else
            {
                ThrowSyntax("Unknown identifier in value position");
                return false;
            }
        }

        _tokenType = identType;
        _valueSequence = identSeq;
        CompleteScalar();
        return true;
    }

    // ───────────────────── Stub Consumers (Phase 4) ─────────────────────

    private bool ConsumeString()
    {
        // Single-quoted string: '...' or multiline '''...'''
        long absStart = _totalConsumed + _consumed;
        _consumed++; // skip opening '
        _bytePositionInLine++;

        // Check for triple-quote (multiline)
        if (TryPeek(0, out byte p1) && p1 == PaktConstants.SingleQuote
            && TryPeek(1, out byte p2) && p2 == PaktConstants.SingleQuote)
        {
            _consumed += 2;
            _bytePositionInLine += 2;
            return ConsumeMultiLineString(absStart, isRaw: false);
        }

        // Single-line string: scan for closing quote, backslash, or newline
        bool escaped = false;
        while (true)
        {
            while (_consumed < _buffer.Length)
            {
                byte b = _buffer[_consumed];

                if (b == PaktConstants.Nul)
                    ThrowSyntax("NUL byte inside string");

                if (b == PaktConstants.SingleQuote)
                {
                    _consumed++;
                    _bytePositionInLine++;
                    long len = (_totalConsumed + _consumed) - absStart;
                    _tokenType = PaktTokenType.String;
                    _valueSequence = _sequence.Slice(absStart, len);
                    _valueIsEscaped = escaped;
                    CompleteScalar();
                    return true;
                }

                if (b == PaktConstants.Backslash)
                {
                    escaped = true;
                    _consumed++;
                    _bytePositionInLine++;
                    // Skip escaped char — may need next segment
                    if (_consumed >= _buffer.Length)
                    {
                        if (!GetNextSpan()) goto NeedMore;
                    }
                    _consumed++;
                    _bytePositionInLine++;
                    continue;
                }

                if (b == PaktConstants.LF || b == PaktConstants.CR)
                    ThrowSyntax("Newline inside single-line string");

                _consumed++;
                _bytePositionInLine++;
            }

            // Hit segment boundary — try next segment
            if (!GetNextSpan()) goto NeedMore;
        }

    NeedMore:
        if (IsLastSpan) ThrowSyntax("Unterminated string");
        return false;
    }

    private bool ConsumeRawString()
    {
        long absStart = _totalConsumed + _consumed;
        _consumed += 2; // skip r'
        _bytePositionInLine += 2;

        // Check for triple-quote
        if (TryPeek(0, out byte p1) && p1 == PaktConstants.SingleQuote
            && TryPeek(1, out byte p2) && p2 == PaktConstants.SingleQuote)
        {
            _consumed += 2;
            _bytePositionInLine += 2;
            return ConsumeMultiLineString(absStart, isRaw: true);
        }

        // Single-line raw: scan for closing quote only
        while (true)
        {
            while (_consumed < _buffer.Length)
            {
                byte b = _buffer[_consumed];

                if (b == PaktConstants.Nul)
                    ThrowSyntax("NUL byte inside string");

                if (b == PaktConstants.SingleQuote)
                {
                    _consumed++;
                    _bytePositionInLine++;
                    long len = (_totalConsumed + _consumed) - absStart;
                    _tokenType = PaktTokenType.String;
                    _valueSequence = _sequence.Slice(absStart, len);
                    _valueIsEscaped = false;
                    CompleteScalar();
                    return true;
                }

                if (b == PaktConstants.LF || b == PaktConstants.CR)
                    ThrowSyntax("Newline inside single-line string");

                _consumed++;
                _bytePositionInLine++;
            }

            if (!GetNextSpan()) break;
        }

        if (IsLastSpan) ThrowSyntax("Unterminated raw string");
        return false;
    }

    private bool ConsumeMultiLineString(long absStart, bool isRaw)
    {
        bool escaped = false;

        while (true)
        {
            while (_consumed < _buffer.Length)
            {
                byte b = _buffer[_consumed];

                if (b == PaktConstants.Nul)
                    ThrowSyntax("NUL byte inside string");

                if (b == PaktConstants.SingleQuote
                    && TryPeek(1, out byte q2) && q2 == PaktConstants.SingleQuote
                    && TryPeek(2, out byte q3) && q3 == PaktConstants.SingleQuote)
                {
                    _consumed += 3;
                    _bytePositionInLine += 3;
                    long len = (_totalConsumed + _consumed) - absStart;
                    _tokenType = PaktTokenType.String;
                    _valueSequence = _sequence.Slice(absStart, len);
                    _valueIsEscaped = escaped;
                    CompleteScalar();
                    return true;
                }

                if (!isRaw && b == PaktConstants.Backslash)
                {
                    escaped = true;
                    _consumed++;
                    _bytePositionInLine++;
                    if (_consumed >= _buffer.Length && !GetNextSpan()) goto NeedMore;
                    _consumed++;
                    _bytePositionInLine++;
                    continue;
                }

                if (b == PaktConstants.LF)
                {
                    _consumed++;
                    _lineNumber++;
                    _bytePositionInLine = 0;
                }
                else if (b == PaktConstants.CR)
                {
                    _consumed++;
                    _lineNumber++;
                    _bytePositionInLine = 0;
                    if (_consumed < _buffer.Length && _buffer[_consumed] == PaktConstants.LF)
                        _consumed++;
                    else if (TryPeek(0, out byte lf) && lf == PaktConstants.LF)
                        _consumed++;
                }
                else
                {
                    _consumed++;
                    _bytePositionInLine++;
                }
            }

            if (!GetNextSpan()) goto NeedMore;
        }

    NeedMore:
        if (IsLastSpan) ThrowSyntax("Unterminated multiline string");
        return false;
    }

    private bool ConsumeBinary()
    {
        long absStart = _totalConsumed + _consumed;
        _consumed += 2; // skip x' or b'
        _bytePositionInLine += 2;

        while (true)
        {
            while (_consumed < _buffer.Length)
            {
                byte b = _buffer[_consumed];

                if (b == PaktConstants.Nul)
                    ThrowSyntax("NUL byte inside binary literal");

                if (b == PaktConstants.SingleQuote)
                {
                    _consumed++;
                    _bytePositionInLine++;
                    long len = (_totalConsumed + _consumed) - absStart;
                    _tokenType = PaktTokenType.Binary;
                    _valueSequence = _sequence.Slice(absStart, len);
                    _valueIsEscaped = false;
                    CompleteScalar();
                    return true;
                }

                _consumed++;
                _bytePositionInLine++;
            }

            if (!GetNextSpan()) break;
        }

        if (IsLastSpan) ThrowSyntax("Unterminated binary literal");
        return false;
    }

    /// <summary>After a scalar value at depth 0 in assign mode, transition to next statement.</summary>
    private void CompleteScalar()
    {
        if (_containerStack.CurrentDepth == 0 && _phase == PaktReaderPhase.InAssignValue)
            _phase = PaktReaderPhase.ExpectStatementOrEnd;
    }

    private bool ConsumeNumber()
    {
        long absStart = _totalConsumed + _consumed;
        int len = ScanNumericValue();

        if (len == 0)
        {
            ThrowSyntax("Expected number");
            return false;
        }

        ReadOnlySequence<byte> numSeq = _sequence.Slice(absStart, len);

        if (numSeq.IsSingleSegment)
        {
            _tokenType = ClassifyNumericValue(numSeq.FirstSpan);
        }
        else
        {
            Span<byte> tmp = stackalloc byte[(int)numSeq.Length];
            numSeq.CopyTo(tmp);
            _tokenType = ClassifyNumericValue(tmp);
        }

        _valueSequence = numSeq;
        CompleteScalar();
        return true;
    }

    /// <summary>
    /// Scan a numeric-like value: numbers, dates, timestamps, UUIDs.
    /// These all start with a digit (or minus) and may contain digits, letters,
    /// colons, dashes, dots, plus, underscores — everything except layout and
    /// structural delimiters.
    /// </summary>
    private int ScanNumericValue()
    {
        int totalLen = 0;

        while (true)
        {
            ReadOnlySpan<byte> local = _buffer;

            while (_consumed < local.Length)
            {
                byte b = local[_consumed];
                // Stop at layout, structural delimiters, quotes, semicolons, NUL
                if (b is PaktConstants.Space or PaktConstants.Tab
                    or PaktConstants.LF or PaktConstants.CR
                    or PaktConstants.LBrace or PaktConstants.RBrace
                    or PaktConstants.LParen or PaktConstants.RParen
                    or PaktConstants.LBrack or PaktConstants.RBrack
                    or PaktConstants.LAngle or PaktConstants.RAngle
                    or PaktConstants.Pipe or PaktConstants.SingleQuote
                    or PaktConstants.Hash or PaktConstants.Semicolon
                    or PaktConstants.Nul
                    or PaktConstants.EqualsSign or PaktConstants.Question)
                {
                    return totalLen;
                }

                // Allow: digits, letters, colon, dash, dot, plus, underscore
                _consumed++;
                _bytePositionInLine++;
                totalLen++;
            }

            if (!_isMultiSegment || _isLastSegment)
                return totalLen;

            if (!GetNextSpan())
                return totalLen;
        }
    }

    /// <summary>
    /// Classify a numeric-like value span into Int, Decimal, Float,
    /// Date, Timestamp, or Uuid.
    /// </summary>
    private static PaktTokenType ClassifyNumericValue(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0) return PaktTokenType.Int;

        int i = 0;
        if (span[i] == (byte)'-') i++;

        bool hasColon = false;
        bool hasDot = false;
        bool hasExp = false;
        bool hasT = false;
        bool hasZ = false;
        int dashCount = 0;

        for (; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)':') hasColon = true;
            else if (b == (byte)'.') hasDot = true;
            else if (b is (byte)'e' or (byte)'E') hasExp = true;
            else if (b == (byte)'T') hasT = true;
            else if (b == (byte)'Z') hasZ = true;
            else if (b == (byte)'-' && i > 0) dashCount++;
            else if (b is (byte)'x' or (byte)'o') return PaktTokenType.Int;
        }

        if (hasT || hasZ || hasColon) return PaktTokenType.Timestamp;
        if (dashCount == 4 && span.Length >= 32) return PaktTokenType.Uuid;
        if (dashCount >= 2 && !hasDot && !hasExp) return PaktTokenType.Date;
        if (hasExp) return PaktTokenType.Float;
        if (hasDot) return PaktTokenType.Decimal;
        return PaktTokenType.Int;
    }

    private bool ConsumeAtom()
    {
        // |ident
        int start = _consumed;
        _consumed++; // skip |
        _bytePositionInLine++;

        if (_consumed >= _buffer.Length)
        {
            if (IsLastSpan) ThrowSyntax("Unexpected end of input after '|'");
            return false;
        }

        while (_consumed < _buffer.Length && PaktConstants.IsIdentPart(_buffer[_consumed]))
        {
            _consumed++;
            _bytePositionInLine++;
        }

        int len = _consumed - start;
        _tokenType = PaktTokenType.Atom;
        _valueSequence = _sequence.Slice(_totalConsumed + start, len);

        if (_containerStack.CurrentDepth == 0 && _phase == PaktReaderPhase.InAssignValue)
            _phase = PaktReaderPhase.ExpectStatementOrEnd;

        return true;
    }

    // ───────────────────── Segment Walking ─────────────────────

    /// <summary>
    /// Advance to the next segment of the sequence. Returns false if no more data.
    /// </summary>
    private bool GetNextSpan()
    {
        if (!_isMultiSegment || _isLastSegment)
            return false;

        _totalConsumed += _consumed;
        _consumed = 0;

        _currentPosition = _nextPosition;
        bool hasNext = _sequence.TryGet(ref _nextPosition, out ReadOnlyMemory<byte> memory, advance: true);

        if (!hasNext || memory.Length == 0)
        {
            _isLastSegment = true;
            _buffer = ReadOnlySpan<byte>.Empty;
            return false;
        }

        _buffer = memory.Span;

        // Check if this is the last non-empty segment
        SequencePosition peekNext = _nextPosition;
        if (!_sequence.TryGet(ref peekNext, out ReadOnlyMemory<byte> peekMem, advance: true) || peekMem.Length == 0)
        {
            _isLastSegment = true;
        }

        return true;
    }

    // ───────────────────── Error Helpers ─────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowSyntax(string message) =>
        throw PaktParseError.Syntax(
            new SourcePosition(_totalConsumed + _consumed, (int)_lineNumber, _bytePositionInLine),
            message).ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowUnexpectedEndOfInput() =>
        throw PaktParseError.UnexpectedEndOfInput(
            new SourcePosition(_totalConsumed + _consumed, (int)_lineNumber, _bytePositionInLine),
            $"Unexpected end of input in phase {_phase}").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowNestingExceeded() =>
        throw PaktParseError.NestingDepthExceeded(
            new SourcePosition(_totalConsumed + _consumed, (int)_lineNumber, _bytePositionInLine),
            "Maximum nesting depth exceeded").ToException();
}
