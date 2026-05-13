namespace Pakt;

/// <summary>
/// Internal state machine that maps lexical tokens to structural <see cref="PaktTokenType"/> tokens.
/// Designed to be shared between PaktMemoryReader and PaktStreamReader.
/// </summary>
internal sealed class PaktParserCore
{
    private ParserPhase _phase;
    private PaktTokenType _tokenType;
    private int _depth;
    private int _statementCount;
    private int _annotationNesting;
    private bool _isPack;
    private readonly int _maxNestingDepth;
    private readonly int _maxStatementCount;

    // Annotation byte-range tracking (offsets within the current buffer span).
    // Updated by the owning reader via TrackAnnotationToken / ResetAnnotationTracking.
    private int _annotationStart = -1;
    private int _annotationEnd = -1;

    public PaktParserCore(PaktReaderOptions options)
    {
        _maxNestingDepth = options.MaxNestingDepth;
        _maxStatementCount = options.MaxStatementCount;
    }

    // ───────────────────── public surface ─────────────────────

    internal enum ParserPhase : byte
    {
        ExpectStatementOrEnd,
        ExpectColon,
        InAnnotation,
        PendingAnnotationEnd,
        PendingOperator,
        PendingAnnotationStart,
        InAssignValue,
        InPackValue,
        Done,
    }

    public enum ProcessResult : byte
    {
        /// <summary>A structural token was produced.</summary>
        Emit,

        /// <summary>Token consumed internally; feed the next lexical token.</summary>
        ConsumeMore,

        /// <summary>
        /// Pack-mode lookahead required. The reader must peek at the next lexical
        /// token and call one of the <c>ResolvePackLookahead*</c> methods.
        /// </summary>
        NeedLookahead,
    }

    public PaktTokenType TokenType => _tokenType;
    public int Depth => _depth;
    public ParserPhase Phase => _phase;

    public int AnnotationStart => _annotationStart;
    public int AnnotationEnd => _annotationEnd;

    /// <summary>
    /// Check for pending structural tokens that don't require new lexical input.
    /// </summary>
    public bool TryEmitPending()
    {
        switch (_phase)
        {
            case ParserPhase.PendingAnnotationEnd:
                _tokenType = PaktTokenType.TypeAnnotationEnd;
                _phase = ParserPhase.PendingOperator;
                return true;

            case ParserPhase.PendingOperator:
                _tokenType = _isPack ? PaktTokenType.PackOperator : PaktTokenType.AssignOperator;
                _phase = _isPack ? ParserPhase.InPackValue : ParserPhase.InAssignValue;
                return true;

            case ParserPhase.PendingAnnotationStart:
                _tokenType = PaktTokenType.TypeAnnotationStart;
                _annotationNesting = 0;
                _annotationStart = -1;
                _annotationEnd = -1;
                _phase = ParserPhase.InAnnotation;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Process a lexical token and optionally produce a structural token.
    /// </summary>
    public ProcessResult ProcessToken(
        PaktLexicalToken token,
        ReadOnlySpan<byte> buffer,
        bool sawNewline)
    {
        return _phase switch
        {
            ParserPhase.ExpectStatementOrEnd => HandleStatementOrEnd(token),
            ParserPhase.ExpectColon => HandleColon(token),
            ParserPhase.InAnnotation => HandleAnnotation(token),
            ParserPhase.InAssignValue => HandleAssignValue(token, buffer),
            ParserPhase.InPackValue => HandlePackValue(token, buffer, sawNewline),
            _ => throw new InvalidOperationException($"ProcessToken called in phase {_phase}"),
        };
    }

    /// <summary>
    /// Handle end-of-input from the lexer.
    /// Returns <c>true</c> when parsing terminates normally (no more tokens).
    /// Throws on unexpected EOF.
    /// </summary>
    public bool HandleEndOfInput()
    {
        return _phase switch
        {
            ParserPhase.ExpectStatementOrEnd => SetDone(),
            ParserPhase.InPackValue when _depth == 0 => SetDone(),
            ParserPhase.Done => true,
            _ => throw PaktParseError.UnexpectedEndOfInput(default,
                $"Unexpected end of input in phase {_phase}").ToException(),
        };
    }

    // ── Pack lookahead resolution (called by the reader) ──

    /// <summary>
    /// Resolve pack lookahead: the held ident begins a new statement.
    /// </summary>
    public void ResolvePackLookaheadAsStatement()
    {
        _statementCount++;
        if (_statementCount > _maxStatementCount)
        {
            throw PaktParseError.Syntax(default, "Max statement count exceeded").ToException();
        }

        _tokenType = PaktTokenType.StatementName;
        _phase = ParserPhase.PendingAnnotationStart;
    }

    /// <summary>
    /// Resolve pack lookahead: the held ident is a value (not a statement).
    /// </summary>
    public PaktTokenType ResolvePackLookaheadAsValue(ReadOnlySpan<byte> identBytes)
    {
        _tokenType = ClassifyIdent(identBytes);
        _phase = ParserPhase.InPackValue;
        return _tokenType;
    }

    /// <summary>
    /// Resolve pack lookahead at end-of-input: held ident is a value, pack ends.
    /// </summary>
    public PaktTokenType ResolvePackLookaheadAtEndOfInput(ReadOnlySpan<byte> identBytes)
    {
        _tokenType = ClassifyIdent(identBytes);
        _phase = ParserPhase.Done;
        return _tokenType;
    }

    // ── Annotation byte-range helpers (driven by the reader) ──

    public void ResetAnnotationTracking()
    {
        _annotationStart = -1;
        _annotationEnd = -1;
    }

    public void TrackAnnotationToken(PaktLexicalToken token)
    {
        if (_annotationStart < 0)
        {
            _annotationStart = token.Offset;
        }

        _annotationEnd = token.Offset + token.Length;
    }

    // ───────────────────── phase handlers ─────────────────────

    private ProcessResult HandleStatementOrEnd(PaktLexicalToken token)
    {
        return token.Kind switch
        {
            PaktLexicalTokenKind.Nul => EmitSimple(PaktTokenType.EndOfUnit),
            PaktLexicalTokenKind.Ident => EmitStatementName(),
            _ => throw PaktParseError.Syntax(default,
                $"Expected statement name or NUL; got {token.Kind}").ToException(),
        };
    }

    private ProcessResult EmitStatementName()
    {
        _statementCount++;
        if (_statementCount > _maxStatementCount)
        {
            throw PaktParseError.Syntax(default, "Max statement count exceeded").ToException();
        }

        _tokenType = PaktTokenType.StatementName;
        _phase = ParserPhase.ExpectColon;
        return ProcessResult.Emit;
    }

    private ProcessResult HandleColon(PaktLexicalToken token)
    {
        if (token.Kind != PaktLexicalTokenKind.Colon)
        {
            throw PaktParseError.Syntax(default, $"Expected ':'; got {token.Kind}").ToException();
        }

        _tokenType = PaktTokenType.TypeAnnotationStart;
        _annotationNesting = 0;
        _annotationStart = -1;
        _annotationEnd = -1;
        _phase = ParserPhase.InAnnotation;
        return ProcessResult.Emit;
    }

    private ProcessResult HandleAnnotation(PaktLexicalToken token)
    {
        // = or << at nesting depth 0 terminates the annotation.
        if (_annotationNesting == 0)
        {
            if (token.Kind == PaktLexicalTokenKind.Assign)
            {
                _isPack = false;
                _phase = ParserPhase.PendingAnnotationEnd;
                return ProcessResult.ConsumeMore;
            }

            if (token.Kind == PaktLexicalTokenKind.Pack)
            {
                _isPack = true;
                _phase = ParserPhase.PendingAnnotationEnd;
                return ProcessResult.ConsumeMore;
            }
        }

        // Track nesting for paired delimiters inside the annotation.
        switch (token.Kind)
        {
            case PaktLexicalTokenKind.LBrace:
            case PaktLexicalTokenKind.LParen:
            case PaktLexicalTokenKind.LBrack:
            case PaktLexicalTokenKind.LAngle:
                _annotationNesting++;
                break;
            case PaktLexicalTokenKind.RBrace:
            case PaktLexicalTokenKind.RParen:
            case PaktLexicalTokenKind.RBrack:
            case PaktLexicalTokenKind.RAngle:
                _annotationNesting--;
                if (_annotationNesting < 0)
                {
                    throw PaktParseError.Syntax(default,
                        "Unmatched closing delimiter in type annotation").ToException();
                }

                break;
        }

        return ProcessResult.ConsumeMore;
    }

    private ProcessResult HandleAssignValue(PaktLexicalToken token, ReadOnlySpan<byte> buffer)
    {
        ClassifyValueToken(token, buffer);

        // After a complete scalar or composite-end at depth 0, the statement is done.
        if (_depth == 0 && !IsCompositeOpen(_tokenType))
        {
            _phase = ParserPhase.ExpectStatementOrEnd;
        }

        return ProcessResult.Emit;
    }

    private ProcessResult HandlePackValue(
        PaktLexicalToken token,
        ReadOnlySpan<byte> buffer,
        bool sawNewline)
    {
        // NUL at depth 0 ends the pack.
        if (token.Kind == PaktLexicalTokenKind.Nul && _depth == 0)
        {
            _tokenType = PaktTokenType.EndOfUnit;
            _phase = ParserPhase.ExpectStatementOrEnd;
            return ProcessResult.Emit;
        }

        // Ident at depth 0 preceded by a newline may start a new statement.
        if (token.Kind == PaktLexicalTokenKind.Ident && _depth == 0 && sawNewline)
        {
            return ProcessResult.NeedLookahead;
        }

        ClassifyValueToken(token, buffer);
        return ProcessResult.Emit;
    }

    // ───────────────────── token classification ─────────────────────

    private void ClassifyValueToken(PaktLexicalToken token, ReadOnlySpan<byte> buffer)
    {
        switch (token.Kind)
        {
            case PaktLexicalTokenKind.String:
                _tokenType = PaktTokenType.String;
                break;
            case PaktLexicalTokenKind.Number:
                _tokenType = ClassifyNumber(buffer.Slice(token.Offset, token.Length));
                break;
            case PaktLexicalTokenKind.Binary:
                _tokenType = PaktTokenType.Binary;
                break;
            case PaktLexicalTokenKind.AtomPrefix:
                _tokenType = PaktTokenType.Atom;
                break;
            case PaktLexicalTokenKind.Ident:
                _tokenType = ClassifyIdent(buffer.Slice(token.Offset, token.Length));
                break;
            case PaktLexicalTokenKind.Bind:
                _tokenType = PaktTokenType.MapEntryBind;
                break;
            case PaktLexicalTokenKind.Nul:
                ClassifyNulInValue();
                break;
            default:
                ClassifyDelimiterOrThrow(token.Kind);
                break;
        }
    }

    private void ClassifyNulInValue()
    {
        if (_depth > 0)
        {
            throw PaktParseError.Syntax(default, "NUL inside composite value").ToException();
        }

        _tokenType = PaktTokenType.EndOfUnit;
    }

    private void ClassifyDelimiterOrThrow(PaktLexicalTokenKind kind)
    {
        switch (kind)
        {
            case PaktLexicalTokenKind.LBrace:
                PushDepth();
                _tokenType = PaktTokenType.StructStart;
                break;
            case PaktLexicalTokenKind.RBrace:
                PopDepth();
                _tokenType = PaktTokenType.StructEnd;
                break;
            case PaktLexicalTokenKind.LParen:
                PushDepth();
                _tokenType = PaktTokenType.TupleStart;
                break;
            case PaktLexicalTokenKind.RParen:
                PopDepth();
                _tokenType = PaktTokenType.TupleEnd;
                break;
            case PaktLexicalTokenKind.LBrack:
                PushDepth();
                _tokenType = PaktTokenType.ListStart;
                break;
            case PaktLexicalTokenKind.RBrack:
                PopDepth();
                _tokenType = PaktTokenType.ListEnd;
                break;
            case PaktLexicalTokenKind.LAngle:
                PushDepth();
                _tokenType = PaktTokenType.MapStart;
                break;
            case PaktLexicalTokenKind.RAngle:
                PopDepth();
                _tokenType = PaktTokenType.MapEnd;
                break;
            default:
                throw PaktParseError.Syntax(default,
                    $"Unexpected token {kind} in value position").ToException();
        }
    }

    internal static PaktTokenType ClassifyNumber(ReadOnlySpan<byte> span)
    {
        int i = 0;
        if (i < span.Length && span[i] == (byte)'-')
        {
            i++;
        }

        bool hasDot = false;
        bool hasExp = false;

        for (; i < span.Length; i++)
        {
            byte b = span[i];
            if (b == (byte)'.')
            {
                hasDot = true;
            }
            else if (b is (byte)'e' or (byte)'E')
            {
                hasExp = true;
            }
            else if (b is (byte)'x' or (byte)'o')
            {
                // 0x hex or 0o octal — always integral
                return PaktTokenType.Int;
            }
        }

        if (hasExp)
        {
            return PaktTokenType.Float;
        }

        if (hasDot)
        {
            return PaktTokenType.Decimal;
        }

        return PaktTokenType.Int;
    }

    internal static PaktTokenType ClassifyIdent(ReadOnlySpan<byte> span)
    {
        if (span.SequenceEqual("true"u8) || span.SequenceEqual("false"u8))
        {
            return PaktTokenType.Bool;
        }

        if (span.SequenceEqual("nil"u8))
        {
            return PaktTokenType.Nil;
        }

        throw PaktParseError.Syntax(default, "Unknown identifier in value position").ToException();
    }

    // ───────────────────── helpers ─────────────────────

    private void PushDepth()
    {
        _depth++;
        if (_depth > _maxNestingDepth)
        {
            throw PaktParseError.NestingDepthExceeded(default,
                "Max nesting depth exceeded").ToException();
        }
    }

    private void PopDepth()
    {
        _depth--;
        if (_depth < 0)
        {
            throw PaktParseError.Syntax(default, "Unmatched closing delimiter").ToException();
        }
    }

    private static bool IsCompositeOpen(PaktTokenType type) => type is
        PaktTokenType.StructStart or PaktTokenType.TupleStart or
        PaktTokenType.ListStart or PaktTokenType.MapStart;

    private ProcessResult EmitSimple(PaktTokenType type)
    {
        _tokenType = type;
        return ProcessResult.Emit;
    }

    private bool SetDone()
    {
        _phase = ParserPhase.Done;
        return true;
    }
}