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
                if (_phase is PaktReaderPhase.Start or PaktReaderPhase.ExpectStatementOrEnd)
                {
                    _tokenType = PaktTokenType.EndOfUnit;
                    _phase = PaktReaderPhase.Done;
                    _valueSequence = default;
                    return true;
                }

                // Streaming collection at EOF — implicit close
                if (_phase == PaktReaderPhase.InAssignValue && _isStreaming && _containerStack.CurrentDepth > 0)
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
            PaktReaderPhase.InAssignValue
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
                        if (nesting == 0)
                        {
                            // '=' at depth 0 — annotation ends here (statement operator)
                            goto AnnotationEnd;
                        }
                        // '=' inside a composite type (e.g. <str = str>) — skip
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
            _consumed++;
            _bytePositionInLine++;
            _tokenType = PaktTokenType.AssignOperator;
            _valueSequence = _sequence.Slice(_tokenStartIndex, 1);
            _isStreaming = false;
            _phase = PaktReaderPhase.InAssignValue;
            return true;
        }

        ThrowSyntax($"Expected '=', got 0x{b:X2}");
        return false;
    }

    // ───────────────────── Value Dispatch ─────────────────────

    private bool ConsumeValue()
    {
        byte b = _buffer[_consumed];

        // Streaming prefix: ~[ or ~<
        if (b == PaktConstants.Tilde)
        {
            if (!TryPeek(1, out byte streamNext))
            {
                if (IsLastSpan) ThrowSyntax("Unexpected '~' at end of input");
                return false;
            }

            _consumed++; // skip ~
            _bytePositionInLine++;
            _isStreaming = true;
            _tokenStartIndex = _totalConsumed + _consumed; // point to [ or <

            if (streamNext == PaktConstants.LBrack)
                return OpenContainer(PaktTokenType.ListStart, ContainerKind.List);
            if (streamNext == PaktConstants.LAngle)
                return OpenContainer(PaktTokenType.MapStart, ContainerKind.Map);

            ThrowSyntax($"Expected '[' or '<' after '~', got 0x{streamNext:X2}");
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

        // Map entry bind — = inside map context
        if (b == PaktConstants.EqualsSign)
        {
            if (_containerStack.CurrentDepth > 0 && _containerStack.Peek() == ContainerKind.Map)
            {
                _consumed++;
                _bytePositionInLine++;
                _tokenType = PaktTokenType.MapEntryBind;
                _valueSequence = _sequence.Slice(_tokenStartIndex, 1);
                return true;
            }
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

        // Number (digit or minus) or keyword (true/false/nil)
        // Unified: scan unquoted value with SIMD, then classify
        if (PaktConstants.IsDigit(b) || b == PaktConstants.Minus || PaktConstants.IsIdentStart(b))
            return ConsumeUnquotedValue();

        // Atom prefix: |ident
        if (b == PaktConstants.Pipe)
            return ConsumeAtom();

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

    // ConsumeIdentifierOrKeyword removed — absorbed into ConsumeUnquotedValue

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

    /// <summary>
    /// Consume an unquoted value using SIMD-accelerated boundary detection,
    /// then classify the captured span. Handles numbers, dates, timestamps,
    /// UUIDs, booleans, and nil in a single unified path.
    /// </summary>
    private bool ConsumeUnquotedValue()
    {
        long absStart = _totalConsumed + _consumed;
        int totalLen = 0;

        // Vectorized scan — find end of value using SIMD IndexOfAny
        while (true)
        {
            ReadOnlySpan<byte> remaining = _buffer[_consumed..];
            int idx = remaining.IndexOfAny(PaktConstants.ValueStopBytes);

            if (idx >= 0)
            {
                _consumed += idx;
                _bytePositionInLine += idx;
                totalLen += idx;
                break;
            }

            // Consumed entire segment
            totalLen += remaining.Length;
            _consumed += remaining.Length;
            _bytePositionInLine += remaining.Length;

            if (!_isMultiSegment || _isLastSegment)
                break;

            if (!GetNextSpan())
                break;
        }

        if (totalLen == 0)
        {
            ThrowSyntax("Expected value");
            return false;
        }

        // Step 2: Classify from captured span
        ReadOnlySequence<byte> valSeq = _sequence.Slice(absStart, totalLen);

        if (valSeq.IsSingleSegment)
        {
            _tokenType = ClassifyUnquotedValue(valSeq.FirstSpan);
        }
        else
        {
            Span<byte> tmp = stackalloc byte[(int)valSeq.Length];
            valSeq.CopyTo(tmp);
            _tokenType = ClassifyUnquotedValue(tmp);
        }

        _valueSequence = valSeq;
        CompleteScalar();
        return true;
    }

    /// <summary>
    /// Classify an unquoted value span. Handles keywords (true/false/nil),
    /// fixed-length patterns (date/uuid), and numeric types using vectorized
    /// <see cref="MemoryExtensions.Contains{T}"/> checks.
    /// </summary>
    private static PaktTokenType ClassifyUnquotedValue(ReadOnlySpan<byte> v)
    {
        // Keywords — exact match
        if (v.SequenceEqual("true"u8) || v.SequenceEqual("false"u8))
            return PaktTokenType.Bool;
        if (v.SequenceEqual("nil"u8))
            return PaktTokenType.Nil;

        // Fixed-length patterns
        if (v.Length == 10 && v[4] == (byte)'-' && v[7] == (byte)'-')
            return PaktTokenType.Date;
        if (v.Length >= 32 && v[8] == (byte)'-' && v[13] == (byte)'-'
            && v[18] == (byte)'-' && v[23] == (byte)'-')
            return PaktTokenType.Uuid;

        // Vectorized presence checks on bounded span
        if (v.Contains((byte)'T') || v.Contains((byte)'Z') || v.Contains((byte)':'))
            return PaktTokenType.Timestamp;
        if (v.Contains((byte)'e') || v.Contains((byte)'E'))
        {
            // Could be float exponent or hex prefix — check for 0x
            if (v.Length >= 2 && v[0] == (byte)'0' && v[1] is (byte)'x' or (byte)'X')
                return PaktTokenType.Int;
            return PaktTokenType.Float;
        }
        if (v.Contains((byte)'.'))
            return PaktTokenType.Decimal;

        // Base prefix check (0x, 0b, 0o)
        if (v.Length >= 2 && v[0] == (byte)'0' && v[1] is (byte)'x' or (byte)'X' or (byte)'b' or (byte)'B' or (byte)'o' or (byte)'O')
            return PaktTokenType.Int;

        // Check if it starts with digit or minus — it's an int
        if (v.Length > 0 && (PaktConstants.IsDigit(v[0]) || v[0] == (byte)'-'))
            return PaktTokenType.Int;

        // Unknown identifier in value position
        ThrowUnknownValue(v);
        return PaktTokenType.None; // unreachable
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowUnknownValue(ReadOnlySpan<byte> v) =>
        throw PaktParseError.Syntax(default,
            $"Unknown identifier in value position: '{System.Text.Encoding.UTF8.GetString(v)}'").ToException();

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