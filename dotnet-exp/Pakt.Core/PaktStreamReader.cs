using System.Buffers;
using System.Globalization;
using System.Text;

namespace Pakt;

/// <summary>
/// Async-fill, sync-consume structural reader over <see cref="Stream"/>.
/// <para>
/// Consume pattern:
/// <code>
/// await using var reader = new PaktStreamReader(stream);
/// await reader.FillAsync(ct);
/// while (true)
/// {
///     while (reader.Read()) { /* process tokens */ }
///     if (!reader.NeedMoreData) break;
///     if (!await reader.FillAsync(ct)) break;
/// }
/// </code>
/// </para>
/// </summary>
public sealed class PaktStreamReader : IAsyncDisposable, IPaktReader
{
    private readonly StreamBuffer _buffer;
    private readonly PaktParserCore _core;
    private readonly ArrayPool<byte> _pool;

    private PaktLexerState _lexerState;
    private bool _streamEnded;
    private int _pendingAdvance;

    // Value span — offsets into whichever source is active.
    private int _valueSpanStart;
    private int _valueSpanLength;
    private ValueSpanSource _valueSpanSource;

    // Annotation byte accumulation across buffer boundaries.
    private byte[]? _annotationBuf;
    private int _annotationBufLen;

    private enum ValueSpanSource : byte
    {
        Buffer,
        AnnotationBuf,
    }

    public PaktStreamReader(Stream stream, PaktReaderOptions? options = null)
    {
        PaktReaderOptions opts = options ?? PaktReaderOptions.Default;
        _pool = opts.BufferPool ?? ArrayPool<byte>.Shared;
        _buffer = new StreamBuffer(stream, _pool, opts.InitialBufferSize, opts.MaxTokenBytes);
        _core = new PaktParserCore(opts);
    }

    // ───────────────────── public properties ─────────────────────

    public PaktTokenType TokenType { get; private set; }

    public ReadOnlySpan<byte> ValueSpan => _valueSpanSource switch
    {
        ValueSpanSource.AnnotationBuf => _annotationBuf.AsSpan(_valueSpanStart, _valueSpanLength),
        _ => _valueSpanLength > 0
            ? _buffer.Span.Slice(_valueSpanStart, _valueSpanLength)
            : ReadOnlySpan<byte>.Empty,
    };

    public int Depth => _core.Depth;

    public bool NeedMoreData { get; private set; }

    public long ByteOffset => _lexerState.TotalConsumed;
    public int Line => _lexerState.Line;
    public int Column => _lexerState.Column;

    // ───────────────────── async fill ─────────────────────

    /// <summary>
    /// Fill the internal buffer from the underlying stream.
    /// Returns <c>false</c> when the stream is exhausted.
    /// </summary>
    public async ValueTask<bool> FillAsync(CancellationToken ct = default)
    {
        NeedMoreData = false;
        bool gotData = await _buffer.FillAsync(ct).ConfigureAwait(false);
        if (!gotData)
        {
            _streamEnded = true;
        }

        return gotData;
    }

    // ───────────────────── sync read ─────────────────────

    /// <inheritdoc />
    public bool Read()
    {
        // Apply deferred buffer advancement from the previous Read().
        if (_pendingAdvance > 0)
        {
            _buffer.Advance(_pendingAdvance);
            _pendingAdvance = 0;
        }

        NeedMoreData = false;

        // Drain pending structural tokens (annotation end, operator, etc.).
        if (_core.TryEmitPending())
        {
            return EmitFromPending();
        }

        return RunLexerLoop();
    }

    // ───────────────────── main lexer loop ─────────────────────

    private bool RunLexerLoop()
    {
        ReadOnlySpan<byte> bufferSpan = _buffer.Span;

        if (bufferSpan.IsEmpty && _streamEnded)
        {
            return !_core.HandleEndOfInput();
        }

        PaktLexer lexer = new(bufferSpan, _streamEnded, ref _lexerState);

        while (true)
        {
            PaktReadResult lexResult = lexer.Read(out PaktLexicalToken lexToken);

            if (lexResult == PaktReadResult.NeedMoreData)
            {
                return HandleNeedMoreData(bufferSpan, ref lexer);
            }

            if (lexResult == PaktReadResult.EndOfInput)
            {
                return HandleEndOfInput(ref lexer);
            }

            bool done = DispatchProcessResult(
                ref lexer, lexToken, bufferSpan, out bool result);
            if (done)
            {
                return result;
            }
        }
    }

    private bool HandleNeedMoreData(ReadOnlySpan<byte> bufferSpan, ref PaktLexer lexer)
    {
        CopyAnnotationBytesIfNeeded(bufferSpan, (int)lexer.BytesConsumed);
        _buffer.Advance((int)lexer.BytesConsumed);
        NeedMoreData = true;
        return false;
    }

    private bool HandleEndOfInput(ref PaktLexer lexer)
    {
        _buffer.Advance((int)lexer.BytesConsumed);
        _ = _core.HandleEndOfInput();
        return false;
    }

    private bool DispatchProcessResult(
        ref PaktLexer lexer,
        PaktLexicalToken lexToken,
        ReadOnlySpan<byte> bufferSpan,
        out bool result)
    {
        PaktParserCore.ParserPhase phaseBefore = _core.Phase;
        PaktParserCore.ProcessResult pr = _core.ProcessToken(lexToken, bufferSpan);

        if (phaseBefore == PaktParserCore.ParserPhase.InAnnotation
            && _core.Phase == PaktParserCore.ParserPhase.InAnnotation)
        {
            _core.TrackAnnotationToken(lexToken);
        }

        switch (pr)
        {
            case PaktParserCore.ProcessResult.ConsumeMore:
                if (_core.TryEmitPending())
                {
                    FinalizeAnnotationBytes(bufferSpan);
                    result = EmitFromPendingWithAdvance((int)lexer.BytesConsumed);
                    return true;
                }

                result = false;
                return false; // continue loop

            case PaktParserCore.ProcessResult.Emit:
                result = EmitFromLexToken(lexToken, (int)lexer.BytesConsumed);
                return true;

            default:
                throw new InvalidOperationException();
        }
    }

    // ───────────────────── annotation bytes ─────────────────────

    private void CopyAnnotationBytesIfNeeded(ReadOnlySpan<byte> bufferSpan, int bytesConsumed)
    {
        if (_core.Phase is not (PaktParserCore.ParserPhase.InAnnotation
            or PaktParserCore.ParserPhase.PendingAnnotationEnd))
        {
            return;
        }

        int start = _core.AnnotationStart;
        int end = _core.AnnotationEnd;

        if (start < 0 || end <= start || end > bytesConsumed)
        {
            return;
        }

        int len = end - start;
        EnsureAnnotationBuf(_annotationBufLen + len);
        bufferSpan.Slice(start, len).CopyTo(_annotationBuf.AsSpan(_annotationBufLen));
        _annotationBufLen += len;
        _core.ResetAnnotationTracking();
    }

    private void FinalizeAnnotationBytes(ReadOnlySpan<byte> bufferSpan)
    {
        int start = _core.AnnotationStart;
        int end = _core.AnnotationEnd;

        if (start >= 0 && end > start)
        {
            int len = end - start;
            EnsureAnnotationBuf(_annotationBufLen + len);
            bufferSpan.Slice(start, len).CopyTo(_annotationBuf.AsSpan(_annotationBufLen));
            _annotationBufLen += len;
        }
    }

    private void EnsureAnnotationBuf(int needed)
    {
        if (_annotationBuf != null && _annotationBuf.Length >= needed)
        {
            return;
        }

        byte[] newBuf = _pool.Rent(Math.Max(needed, 256));
        if (_annotationBuf != null)
        {
            _annotationBuf.AsSpan(0, _annotationBufLen).CopyTo(newBuf);
            _pool.Return(_annotationBuf);
        }

        _annotationBuf = newBuf;
    }

    // ───────────────────── emit helpers ─────────────────────

    private bool EmitFromLexToken(PaktLexicalToken token, int bytesConsumed)
    {
        TokenType = _core.TokenType;
        _valueSpanStart = token.Offset;
        _valueSpanLength = token.Length;
        _valueSpanSource = ValueSpanSource.Buffer;
        _pendingAdvance = bytesConsumed;
        return true;
    }

    private bool EmitFromPending()
    {
        TokenType = _core.TokenType;

        if (TokenType == PaktTokenType.TypeAnnotationEnd)
        {
            _valueSpanStart = 0;
            _valueSpanLength = _annotationBufLen;
            _valueSpanSource = ValueSpanSource.AnnotationBuf;
        }
        else if (TokenType == PaktTokenType.TypeAnnotationStart)
        {
            // Reset annotation accumulator for the upcoming annotation scan.
            _annotationBufLen = 0;
            _valueSpanStart = 0;
            _valueSpanLength = 0;
            _valueSpanSource = ValueSpanSource.Buffer;
        }
        else
        {
            _valueSpanStart = 0;
            _valueSpanLength = 0;
            _valueSpanSource = ValueSpanSource.Buffer;
        }

        return true;
    }

    private bool EmitFromPendingWithAdvance(int bytesConsumed)
    {
        bool result = EmitFromPending();
        _pendingAdvance = bytesConsumed;
        return result;
    }

    // ───────────────────── typed accessors ─────────────────────

    public string ReadString()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected string").ToException();
        }

        if (TokenType != PaktTokenType.String)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected String, got {TokenType}").ToException();
        }

        return DecodeString(ValueSpan);
    }

    public string? ReadStringOrNil()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected string or nil")
                .ToException();
        }

        if (TokenType == PaktTokenType.Nil)
        {
            return null;
        }

        if (TokenType != PaktTokenType.String)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected String or Nil, got {TokenType}").ToException();
        }

        return DecodeString(ValueSpan);
    }

    public int ReadInt32()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected int").ToException();
        }

        if (TokenType != PaktTokenType.Int)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected Int, got {TokenType}").ToException();
        }

        return ParseInt32(ValueSpan);
    }

    public long ReadInt64()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected int").ToException();
        }

        if (TokenType != PaktTokenType.Int)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected Int, got {TokenType}").ToException();
        }

        return ParseInt64(ValueSpan);
    }

    public double ReadDouble()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected float").ToException();
        }

        if (TokenType != PaktTokenType.Float)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected Float, got {TokenType}").ToException();
        }

        return ParseDouble(ValueSpan);
    }

    public decimal ReadDecimal()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected decimal").ToException();
        }

        if (TokenType != PaktTokenType.Decimal)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected Decimal, got {TokenType}").ToException();
        }

        return ParseDecimal(ValueSpan);
    }

    public bool ReadBool()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected bool").ToException();
        }

        if (TokenType != PaktTokenType.Bool)
        {
            throw PaktParseError.TypeMismatch(GetPosition(),
                $"Expected Bool, got {TokenType}").ToException();
        }

        return ValueSpan.SequenceEqual("true"u8);
    }

    public bool TryReadNil()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected value").ToException();
        }

        return TokenType == PaktTokenType.Nil;
    }

    public ReadOnlySpan<byte> ReadRawValue()
    {
        if (!Read())
        {
            throw PaktParseError.UnexpectedEndOfInput(GetPosition(), "Expected value").ToException();
        }

        return ValueSpan;
    }

    public void ExpectToken(PaktTokenType expected)
    {
        if (!Read() || TokenType != expected)
        {
            throw new PaktParseException(
                $"Expected {expected}, got {TokenType}",
                GetPosition(),
                expected,
                TokenType);
        }
    }

    public bool TryExpectToken(PaktTokenType expected)
    {
        return Read() && TokenType == expected;
    }

    /// <summary>
    /// Compares the accumulated annotation bytes against <paramref name="expectedSignature"/>.
    /// Must be called after a <see cref="PaktTokenType.TypeAnnotationStart"/> token has been read.
    /// </summary>
    public bool VerifyTypeAnnotation(ReadOnlySpan<byte> expectedSignature)
    {
        if (_annotationBuf == null || _annotationBufLen == 0)
        {
            return expectedSignature.IsEmpty;
        }

        return _annotationBuf.AsSpan(0, _annotationBufLen).SequenceEqual(expectedSignature);
    }

    // ───────────────────── value parsing helpers ─────────────────────

    private static string DecodeString(ReadOnlySpan<byte> raw)
    {
        // Strip delimiters: 'x', r'x', '''x''', r'''x'''
        int start = 0;
        int end = raw.Length;

        bool isRaw = end > 0 && raw[0] == (byte)'r';
        if (isRaw)
        {
            start++;
        }

        if (end - start >= 6
            && raw[start] == (byte)'\''
            && raw[start + 1] == (byte)'\''
            && raw[start + 2] == (byte)'\'')
        {
            start += 3;
            end -= 3;
        }
        else if (end - start >= 2 && raw[start] == (byte)'\'')
        {
            start += 1;
            end -= 1;
        }

        ReadOnlySpan<byte> content = raw[start..end];

        if (isRaw || !content.Contains((byte)'\\'))
        {
            return Encoding.UTF8.GetString(content);
        }

        // Unescape. Allocates via StringBuilder — acceptable because strings allocate anyway.
        return UnescapeString(content);
    }

    private static string UnescapeString(ReadOnlySpan<byte> content)
    {
        StringBuilder sb = new(content.Length);
        int i = 0;
        while (i < content.Length)
        {
            byte b = content[i];
            if (b == (byte)'\\' && i + 1 < content.Length)
            {
                byte next = content[i + 1];
                switch (next)
                {
                    case (byte)'\\':
                        sb.Append('\\');
                        i += 2;
                        continue;
                    case (byte)'\'':
                        sb.Append('\'');
                        i += 2;
                        continue;
                    case (byte)'n':
                        sb.Append('\n');
                        i += 2;
                        continue;
                    case (byte)'r':
                        sb.Append('\r');
                        i += 2;
                        continue;
                    case (byte)'t':
                        sb.Append('\t');
                        i += 2;
                        continue;
                    case (byte)'u' when i + 5 < content.Length:
                        int cp =
                            (HexVal(content[i + 2]) << 12)
                            | (HexVal(content[i + 3]) << 8)
                            | (HexVal(content[i + 4]) << 4)
                            | HexVal(content[i + 5]);
                        sb.Append(char.ConvertFromUtf32(cp));
                        i += 6;
                        continue;
                }
            }

            // Decode one UTF-8 character.
            int charLen = Utf8CharLength(b);
            if (charLen <= 0 || i + charLen > content.Length)
            {
                sb.Append((char)b);
                i++;
            }
            else
            {
                sb.Append(Encoding.UTF8.GetString(content.Slice(i, charLen)));
                i += charLen;
            }
        }

        return sb.ToString();
    }

    private static int Utf8CharLength(byte leadByte) => leadByte switch
    {
        < 0x80 => 1,
        < 0xC0 => -1, // continuation byte
        < 0xE0 => 2,
        < 0xF0 => 3,
        < 0xF8 => 4,
        _ => -1,
    };

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0,
    };

    private static int ParseInt32(ReadOnlySpan<byte> span)
    {
        Span<byte> clean = stackalloc byte[span.Length];
        int len = StripUnderscores(span, clean);

        if (TryParseRadixInt(clean[..len], out long longVal))
        {
            return checked((int)longVal);
        }

        if (int.TryParse(
            clean[..len],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value))
        {
            return value;
        }

        throw PaktParseError.TypeMismatch(default, "Invalid int32 value").ToException();
    }

    private static long ParseInt64(ReadOnlySpan<byte> span)
    {
        Span<byte> clean = stackalloc byte[span.Length];
        int len = StripUnderscores(span, clean);

        if (TryParseRadixInt(clean[..len], out long value))
        {
            return value;
        }

        if (long.TryParse(
            clean[..len],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value))
        {
            return value;
        }

        throw PaktParseError.TypeMismatch(default, "Invalid int64 value").ToException();
    }

    private static double ParseDouble(ReadOnlySpan<byte> span)
    {
        Span<byte> clean = stackalloc byte[span.Length];
        int len = StripUnderscores(span, clean);

        if (double.TryParse(
            clean[..len],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value))
        {
            return value;
        }

        throw PaktParseError.TypeMismatch(default, "Invalid float value").ToException();
    }

    private static decimal ParseDecimal(ReadOnlySpan<byte> span)
    {
        Span<byte> clean = stackalloc byte[span.Length];
        int len = StripUnderscores(span, clean);

        if (decimal.TryParse(
            clean[..len],
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal value))
        {
            return value;
        }

        throw PaktParseError.TypeMismatch(default, "Invalid decimal value").ToException();
    }

    private static int StripUnderscores(ReadOnlySpan<byte> source, Span<byte> dest)
    {
        int j = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != (byte)'_')
            {
                dest[j++] = source[i];
            }
        }

        return j;
    }

    private static bool TryParseRadixInt(ReadOnlySpan<byte> clean, out long value)
    {
        value = 0;
        bool negative = false;
        int i = 0;

        if (i < clean.Length && clean[i] == (byte)'-')
        {
            negative = true;
            i++;
        }

        if (i + 2 >= clean.Length || clean[i] != (byte)'0')
        {
            return false;
        }

        byte prefix = clean[i + 1];
        ReadOnlySpan<byte> digits = clean[(i + 2)..];

        if (prefix == (byte)'x')
        {
            foreach (byte d in digits)
            {
                value = (value << 4) | (uint)HexVal(d);
            }
        }
        else if (prefix == (byte)'o')
        {
            foreach (byte d in digits)
            {
                value = (value << 3) | (uint)(d - '0');
            }
        }
        else if (prefix == (byte)'b')
        {
            foreach (byte d in digits)
            {
                value = (value << 1) | (uint)(d - '0');
            }
        }
        else
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }

    // ───────────────────── position helper ─────────────────────

    private SourcePosition GetPosition() =>
        new(_lexerState.TotalConsumed, _lexerState.Line, _lexerState.Column);

    // ───────────────────── dispose ─────────────────────

    public ValueTask DisposeAsync()
    {
        _buffer.Dispose();

        if (_annotationBuf != null)
        {
            _pool.Return(_annotationBuf);
            _annotationBuf = null;
        }

        return default;
    }
}