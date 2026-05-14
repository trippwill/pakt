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
        int start = _consumed;
        int nesting = 0;
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
                    if (nesting == 0 && _consumed + 1 < local.Length && local[_consumed + 1] == PaktConstants.LAngle)
                    {
                        // This is '<<' — annotation ends here
                        goto AnnotationEnd;
                    }
                    nesting++;
                    _consumed++;
                    _bytePositionInLine++;
                    break;

                case PaktConstants.EqualsSign:
                    if (_consumed + 1 < local.Length && local[_consumed + 1] == PaktConstants.RAngle)
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

        // Ran out of buffer inside annotation
        if (IsLastSpan)
            ThrowUnexpectedEndOfInput();
        return false; // need more data

    AnnotationEnd:
        int len = _consumed - start;
        // Trim trailing whitespace from annotation
        while (len > 0 && PaktConstants.IsLayout(local[start + len - 1]))
            len--;

        _tokenType = PaktTokenType.TypeAnnotationStart;
        _valueSequence = _sequence.Slice(_totalConsumed + start, len);
        _phase = PaktReaderPhase.ExpectOperator;
        return true;
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
            // Check it's not '=>'
            if (_consumed + 1 < _buffer.Length && _buffer[_consumed + 1] == PaktConstants.RAngle)
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
            if (_consumed + 1 >= _buffer.Length)
            {
                if (IsLastSpan)
                    ThrowSyntax("Unexpected '<' at end of input — expected '<<'");
                return false; // need more data
            }

            if (_buffer[_consumed + 1] != PaktConstants.LAngle)
                ThrowSyntax("Expected '<<' pack operator");

            _consumed += 2;
            _bytePositionInLine += 2;
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
        if (b == PaktConstants.EqualsSign && _consumed + 1 < _buffer.Length
            && _buffer[_consumed + 1] == PaktConstants.RAngle)
        {
            _consumed += 2;
            _bytePositionInLine += 2;
            _tokenType = PaktTokenType.MapEntryBind;
            _valueSequence = _sequence.Slice(_tokenStartIndex, 2);
            return true;
        }

        // String literals
        if (b == PaktConstants.SingleQuote)
            return ConsumeString();

        // Raw string: r'...'
        if (b == PaktConstants.LowerR && _consumed + 1 < _buffer.Length
            && _buffer[_consumed + 1] == PaktConstants.SingleQuote)
            return ConsumeRawString();

        // Binary: x'...' or b'...'
        if ((b == PaktConstants.LowerX || b == PaktConstants.LowerB)
            && _consumed + 1 < _buffer.Length
            && _buffer[_consumed + 1] == PaktConstants.SingleQuote)
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
        int start = _consumed;
        _consumed++;
        _bytePositionInLine++;

        while (_consumed < _buffer.Length && PaktConstants.IsIdentPart(_buffer[_consumed]))
        {
            _consumed++;
            _bytePositionInLine++;
        }

        if (_consumed >= _buffer.Length && !IsLastSpan)
        {
            // Ident may continue in next segment — need more data
            // For now: only handle single-segment idents (Phase 6 handles multi-seg)
            return false;
        }

        int len = _consumed - start;
        _tokenType = tokenType;
        _valueSequence = _sequence.Slice(_totalConsumed + start, len);
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
        int start = _consumed;
        _consumed++;
        _bytePositionInLine++;

        while (_consumed < _buffer.Length && PaktConstants.IsIdentPart(_buffer[_consumed]))
        {
            _consumed++;
            _bytePositionInLine++;
        }

        int len = _consumed - start;
        ReadOnlySpan<byte> ident = _buffer.Slice(start, len);

        if (ident.SequenceEqual("true"u8) || ident.SequenceEqual("false"u8))
        {
            _tokenType = PaktTokenType.Bool;
        }
        else if (ident.SequenceEqual("nil"u8))
        {
            _tokenType = PaktTokenType.Nil;
        }
        else
        {
            ThrowSyntax("Unknown identifier in value position");
            return false;
        }

        _valueSequence = _sequence.Slice(_totalConsumed + start, len);

        // After scalar at depth 0 in assign, statement done
        if (_containerStack.CurrentDepth == 0 && _phase == PaktReaderPhase.InAssignValue)
            _phase = PaktReaderPhase.ExpectStatementOrEnd;

        return true;
    }

    // ───────────────────── Stub Consumers (Phase 4) ─────────────────────

    private bool ConsumeString()
    {
        // Stub — Phase 4 will implement full string scanning
        ThrowSyntax("String scanning not yet implemented");
        return false;
    }

    private bool ConsumeRawString()
    {
        ThrowSyntax("Raw string scanning not yet implemented");
        return false;
    }

    private bool ConsumeBinary()
    {
        ThrowSyntax("Binary scanning not yet implemented");
        return false;
    }

    private bool ConsumeNumber()
    {
        // Simple number scanner — find delimiter boundary
        int start = _consumed;

        while (_consumed < _buffer.Length)
        {
            if (PaktConstants.Delimiters.Contains(_buffer[_consumed]))
                break;
            _consumed++;
            _bytePositionInLine++;
        }

        if (_consumed == start)
        {
            ThrowSyntax("Expected number");
            return false;
        }

        int len = _consumed - start;
        _tokenType = ClassifyNumber(_buffer.Slice(start, len));
        _valueSequence = _sequence.Slice(_totalConsumed + start, len);

        if (_containerStack.CurrentDepth == 0 && _phase == PaktReaderPhase.InAssignValue)
            _phase = PaktReaderPhase.ExpectStatementOrEnd;

        return true;
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

    // ───────────────────── Number Classification ─────────────────────

    private static PaktTokenType ClassifyNumber(ReadOnlySpan<byte> span)
    {
        int i = 0;
        if (i < span.Length && span[i] == (byte)'-') i++;

        bool hasDot = false;
        bool hasExp = false;

        for (; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)'.') hasDot = true;
            else if (b is (byte)'e' or (byte)'E') hasExp = true;
            else if (b is (byte)'x' or (byte)'o') return PaktTokenType.Int;
        }

        if (hasExp) return PaktTokenType.Float;
        if (hasDot) return PaktTokenType.Decimal;
        return PaktTokenType.Int;
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
