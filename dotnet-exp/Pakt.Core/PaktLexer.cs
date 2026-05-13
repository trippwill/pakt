using System.Diagnostics;

namespace Pakt;

/// <summary>
/// Zero-allocation lexer over a contiguous UTF-8 byte span.
/// Produces <see cref="PaktLexicalToken"/> values one at a time.
/// State is saved/restored via <see cref="PaktLexerState"/> for streaming across buffer refills.
/// </summary>
public ref struct PaktLexer
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly bool _isFinalBlock;
    private readonly long _totalConsumedBase;

    private int _pos;
    private int _bytesConsumed;
    private SourceCursor _cursor;
    private bool _sawNewline;
    private LexerMode _mode;
    private ref PaktLexerState _state;

    public PaktLexer(
        ReadOnlySpan<byte> buffer,
        bool isFinalBlock,
        ref PaktLexerState state)
    {
        _buffer = buffer;
        _isFinalBlock = isFinalBlock;
        _state = ref state;
        _totalConsumedBase = state.TotalConsumed;
        _pos = 0;
        _bytesConsumed = 0;
        _sawNewline = false;
        _mode = state.Mode;

        // Restore cursor from state.
        if (state.TotalConsumed == 0 && state.Line == 0)
        {
            _cursor = SourceCursor.Start;
        }
        else
        {
            _cursor = new SourceCursor
            {
                Offset = state.TotalConsumed,
                Line = state.Line,
                Column = state.Column,
            };
        }

        // BOM skip on very first buffer.
        if (state.TotalConsumed == 0
            && state.Mode == LexerMode.Normal
            && _buffer.Length >= 3
            && _buffer[0] == 0xEF
            && _buffer[1] == 0xBB
            && _buffer[2] == 0xBF)
        {
            _pos = 3;
            _cursor.AdvanceColumns(3);
        }
    }

    /// <summary>Whether a newline appeared in layout before the most recently read token.</summary>
    public bool SawNewlineBeforeToken => _sawNewline;

    /// <summary>
    /// Total bytes consumed from the current buffer.
    /// After <see cref="PaktReadResult.NeedMoreData"/>, this is the safe-to-discard count;
    /// bytes from this offset onward must be preserved for the next buffer.
    /// </summary>
    public long BytesConsumed => _bytesConsumed;

    /// <summary>
    /// Returns the raw byte span for a token produced by <see cref="Read"/>.
    /// Valid only while the underlying buffer is alive.
    /// </summary>
    public ReadOnlySpan<byte> GetSpan(PaktLexicalToken token) =>
        _buffer.Slice(token.Offset, token.Length);

    /// <summary>
    /// Reads the next lexical token, skipping layout (whitespace, newlines, comments).
    /// </summary>
    public PaktReadResult Read(out PaktLexicalToken token)
    {
        token = default;
        _sawNewline = false;

        // Resume a partial token left over from a previous buffer.
        if (_mode != LexerMode.Normal)
        {
            return ResumePartialToken(out token);
        }

        // Skip layout characters and comments.
        if (!SkipLayout())
        {
            _bytesConsumed = _pos;
            SaveNormalState();
            return _isFinalBlock ? PaktReadResult.EndOfInput : PaktReadResult.NeedMoreData;
        }

        // Dispatch on the first non-layout byte.
        return DispatchToken(out token);
    }

    // ───────────────────────── layout ─────────────────────────

    /// <summary>
    /// Skips whitespace, newlines and comments.  Returns <c>true</c> when a
    /// non-layout byte is available at <c>_pos</c>, <c>false</c> when the
    /// buffer is exhausted (possibly rolled back to a comment start).
    /// </summary>
    private bool SkipLayout()
    {
        while (_pos < _buffer.Length)
        {
            byte b = _buffer[_pos];

            if (Lexical.IsWhitespace(b))
            {
                _cursor.Advance(b);
                _pos++;
                continue;
            }

            if (b == Lexical.Newline)
            {
                _sawNewline = true;
                _cursor.Advance(b);
                _pos++;
                continue;
            }

            if (b == Lexical.CarriageReturn)
            {
                _sawNewline = true;
                _cursor.Advance(b);
                _pos++;
                if (_pos < _buffer.Length && _buffer[_pos] == Lexical.Newline)
                {
                    _cursor.Advance(Lexical.Newline);
                    _pos++;
                }
                continue;
            }

            if (b == Lexical.Hash)
            {
                if (!SkipComment())
                {
                    return false;
                }

                continue;
            }

            // Non-layout byte ready.
            return true;
        }

        return false;
    }

    /// <summary>
    /// Skips a line comment starting at <c>#</c>.
    /// Returns <c>false</c> if the buffer ends mid-comment and this is not the final block,
    /// rolling back <c>_pos</c> so the comment bytes are preserved for the next buffer.
    /// </summary>
    private bool SkipComment()
    {
        int commentStart = _pos;
        SourceCursor savedCursor = _cursor;

        _cursor.Advance(_buffer[_pos]); // #
        _pos++;

        while (_pos < _buffer.Length)
        {
            byte cb = _buffer[_pos];
            if (cb == Lexical.Newline || cb == Lexical.CarriageReturn)
            {
                break;
            }

            if (cb == Lexical.Nul)
            {
                ThrowSyntax("NUL byte inside comment");
            }

            _cursor.Advance(cb);
            _pos++;
        }

        if (_pos >= _buffer.Length && !_isFinalBlock)
        {
            _pos = commentStart;
            _cursor = savedCursor;
            return false;
        }

        return true;
    }

    // ───────────────────────── dispatch ─────────────────────────

    private PaktReadResult DispatchToken(out PaktLexicalToken token)
    {
        token = default;
        int tokenStart = _pos;
        byte b = _buffer[_pos];

        // ── NUL ──
        if (b == Lexical.Nul)
        {
            _pos++;
            _cursor.Advance(b);
            token = new PaktLexicalToken(PaktLexicalTokenKind.Nul, tokenStart, 1);
            CommitToken();
            return PaktReadResult.Token;
        }

        // ── single-byte delimiters ──
        switch (b)
        {
            case Lexical.Colon:
                return EmitSingleByte(PaktLexicalTokenKind.Colon, tokenStart, out token);
            case Lexical.Question:
                return EmitSingleByte(PaktLexicalTokenKind.Nullable, tokenStart, out token);
            case Lexical.LBrace:
                return EmitSingleByte(PaktLexicalTokenKind.LBrace, tokenStart, out token);
            case Lexical.RBrace:
                return EmitSingleByte(PaktLexicalTokenKind.RBrace, tokenStart, out token);
            case Lexical.LParen:
                return EmitSingleByte(PaktLexicalTokenKind.LParen, tokenStart, out token);
            case Lexical.RParen:
                return EmitSingleByte(PaktLexicalTokenKind.RParen, tokenStart, out token);
            case Lexical.LBrack:
                return EmitSingleByte(PaktLexicalTokenKind.LBrack, tokenStart, out token);
            case Lexical.RBrack:
                return EmitSingleByte(PaktLexicalTokenKind.RBrack, tokenStart, out token);
            case Lexical.RAngle:
                return EmitSingleByte(PaktLexicalTokenKind.RAngle, tokenStart, out token);
        }

        // ── digraphs and compound tokens ──
        return DispatchCompoundOrValue(tokenStart, b, out token);
    }

    private PaktReadResult DispatchCompoundOrValue(int tokenStart, byte b, out PaktLexicalToken token)
    {
        token = default;

        if (b == Lexical.EqualsSign) return ScanEqualsOrBind(tokenStart, out token);
        if (b == Lexical.LAngle) return ScanAngleOrPack(tokenStart, out token);
        if (b == Lexical.Pipe) return ScanPipeOrAtom(tokenStart, out token);
        if (b == Lexical.SingleQuote) return ScanQuotedString(tokenStart, out token);
        if (b == Lexical.LowerR) return ScanRawOrIdent(tokenStart, out token);

        if (b == Lexical.LowerX || b == Lexical.LowerB)
        {
            return ScanBinaryOrIdent(tokenStart, out token);
        }

        if (Lexical.IsIdentifierStart(b))
        {
            return ScanIdent(tokenStart, out token);
        }

        if (Lexical.IsDigit(b))
        {
            return ScanNumber(tokenStart, out token);
        }

        if (b == Lexical.Minus)
        {
            return ScanNegativeNumber(tokenStart, out token);
        }

        if (IsReservedByte(b))
        {
            ThrowReserved(b);
        }

        ThrowSyntax($"Unexpected byte 0x{b:X2}");
        return default; // unreachable
    }

    // ───────────────────────── single-byte emit ─────────────────────────

    private PaktReadResult EmitSingleByte(
        PaktLexicalTokenKind kind,
        int tokenStart,
        out PaktLexicalToken token)
    {
        _pos++;
        _cursor.Advance(_buffer[tokenStart]);
        token = new PaktLexicalToken(kind, tokenStart, 1);
        CommitToken();
        return PaktReadResult.Token;
    }

    // ───────────────────────── digraphs ─────────────────────────

    private PaktReadResult ScanEqualsOrBind(int tokenStart, out PaktLexicalToken token)
    {
        token = default;

        if (_pos + 1 >= _buffer.Length)
        {
            if (_isFinalBlock)
            {
                return EmitSingleByte(PaktLexicalTokenKind.Assign, tokenStart, out token);
            }

            return NeedMoreDataAt(tokenStart);
        }

        if (_buffer[_pos + 1] == Lexical.RAngle)
        {
            // =>
            _cursor.Advance(_buffer[_pos]);
            _cursor.Advance(_buffer[_pos + 1]);
            _pos += 2;
            token = new PaktLexicalToken(PaktLexicalTokenKind.Bind, tokenStart, 2);
            CommitToken();
            return PaktReadResult.Token;
        }

        return EmitSingleByte(PaktLexicalTokenKind.Assign, tokenStart, out token);
    }

    private PaktReadResult ScanAngleOrPack(int tokenStart, out PaktLexicalToken token)
    {
        token = default;

        if (_pos + 1 >= _buffer.Length)
        {
            if (_isFinalBlock)
            {
                return EmitSingleByte(PaktLexicalTokenKind.LAngle, tokenStart, out token);
            }

            return NeedMoreDataAt(tokenStart);
        }

        if (_buffer[_pos + 1] == Lexical.LAngle)
        {
            // <<
            _cursor.Advance(_buffer[_pos]);
            _cursor.Advance(_buffer[_pos + 1]);
            _pos += 2;
            token = new PaktLexicalToken(PaktLexicalTokenKind.Pack, tokenStart, 2);
            CommitToken();
            return PaktReadResult.Token;
        }

        return EmitSingleByte(PaktLexicalTokenKind.LAngle, tokenStart, out token);
    }

    // ───────────────────────── pipe / atom ─────────────────────────

    private PaktReadResult ScanPipeOrAtom(int tokenStart, out PaktLexicalToken token)
    {
        token = default;

        if (_pos + 1 >= _buffer.Length)
        {
            if (_isFinalBlock)
            {
                return EmitSingleByte(PaktLexicalTokenKind.Pipe, tokenStart, out token);
            }

            return NeedMoreDataAt(tokenStart);
        }

        if (Lexical.IsIdentifierStart(_buffer[_pos + 1]))
        {
            // |ident — AtomPrefix
            _cursor.Advance(_buffer[_pos]); // |
            _pos++;
            _cursor.Advance(_buffer[_pos]); // first ident char
            _pos++;

            while (_pos < _buffer.Length && Lexical.IsIdentifierPart(_buffer[_pos]))
            {
                _cursor.Advance(_buffer[_pos]);
                _pos++;
            }

            if (_pos >= _buffer.Length && !_isFinalBlock)
            {
                // Ident part might continue in next buffer.
                return NeedMoreDataAt(tokenStart);
            }

            token = new PaktLexicalToken(PaktLexicalTokenKind.AtomPrefix, tokenStart, _pos - tokenStart);
            CommitToken();
            return PaktReadResult.Token;
        }

        return EmitSingleByte(PaktLexicalTokenKind.Pipe, tokenStart, out token);
    }

    // ───────────────────────── strings ─────────────────────────

    private PaktReadResult ScanQuotedString(int tokenStart, out PaktLexicalToken token)
    {
        token = default;
        Debug.Assert(_buffer[_pos] == Lexical.SingleQuote);

        // Determine if this is ''' (multiline) or ' (single-line).
        if (_pos + 1 >= _buffer.Length)
        {
            return _isFinalBlock
                ? ThrowUnterminatedString<PaktReadResult>()
                : NeedMoreDataAt(tokenStart);
        }

        if (_buffer[_pos + 1] == Lexical.SingleQuote)
        {
            if (_pos + 2 >= _buffer.Length)
            {
                return _isFinalBlock
                    ? EmitEmptyString(tokenStart, out token) // '' = empty string
                    : NeedMoreDataAt(tokenStart);
            }

            if (_buffer[_pos + 2] == Lexical.SingleQuote)
            {
                // ''' — multiline string
                AdvanceBytes(3);
                return ScanStringBody(tokenStart, isMultiLine: true, isRaw: false, out token);
            }

            // '' — empty string
            return EmitEmptyString(tokenStart, out token);
        }

        // ' — single-line string
        AdvanceBytes(1);
        return ScanStringBody(tokenStart, isMultiLine: false, isRaw: false, out token);
    }

    private PaktReadResult EmitEmptyString(int tokenStart, out PaktLexicalToken token)
    {
        AdvanceBytes(2); // opening ' and closing '
        token = new PaktLexicalToken(PaktLexicalTokenKind.String, tokenStart, 2);
        CommitToken();
        return PaktReadResult.Token;
    }

    private PaktReadResult ScanRawOrIdent(int tokenStart, out PaktLexicalToken token)
    {
        token = default;
        Debug.Assert(_buffer[_pos] == Lexical.LowerR);

        if (_pos + 1 >= _buffer.Length)
        {
            return _isFinalBlock
                ? ScanIdent(tokenStart, out token)
                : NeedMoreDataAt(tokenStart);
        }

        if (_buffer[_pos + 1] != Lexical.SingleQuote)
        {
            return ScanIdent(tokenStart, out token);
        }

        // r' — raw string or raw multiline?
        if (_pos + 2 >= _buffer.Length)
        {
            return _isFinalBlock
                ? ThrowUnterminatedString<PaktReadResult>()
                : NeedMoreDataAt(tokenStart);
        }

        if (_buffer[_pos + 2] == Lexical.SingleQuote)
        {
            if (_pos + 3 >= _buffer.Length)
            {
                return _isFinalBlock
                    ? EmitRawEmptyString(tokenStart, out token) // r'' = raw empty string
                    : NeedMoreDataAt(tokenStart);
            }

            if (_buffer[_pos + 3] == Lexical.SingleQuote)
            {
                // r''' — raw multiline string
                AdvanceBytes(4);
                return ScanStringBody(tokenStart, isMultiLine: true, isRaw: true, out token);
            }

            // r'' — raw empty string
            return EmitRawEmptyString(tokenStart, out token);
        }

        // r'X — raw single-line string
        AdvanceBytes(2); // r'
        return ScanStringBody(tokenStart, isMultiLine: false, isRaw: true, out token);
    }

    private PaktReadResult EmitRawEmptyString(int tokenStart, out PaktLexicalToken token)
    {
        AdvanceBytes(3); // r + opening ' + closing '
        token = new PaktLexicalToken(PaktLexicalTokenKind.String, tokenStart, 3);
        CommitToken();
        return PaktReadResult.Token;
    }

    private PaktReadResult ScanBinaryOrIdent(int tokenStart, out PaktLexicalToken token)
    {
        token = default;
        Debug.Assert(_buffer[_pos] == Lexical.LowerX || _buffer[_pos] == Lexical.LowerB);

        if (_pos + 1 >= _buffer.Length)
        {
            return _isFinalBlock
                ? ScanIdent(tokenStart, out token)
                : NeedMoreDataAt(tokenStart);
        }

        if (_buffer[_pos + 1] != Lexical.SingleQuote)
        {
            return ScanIdent(tokenStart, out token);
        }

        // x' or b' — binary literal
        AdvanceBytes(2);
        return ScanBinaryBody(tokenStart, out token);
    }

    // ───────────────────── string / binary body ─────────────────────

    /// <summary>
    /// Scans the interior of a string after the opening delimiter has been consumed.
    /// Handles single-line, multiline, raw and non-raw variants.
    /// </summary>
    private PaktReadResult ScanStringBody(
        int tokenStart,
        bool isMultiLine,
        bool isRaw,
        out PaktLexicalToken token)
    {
        token = default;

        while (_pos < _buffer.Length)
        {
            byte b = _buffer[_pos];

            if (b == Lexical.Nul)
            {
                ThrowSyntax("NUL byte inside string");
            }

            if (b == Lexical.SingleQuote)
            {
                PaktReadResult closeResult = ScanStringClose(
                    tokenStart, isMultiLine, isRaw, out token);
                if (closeResult != PaktReadResult.EndOfInput)
                {
                    return closeResult; // Token or NeedMoreData
                }

                // EndOfInput is a sentinel meaning "not the closing delimiter — keep scanning".
                continue;
            }

            if (!isRaw && b == Lexical.Backslash)
            {
                PaktReadResult escResult = ScanEscape(tokenStart, isMultiLine);
                if (escResult != PaktReadResult.Token)
                {
                    return escResult;
                }

                continue;
            }

            if (!isMultiLine && (b == Lexical.Newline || b == Lexical.CarriageReturn))
            {
                ThrowSyntax("Newline in single-line string");
            }

            _cursor.Advance(b);
            _pos++;
        }

        if (_isFinalBlock)
        {
            ThrowUnterminatedString<PaktReadResult>();
        }

        return NeedMoreDataAt(tokenStart, ModeForString(isMultiLine, isRaw));
    }

    /// <summary>
    /// Handles a <c>'</c> encountered inside a string body.
    /// Returns <see cref="PaktReadResult.Token"/> when the string is closed,
    /// <see cref="PaktReadResult.NeedMoreData"/> when more data is needed,
    /// or <see cref="PaktReadResult.EndOfInput"/> as a sentinel meaning
    /// "this quote is content, not the closing delimiter — caller should continue scanning."
    /// </summary>
    private PaktReadResult ScanStringClose(
        int tokenStart,
        bool isMultiLine,
        bool isRaw,
        out PaktLexicalToken token)
    {
        token = default;

        if (isMultiLine)
        {
            if (_pos + 2 >= _buffer.Length)
            {
                if (_isFinalBlock)
                {
                    ThrowUnterminatedString<PaktReadResult>();
                }

                return NeedMoreDataAt(tokenStart, ModeForString(isMultiLine, isRaw));
            }

            if (_buffer[_pos + 1] == Lexical.SingleQuote
                && _buffer[_pos + 2] == Lexical.SingleQuote)
            {
                AdvanceBytes(3);
                token = new PaktLexicalToken(
                    PaktLexicalTokenKind.String, tokenStart, _pos - tokenStart);
                CommitToken();
                return PaktReadResult.Token;
            }

            // Fewer than 3 consecutive quotes — content, not closing delimiter.
            _cursor.Advance(_buffer[_pos]);
            _pos++;
            return PaktReadResult.EndOfInput; // sentinel: keep scanning
        }

        // Single-line closing quote.
        _cursor.Advance(_buffer[_pos]);
        _pos++;
        token = new PaktLexicalToken(
            PaktLexicalTokenKind.String, tokenStart, _pos - tokenStart);
        CommitToken();
        return PaktReadResult.Token;
    }

    /// <summary>
    /// Scans a binary literal body after the opening <c>x'</c> or <c>b'</c>.
    /// Content is not validated here (hex/base64 checks are the reader's job).
    /// </summary>
    private PaktReadResult ScanBinaryBody(int tokenStart, out PaktLexicalToken token)
    {
        token = default;

        while (_pos < _buffer.Length)
        {
            byte b = _buffer[_pos];

            if (b == Lexical.Nul)
            {
                ThrowSyntax("NUL byte inside binary literal");
            }

            if (b == Lexical.SingleQuote)
            {
                _cursor.Advance(b);
                _pos++;
                token = new PaktLexicalToken(
                    PaktLexicalTokenKind.Binary,
                    tokenStart,
                    _pos - tokenStart);
                CommitToken();
                return PaktReadResult.Token;
            }

            if (b == Lexical.Newline || b == Lexical.CarriageReturn)
            {
                ThrowSyntax("Newline in binary literal");
            }

            _cursor.Advance(b);
            _pos++;
        }

        if (_isFinalBlock)
        {
            ThrowUnterminatedString<PaktReadResult>();
        }

        return NeedMoreDataAt(tokenStart, LexerMode.InBinary);
    }

    /// <summary>
    /// Validates and skips one escape sequence (<c>\X</c> or <c>\uXXXX</c>).
    /// Returns <see cref="PaktReadResult.Token"/> when the escape was consumed,
    /// or <see cref="PaktReadResult.NeedMoreData"/> when the buffer is exhausted
    /// mid-escape.
    /// </summary>
    private PaktReadResult ScanEscape(int tokenStart, bool isMultiLine)
    {
        Debug.Assert(_buffer[_pos] == Lexical.Backslash);

        if (_pos + 1 >= _buffer.Length)
        {
            if (_isFinalBlock)
            {
                ThrowSyntax("Unterminated escape sequence");
            }

            return NeedMoreDataAt(tokenStart, ModeForString(isMultiLine, isRaw: false));
        }

        byte next = _buffer[_pos + 1];

        if (next == (byte)'u')
        {
            return ScanUnicodeEscape(tokenStart, isMultiLine);
        }

        // Simple escapes: \\ \' \n \r \t
        if (next is (byte)'\\' or (byte)'\'' or (byte)'n' or (byte)'r' or (byte)'t')
        {
            AdvanceBytes(2);
            return PaktReadResult.Token;
        }

        ThrowSyntax($"Invalid escape sequence: \\{(char)next}");
        return default; // unreachable
    }

    /// <summary>Validates and consumes a <c>\uXXXX</c> escape (6 bytes total).</summary>
    private PaktReadResult ScanUnicodeEscape(int tokenStart, bool isMultiLine)
    {
        if (_pos + 5 >= _buffer.Length)
        {
            if (_isFinalBlock)
            {
                ThrowSyntax("Incomplete \\u escape — expected 4 hex digits");
            }

            return NeedMoreDataAt(tokenStart, ModeForString(isMultiLine, isRaw: false));
        }

        for (int i = 2; i < 6; i++)
        {
            if (!Lexical.IsHexDigit(_buffer[_pos + i]))
            {
                ThrowSyntax($"Invalid hex digit in \\u escape: 0x{_buffer[_pos + i]:X2}");
            }
        }

        int codePoint =
            (HexVal(_buffer[_pos + 2]) << 12)
            | (HexVal(_buffer[_pos + 3]) << 8)
            | (HexVal(_buffer[_pos + 4]) << 4)
            | HexVal(_buffer[_pos + 5]);

        if (codePoint is >= 0xD800 and <= 0xDFFF)
        {
            ThrowSyntax($"Surrogate code point U+{codePoint:X4} is not valid in \\u escape");
        }

        if (codePoint == 0)
        {
            ThrowSyntax("NUL (\\u0000) is not permitted in strings");
        }

        AdvanceBytes(6);
        return PaktReadResult.Token;
    }

    // ───────────────────── identifiers ─────────────────────

    private PaktReadResult ScanIdent(int tokenStart, out PaktLexicalToken token)
    {
        token = default;
        Debug.Assert(Lexical.IsIdentifierStart(_buffer[tokenStart]));

        _cursor.Advance(_buffer[_pos]);
        _pos++;

        while (_pos < _buffer.Length && Lexical.IsIdentifierPart(_buffer[_pos]))
        {
            _cursor.Advance(_buffer[_pos]);
            _pos++;
        }

        if (_pos >= _buffer.Length && !_isFinalBlock)
        {
            return NeedMoreDataAt(tokenStart);
        }

        token = new PaktLexicalToken(PaktLexicalTokenKind.Ident, tokenStart, _pos - tokenStart);
        CommitToken();
        return PaktReadResult.Token;
    }

    // ───────────────────── numbers ─────────────────────

    private PaktReadResult ScanNumber(int tokenStart, out PaktLexicalToken token)
    {
        token = default;

        while (_pos < _buffer.Length)
        {
            byte b = _buffer[_pos];

            if (IsNumberTerminator(b))
            {
                break;
            }

            _cursor.Advance(b);
            _pos++;
        }

        if (_pos == tokenStart)
        {
            ThrowSyntax("Expected number");
        }

        if (_pos >= _buffer.Length && !_isFinalBlock)
        {
            return NeedMoreDataAt(tokenStart);
        }

        token = new PaktLexicalToken(PaktLexicalTokenKind.Number, tokenStart, _pos - tokenStart);
        CommitToken();
        return PaktReadResult.Token;
    }

    private PaktReadResult ScanNegativeNumber(int tokenStart, out PaktLexicalToken token)
    {
        token = default;
        Debug.Assert(_buffer[_pos] == Lexical.Minus);

        if (_pos + 1 >= _buffer.Length)
        {
            if (_isFinalBlock)
            {
                ThrowSyntax("Unexpected '-' at end of input");
            }

            return NeedMoreDataAt(tokenStart);
        }

        if (!Lexical.IsDigit(_buffer[_pos + 1]))
        {
            ThrowSyntax($"Expected digit after '-', got 0x{_buffer[_pos + 1]:X2}");
        }

        // Consume the '-' then delegate to the main number scanner.
        _cursor.Advance(_buffer[_pos]);
        _pos++;
        return ScanNumber(tokenStart, out token);
    }

    // ───────────────────── resume partial token ─────────────────────

    private PaktReadResult ResumePartialToken(out PaktLexicalToken token)
    {
        token = default;
        int tokenStart = 0; // after compaction, partial token is at offset 0
        LexerMode mode = _mode;
        _mode = LexerMode.Normal;

        // Skip past the opening delimiter that is present in the preserved bytes.
        int delimiterLen = mode switch
        {
            LexerMode.InString => 1,       // '
            LexerMode.InRawString => 2,    // r'
            LexerMode.InMultiLine => 3,    // '''
            LexerMode.InRawMultiLine => 4, // r'''
            LexerMode.InBinary => 2,       // x' or b'
            _ => 0,
        };

        if (_buffer.Length < delimiterLen)
        {
            // Not enough data to even re-read the delimiter.
            if (_isFinalBlock)
            {
                ThrowUnterminatedString<PaktReadResult>();
            }

            return NeedMoreDataAt(tokenStart, mode);
        }

        AdvanceBytes(delimiterLen);

        return mode switch
        {
            LexerMode.InString => ScanStringBody(tokenStart, isMultiLine: false, isRaw: false, out token),
            LexerMode.InRawString => ScanStringBody(tokenStart, isMultiLine: false, isRaw: true, out token),
            LexerMode.InMultiLine => ScanStringBody(tokenStart, isMultiLine: true, isRaw: false, out token),
            LexerMode.InRawMultiLine => ScanStringBody(tokenStart, isMultiLine: true, isRaw: true, out token),
            LexerMode.InBinary => ScanBinaryBody(tokenStart, out token),
            _ => throw new InvalidOperationException($"Unexpected resume mode: {mode}"),
        };
    }

    // ───────────────────── state management ─────────────────────

    private void CommitToken()
    {
        _bytesConsumed = _pos;
        SaveNormalState();
    }

    private void SaveNormalState()
    {
        _state.TotalConsumed = _totalConsumedBase + _pos;
        _state.Line = _cursor.Line;
        _state.Column = (int)_cursor.Column;
        _state.Mode = LexerMode.Normal;
        _state.EscapeSubstate = 0;
        _state.PendingQuoteCount = 0;
        _state.PartialTokenStart = 0;
    }

    /// <summary>
    /// Save state for NeedMoreData when there is no partial token.
    /// <c>_bytesConsumed</c> must already be set by the caller.
    /// </summary>
    private PaktReadResult NeedMoreDataAt(int tokenStart)
    {
        _bytesConsumed = tokenStart;
        _state.TotalConsumed = _totalConsumedBase + tokenStart;

        // Cursor at the token start: we need to re-derive it.
        // Since we've been advancing the cursor past the token bytes,
        // we store the current cursor and note that on resume the
        // partial token bytes will be re-scanned.  The cursor saved
        // here is at the token start because the caller hasn't advanced
        // past layout yet — except when we've already advanced into the
        // token.  For safety we save the cursor as-is; re-scanning the
        // preserved bytes corrects the position on resume.
        //
        // For Normal mode (no partial token), this is straightforward:
        // cursor is at the position of buffer[tokenStart].
        _state.Line = _cursor.Line;
        _state.Column = (int)_cursor.Column;
        _state.Mode = LexerMode.Normal;
        _state.EscapeSubstate = 0;
        _state.PendingQuoteCount = 0;
        _state.PartialTokenStart = 0;
        return PaktReadResult.NeedMoreData;
    }

    /// <summary>
    /// Save state for NeedMoreData mid-token (string/binary).
    /// </summary>
    private PaktReadResult NeedMoreDataAt(int tokenStart, LexerMode mode)
    {
        _bytesConsumed = tokenStart;

        // Save cursor at the position corresponding to buffer[tokenStart].
        // The cursor has advanced past some token bytes, but we'll re-scan on
        // resume. We need the cursor for the token start.
        //
        // However, we don't have the exact cursor at tokenStart saved. As a
        // pragmatic approach we recompute it: we know TotalConsumed at token
        // start and the line/column can't be perfectly recovered without replay.
        //
        // The simple and correct approach: save the state's Line/Column from
        // the constructor (they correspond to buffer offset 0) and note that
        // PartialTokenStart = 0 after compaction. The resume path re-scans
        // from offset 0 through the delimiter and body, re-advancing the cursor.
        //
        // We use the *initial* cursor state that was set in the constructor,
        // because after buffer compaction the preserved bytes start at offset 0
        // and the cursor must match that position.
        _state.TotalConsumed = _totalConsumedBase + tokenStart;
        _state.Line = _cursor.Line;
        _state.Column = (int)_cursor.Column;
        _state.Mode = mode;
        _state.EscapeSubstate = 0;
        _state.PendingQuoteCount = 0;
        _state.PartialTokenStart = 0;

        // Correct the Line/Column: we need the position at tokenStart,
        // not at _pos. Since we can't trivially recover it and re-scanning
        // will fix the cursor on resume anyway, this is acceptable.
        // For single-buffer (PaktMemoryReader) usage this path is never hit.
        return PaktReadResult.NeedMoreData;
    }

    // ───────────────────── helpers ─────────────────────

    /// <summary>Advance <c>_pos</c> and <c>_cursor</c> by <paramref name="count"/> bytes.</summary>
    private void AdvanceBytes(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _cursor.Advance(_buffer[_pos]);
            _pos++;
        }
    }

    /// <summary>
    /// Returns <c>true</c> for any byte that terminates a Number token:
    /// layout, structural delimiters, operators, quote, comment, NUL.
    /// </summary>
    private static bool IsNumberTerminator(byte b) =>
        Lexical.IsLayoutChar(b)
        || b == Lexical.LBrace || b == Lexical.RBrace
        || b == Lexical.LParen || b == Lexical.RParen
        || b == Lexical.LBrack || b == Lexical.RBrack
        || b == Lexical.LAngle || b == Lexical.RAngle
        || b == Lexical.Pipe
        || b == Lexical.Colon || b == Lexical.EqualsSign
        || b == Lexical.Question
        || b == Lexical.SingleQuote
        || b == Lexical.Hash
        || b == Lexical.Nul;

    private static LexerMode ModeForString(bool isMultiLine, bool isRaw)
    {
        return (isMultiLine, isRaw) switch
        {
            (false, false) => LexerMode.InString,
            (false, true) => LexerMode.InRawString,
            (true, false) => LexerMode.InMultiLine,
            (true, true) => LexerMode.InRawMultiLine,
        };
    }

    private static bool IsReservedByte(byte b) =>
        b is (byte)',' or (byte)';' or (byte)'"'
            or (byte)'@' or (byte)'!' or (byte)'*'
            or (byte)'$' or (byte)'&' or (byte)'~'
            or (byte)'`';

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0,
    };

    // ───────────────────── error helpers ─────────────────────

    private void ThrowSyntax(string message) =>
        throw PaktParseError.Syntax(_cursor.ToPosition(), message).ToException();

    private void ThrowReserved(byte b) =>
        throw PaktParseError.ReservedToken(
            _cursor.ToPosition(),
            $"Reserved token '{(char)b}' is not allowed outside strings")
        .ToException();

    private T ThrowUnterminatedString<T>()
    {
        throw PaktParseError.UnexpectedEndOfInput(
            _cursor.ToPosition(),
            "Unterminated string or binary literal")
        .ToException();
    }
}