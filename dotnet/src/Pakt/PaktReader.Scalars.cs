using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pakt;

public ref partial struct PaktReader
{
    // -----------------------------------------------------------------------
    // Scalar reading (used by state machine to consume raw bytes)
    // -----------------------------------------------------------------------

    private (int start, int length) ReadScalarDirect(PaktScalarType kind)
    {
        return kind switch
        {
            PaktScalarType.Str => ReadStringValue(),
            PaktScalarType.Int => ReadIntValue(),
            PaktScalarType.Dec => ReadDecValue(),
            PaktScalarType.Float => ReadFloatValue(),
            PaktScalarType.Bool => ReadBoolValue(),
            PaktScalarType.Uuid => ReadUuidValue(),
            PaktScalarType.Date => ReadDateValue(),
            PaktScalarType.Time => ReadTimeValue(),
            PaktScalarType.DateTime => ReadDateTimeValue(),
            PaktScalarType.Bin => ReadBinValue(),
            _ => throw new PaktException($"Unknown scalar type kind {kind}", Position, PaktErrorCode.Syntax),
        };
    }

    // -----------------------------------------------------------------------
    // Public value accessors
    // -----------------------------------------------------------------------

    /// <summary>Gets the current scalar value as a string.</summary>
    public readonly string GetString()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return Encoding.UTF8.GetString(ValueSpan);
    }

    /// <summary>Gets the current scalar value as a 64-bit integer.</summary>
    public readonly long GetInt64()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return ParseInt64(ValueSpan);
    }

    /// <summary>Gets the current scalar value as a decimal.</summary>
    public readonly decimal GetDecimal()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return decimal.Parse(StripUnderscores(ValueSpan), NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the current scalar value as a double.</summary>
    public readonly double GetDouble()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return double.Parse(StripUnderscores(ValueSpan), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the current scalar value as a boolean.</summary>
    public readonly bool GetBoolean()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        var span = ValueSpan;
        if (span.Length == 4 && span[0] == 't') return true;
        if (span.Length == 5 && span[0] == 'f') return false;
        throw new PaktException($"Expected 'true' or 'false'", Position, PaktErrorCode.TypeMismatch);
    }

    /// <summary>Gets the current scalar value as a GUID.</summary>
    public readonly Guid GetGuid()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return Guid.Parse(Encoding.UTF8.GetString(ValueSpan));
    }

    /// <summary>Gets the current scalar value as a DateOnly.</summary>
    public readonly DateOnly GetDate()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return DateOnly.Parse(Encoding.UTF8.GetString(ValueSpan), CultureInfo.InvariantCulture);
    }

    /// <summary>Gets the current scalar value as a DateTimeOffset (for time values).</summary>
    public readonly DateTimeOffset GetTime()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        var text = Encoding.UTF8.GetString(ValueSpan);
        return DateTimeOffset.Parse("2000-01-01T" + text, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    /// <summary>Gets the current scalar value as a DateTimeOffset.</summary>
    public readonly DateTimeOffset GetDateTime()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return DateTimeOffset.Parse(Encoding.UTF8.GetString(ValueSpan), CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    /// <summary>
    /// Decodes the current bin value into the destination buffer. Returns the number of bytes written.
    /// </summary>
    public readonly int GetBytesFromBin(Span<byte> destination)
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        var hexStr = Encoding.UTF8.GetString(ValueSpan);
        var bytes = Convert.FromHexString(hexStr);
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    /// <summary>Gets the current atom value as a string.</summary>
    public readonly string GetAtom()
    {
        AssertTokenIs(PaktTokenType.ScalarValue);
        return Encoding.UTF8.GetString(ValueSpan);
    }

    // -----------------------------------------------------------------------
    // Internal scalar reading methods
    // -----------------------------------------------------------------------

    private (int start, int length) ReadStringValue()
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Expected string, got EOF", PaktErrorCode.UnexpectedEof);

        bool raw = false;
        byte start = _buffer[_consumed];

        if (start == 'r')
        {
            raw = true;
            _consumed++;
            _bytePositionInLine++;
            if (_consumed >= _buffer.Length)
                ThrowError("Expected quote after raw string prefix, got EOF", PaktErrorCode.UnexpectedEof);
            start = _buffer[_consumed];
        }

        byte quote = start;
        if (quote != '\'' && quote != '"')
            ThrowError($"Expected string, got '{(char)quote}'", PaktErrorCode.Syntax);

        _consumed++;
        _bytePositionInLine++;

        // Check for triple-quote
        if (_consumed + 1 < _buffer.Length && _buffer[_consumed] == quote && _buffer[_consumed + 1] == quote)
        {
            _consumed += 2;
            _bytePositionInLine += 2;
            return ReadMultiLineString(quote, raw);
        }

        return ReadSingleLineString(quote, raw);
    }

    private (int start, int length) ReadSingleLineString(byte quote, bool raw)
    {
        var sb = new StringBuilder(64);

        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (b == quote)
            {
                _consumed++;
                _bytePositionInLine++;
                return StoreDecodedString(sb.ToString());
            }
            if (!raw && b == '\\')
            {
                _consumed++;
                _bytePositionInLine++;
                ReadEscapeInto(sb);
                continue;
            }
            if (b == '\n' || b == '\r')
                ThrowError("Newline in single-line string", PaktErrorCode.Syntax);
            if (b == 0)
                ThrowError("Null byte in string", PaktErrorCode.Syntax);

            AppendUtf8Char(sb);
        }

        ThrowError("Unterminated string", PaktErrorCode.UnexpectedEof);
        return default;
    }

    private (int start, int length) ReadMultiLineString(byte quote, bool raw)
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Expected newline after opening triple-quote, got EOF", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed];
        if (b == '\r')
        {
            _consumed++;
            if (_consumed < _buffer.Length && _buffer[_consumed] == '\n')
                _consumed++;
            _line++;
            _bytePositionInLine = 0;
        }
        else if (b == '\n')
        {
            _consumed++;
            _line++;
            _bytePositionInLine = 0;
        }
        else
        {
            ThrowError($"Expected newline after opening triple-quote, got '{(char)b}'", PaktErrorCode.Syntax);
        }

        var sb = new StringBuilder(256);
        int baseline = 0;
        bool baselineSet = false;
        int lineCount = 0;

        while (true)
        {
            var line = ReadRawLineBytes(out bool hitEof);
            if (hitEof && line.Length == 0)
                ThrowError("Unterminated multi-line string", PaktErrorCode.UnexpectedEof);

            var trimmed = TrimLeadingWS(line);
            if (IsClosingTripleQuote(trimmed, quote))
                return StoreDecodedString(sb.ToString());

            if (IsBlankLine(line))
            {
                if (lineCount > 0) sb.Append('\n');
                lineCount++;
                continue;
            }

            int leading = CountLeadingWS(line);
            if (!baselineSet)
            {
                baseline = leading;
                baselineSet = true;
            }
            if (leading < baseline)
                ThrowError("Insufficient indentation in multi-line string", PaktErrorCode.Syntax);

            if (lineCount > 0) sb.Append('\n');
            lineCount++;

            var content = line.Slice(baseline);
            if (raw)
            {
                for (int i = 0; i < content.Length; i++)
                {
                    if (content[i] == 0)
                        ThrowError("Null byte in string", PaktErrorCode.Syntax);
                }
                sb.Append(Encoding.UTF8.GetString(content));
            }
            else
            {
                AppendWithEscapes(sb, content);
            }

            if (hitEof)
                ThrowError("Unterminated multi-line string", PaktErrorCode.UnexpectedEof);
        }
    }

    private ReadOnlySpan<byte> ReadRawLineBytes(out bool hitEof)
    {
        hitEof = false;
        int lineStart = _consumed;
        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (b == '\n')
            {
                int end = _consumed;
                _consumed++;
                _line++;
                _bytePositionInLine = 0;
                return _buffer.Slice(lineStart, end - lineStart);
            }
            if (b == '\r')
            {
                int end = _consumed;
                _consumed++;
                if (_consumed < _buffer.Length && _buffer[_consumed] == '\n')
                    _consumed++;
                _line++;
                _bytePositionInLine = 0;
                return _buffer.Slice(lineStart, end - lineStart);
            }
            _consumed++;
            _bytePositionInLine++;
        }
        hitEof = true;
        return _buffer.Slice(lineStart, _consumed - lineStart);
    }

    private static bool IsClosingTripleQuote(ReadOnlySpan<byte> trimmed, byte quote)
        => trimmed.Length == 3 && trimmed[0] == quote && trimmed[1] == quote && trimmed[2] == quote;

    private static ReadOnlySpan<byte> TrimLeadingWS(ReadOnlySpan<byte> span)
    {
        int i = 0;
        while (i < span.Length && (span[i] == ' ' || span[i] == '\t')) i++;
        return span.Slice(i);
    }

    private static bool IsBlankLine(ReadOnlySpan<byte> line)
    {
        for (int i = 0; i < line.Length; i++)
            if (line[i] != ' ' && line[i] != '\t') return false;
        return true;
    }

    private static int CountLeadingWS(ReadOnlySpan<byte> line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return i;
    }

    private void AppendWithEscapes(StringBuilder sb, ReadOnlySpan<byte> content)
    {
        int i = 0;
        while (i < content.Length)
        {
            byte b = content[i];
            if (b == 0) ThrowError("Null byte in string", PaktErrorCode.Syntax);
            if (b == '\\')
            {
                i++;
                if (i >= content.Length) ThrowError("Unterminated escape sequence in multi-line string", PaktErrorCode.Syntax);
                byte next = content[i];
                switch (next)
                {
                    case (byte)'\\': sb.Append('\\'); i++; break;
                    case (byte)'\'': sb.Append('\''); i++; break;
                    case (byte)'"': sb.Append('"'); i++; break;
                    case (byte)'n': sb.Append('\n'); i++; break;
                    case (byte)'r': sb.Append('\r'); i++; break;
                    case (byte)'t': sb.Append('\t'); i++; break;
                    case (byte)'u':
                        i++;
                        if (i + 4 > content.Length) ThrowError("Incomplete \\u escape", PaktErrorCode.Syntax);
                        AppendUnicodeEscape(sb, content.Slice(i, 4));
                        i += 4;
                        break;
                    case (byte)'U':
                        i++;
                        if (i + 8 > content.Length) ThrowError("Incomplete \\U escape", PaktErrorCode.Syntax);
                        AppendUnicodeEscape(sb, content.Slice(i, 8));
                        i += 8;
                        break;
                    default:
                        ThrowError($"Invalid escape sequence: \\{(char)next}", PaktErrorCode.Syntax);
                        break;
                }
                continue;
            }
            if (b < 0x80) { sb.Append((char)b); i++; }
            else
            {
                int seqLen = Utf8SequenceLength(b);
                if (i + seqLen > content.Length) ThrowError("Invalid UTF-8 sequence", PaktErrorCode.Syntax);
                sb.Append(Encoding.UTF8.GetString(content.Slice(i, seqLen)));
                i += seqLen;
            }
        }
    }

    private void ReadEscapeInto(StringBuilder sb)
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Unterminated escape sequence", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed++];
        _bytePositionInLine++;

        switch (b)
        {
            case (byte)'\\': sb.Append('\\'); break;
            case (byte)'\'': sb.Append('\''); break;
            case (byte)'"': sb.Append('"'); break;
            case (byte)'n': sb.Append('\n'); break;
            case (byte)'r': sb.Append('\r'); break;
            case (byte)'t': sb.Append('\t'); break;
            case (byte)'u':
            {
                if (_consumed + 4 > _buffer.Length)
                    ThrowError("Incomplete \\u escape", PaktErrorCode.UnexpectedEof);
                var hexSpan = _buffer.Slice(_consumed, 4);
                AppendUnicodeEscape(sb, hexSpan);
                _consumed += 4;
                _bytePositionInLine += 4;
                break;
            }
            case (byte)'U':
            {
                if (_consumed + 8 > _buffer.Length)
                    ThrowError("Incomplete \\U escape", PaktErrorCode.UnexpectedEof);
                var hexSpan = _buffer.Slice(_consumed, 8);
                AppendUnicodeEscape(sb, hexSpan);
                _consumed += 8;
                _bytePositionInLine += 8;
                break;
            }
            default:
                ThrowError($"Invalid escape sequence: \\{(char)b}", PaktErrorCode.Syntax);
                break;
        }
    }

    private void AppendUnicodeEscape(StringBuilder sb, ReadOnlySpan<byte> hexDigits)
    {
        int val = 0;
        for (int i = 0; i < hexDigits.Length; i++)
        {
            int d = HexVal(hexDigits[i]);
            if (d < 0) ThrowError("Invalid hex digit in unicode escape", PaktErrorCode.Syntax);
            val = val * 16 + d;
        }
        if (val == 0) ThrowError("Null byte (U+0000) not permitted in strings", PaktErrorCode.Syntax);
        if (val > 0x10FFFF) ThrowError($"Invalid unicode code point: U+{val:X8}", PaktErrorCode.Syntax);

        if (val <= 0xFFFF)
        {
            if (char.IsSurrogate((char)val))
                ThrowError($"Surrogate code point U+{val:X4} not permitted", PaktErrorCode.Syntax);
            sb.Append((char)val);
        }
        else
        {
            // Supplementary plane — encode as surrogate pair
            sb.Append(char.ConvertFromUtf32(val));
        }
    }

    private void AppendUtf8Char(StringBuilder sb)
    {
        byte b = _buffer[_consumed];
        if (b < 0x80)
        {
            sb.Append((char)b);
            _consumed++;
            _bytePositionInLine++;
        }
        else
        {
            int seqLen = Utf8SequenceLength(b);
            if (_consumed + seqLen > _buffer.Length) ThrowError("Invalid UTF-8 sequence", PaktErrorCode.Syntax);
            sb.Append(Encoding.UTF8.GetString(_buffer.Slice(_consumed, seqLen)));
            _consumed += seqLen;
            _bytePositionInLine += seqLen;
        }
    }

    private static int Utf8SequenceLength(byte leadByte)
    {
        if ((leadByte & 0x80) == 0) return 1;
        if ((leadByte & 0xE0) == 0xC0) return 2;
        if ((leadByte & 0xF0) == 0xE0) return 3;
        if ((leadByte & 0xF8) == 0xF0) return 4;
        return 1;
    }

    private (int start, int length) StoreDecodedString(string str)
    {
        int byteCount = Encoding.UTF8.GetByteCount(str);
        EnsureDecodedBuffer(byteCount);
        Encoding.UTF8.GetBytes(str, _decodedBuffer);
        _decodedLength = byteCount;
        _usingDecodedBuffer = true;
        return (-1, byteCount);
    }

    private void EnsureDecodedBuffer(int needed)
    {
        if (_decodedBuffer == null || _decodedBuffer.Length < needed)
        {
            if (_decodedBuffer != null)
                ArrayPool<byte>.Shared.Return(_decodedBuffer);
            _decodedBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(needed, 256));
        }
    }

    // -----------------------------------------------------------------------
    // Integer reading
    // -----------------------------------------------------------------------

    private (int start, int length) ReadIntValue()
    {
        int intStart = _consumed;

        if (_consumed < _buffer.Length && _buffer[_consumed] == '-')
        {
            _consumed++;
            _bytePositionInLine++;
        }

        if (_consumed >= _buffer.Length)
            ThrowError("Expected digit in integer, got EOF", PaktErrorCode.UnexpectedEof);

        byte first = _buffer[_consumed];
        if (!IsDigit(first))
            ThrowError($"Expected digit in integer, got '{(char)first}'", PaktErrorCode.Syntax);

        if (first == '0' && _consumed + 1 < _buffer.Length)
        {
            byte prefix = _buffer[_consumed + 1];
            if (prefix == 'x' || prefix == 'b' || prefix == 'o')
            {
                _consumed += 2;
                _bytePositionInLine += 2;
                Func<byte, bool> check = prefix switch
                {
                    (byte)'x' => IsHex,
                    (byte)'b' => IsBin,
                    (byte)'o' => IsOct,
                    _ => IsDigit,
                };
                ReadPrefixedDigits(check);
                return (intStart, _consumed - intStart);
            }
        }

        ReadDigitSep();
        return (intStart, _consumed - intStart);
    }

    private (int start, int length) ReadDecValue()
    {
        int decStart = _consumed;

        if (_consumed < _buffer.Length && _buffer[_consumed] == '-')
        {
            _consumed++;
            _bytePositionInLine++;
        }

        if (_consumed < _buffer.Length && _buffer[_consumed] != '.')
            ReadDigitSep();

        ExpectByte((byte)'.');
        ReadDigitSep();
        return (decStart, _consumed - decStart);
    }

    private (int start, int length) ReadFloatValue()
    {
        int floatStart = _consumed;

        if (_consumed < _buffer.Length && _buffer[_consumed] == '-')
        {
            _consumed++;
            _bytePositionInLine++;
        }

        if (_consumed < _buffer.Length && _buffer[_consumed] != '.' && _buffer[_consumed] != 'e' && _buffer[_consumed] != 'E')
            ReadDigitSep();

        if (_consumed < _buffer.Length && _buffer[_consumed] == '.')
        {
            _consumed++;
            _bytePositionInLine++;
            ReadDigitSep();
        }

        if (_consumed >= _buffer.Length)
            ThrowError("Expected exponent in float, got EOF", PaktErrorCode.UnexpectedEof);
        byte e = _buffer[_consumed];
        if (e != 'e' && e != 'E')
            ThrowError($"Expected exponent in float, got '{(char)e}'", PaktErrorCode.Syntax);
        _consumed++;
        _bytePositionInLine++;

        if (_consumed < _buffer.Length && (_buffer[_consumed] == '+' || _buffer[_consumed] == '-'))
        {
            _consumed++;
            _bytePositionInLine++;
        }

        int count = 0;
        while (_consumed < _buffer.Length && IsDigit(_buffer[_consumed]))
        {
            _consumed++;
            _bytePositionInLine++;
            count++;
        }
        if (count == 0) ThrowError("Expected digits in float exponent", PaktErrorCode.Syntax);
        return (floatStart, _consumed - floatStart);
    }

    private (int start, int length) ReadBoolValue()
    {
        int boolStart = _consumed;
        var ident = ReadIdent();
        if (ident != "true" && ident != "false")
            ThrowError($"Expected 'true' or 'false', got '{ident}'", PaktErrorCode.Syntax);
        return (boolStart, _consumed - boolStart);
    }

    private (int start, int length) ReadUuidValue()
    {
        int uuidStart = _consumed;
        ReadExactHex(8); ExpectByte((byte)'-');
        ReadExactHex(4); ExpectByte((byte)'-');
        ReadExactHex(4); ExpectByte((byte)'-');
        ReadExactHex(4); ExpectByte((byte)'-');
        ReadExactHex(12);
        return (uuidStart, _consumed - uuidStart);
    }

    private (int start, int length) ReadDateValue()
    {
        int dateStart = _consumed;
        ReadExactDigits(4); ExpectByte((byte)'-');
        ReadExactDigits(2); ExpectByte((byte)'-');
        ReadExactDigits(2);
        return (dateStart, _consumed - dateStart);
    }

    private (int start, int length) ReadTimeValue()
    {
        int timeStart = _consumed;
        ReadExactDigits(2); ExpectByte((byte)':');
        ReadExactDigits(2); ExpectByte((byte)':');
        ReadExactDigits(2);
        ReadOptionalFractionalSeconds();
        ReadTimezone();
        return (timeStart, _consumed - timeStart);
    }

    private (int start, int length) ReadDateTimeValue()
    {
        int dtStart = _consumed;
        ReadExactDigits(4); ExpectByte((byte)'-');
        ReadExactDigits(2); ExpectByte((byte)'-');
        ReadExactDigits(2);
        ExpectByte((byte)'T');
        ReadExactDigits(2); ExpectByte((byte)':');
        ReadExactDigits(2); ExpectByte((byte)':');
        ReadExactDigits(2);
        ReadOptionalFractionalSeconds();
        ReadTimezone();
        return (dtStart, _consumed - dtStart);
    }

    private void ReadOptionalFractionalSeconds()
    {
        if (_consumed < _buffer.Length && _buffer[_consumed] == '.')
        {
            _consumed++;
            _bytePositionInLine++;
            int count = 0;
            while (_consumed < _buffer.Length && IsDigit(_buffer[_consumed]))
            {
                _consumed++;
                _bytePositionInLine++;
                count++;
            }
            if (count == 0) ThrowError("Expected digits after '.' in time", PaktErrorCode.Syntax);
        }
    }

    private void ReadTimezone()
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Expected timezone, got EOF", PaktErrorCode.UnexpectedEof);
        byte tz = _buffer[_consumed];
        if (tz == 'Z')
        {
            _consumed++;
            _bytePositionInLine++;
        }
        else if (tz == '+' || tz == '-')
        {
            _consumed++;
            _bytePositionInLine++;
            ReadExactDigits(2); ExpectByte((byte)':'); ReadExactDigits(2);
        }
        else
        {
            ThrowError($"Expected timezone (Z or ±HH:MM), got '{(char)tz}'", PaktErrorCode.Syntax);
        }
    }

    private (int start, int length) ReadBinValue()
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Expected binary literal, got EOF", PaktErrorCode.UnexpectedEof);

        byte prefix = _buffer[_consumed];
        if (prefix != 'x' && prefix != 'b')
            ThrowError($"Expected binary literal, got '{(char)prefix}'", PaktErrorCode.Syntax);
        _consumed++;
        _bytePositionInLine++;
        ExpectByte((byte)'\'');

        int contentStart = _consumed;
        while (_consumed < _buffer.Length && _buffer[_consumed] != '\'')
        {
            byte ch = _buffer[_consumed];
            if (ch == '\n' || ch == '\r') ThrowError("Newline in binary literal", PaktErrorCode.Syntax);
            if (ch == 0) ThrowError("Null byte in binary literal", PaktErrorCode.Syntax);
            _consumed++;
            _bytePositionInLine++;
        }
        if (_consumed >= _buffer.Length) ThrowError("Unterminated binary literal", PaktErrorCode.UnexpectedEof);

        var contentSpan = _buffer.Slice(contentStart, _consumed - contentStart);
        _consumed++; // closing quote
        _bytePositionInLine++;

        var lit = Encoding.UTF8.GetString(contentSpan);
        byte[] data;
        if (prefix == 'x')
        {
            if (lit.Length % 2 != 0) ThrowError("Hex binary literal must contain an even number of digits", PaktErrorCode.Syntax);
            try { data = Convert.FromHexString(lit); }
            catch { ThrowError("Invalid hex binary literal", PaktErrorCode.Syntax); return default; }
        }
        else
        {
            try { data = Convert.FromBase64String(lit); }
            catch { ThrowError("Invalid base64 binary literal", PaktErrorCode.Syntax); return default; }
        }

        var hex = Convert.ToHexString(data).ToLowerInvariant();
        return StoreDecodedString(hex);
    }

    private (int start, int length) ReadAtomValue(ImmutableArray<string> allowed)
    {
        ExpectByte((byte)'|');
        var ident = ReadIdent();

        if (!allowed.IsDefaultOrEmpty)
        {
            bool found = false;
            foreach (var m in allowed)
            {
                if (m == ident) { found = true; break; }
            }
            if (!found) ThrowError($"Atom '{ident}' not in allowed set", PaktErrorCode.TypeMismatch);
        }

        var bytes = Encoding.UTF8.GetBytes(ident);
        EnsureDecodedBuffer(bytes.Length);
        bytes.CopyTo(_decodedBuffer.AsSpan());
        _decodedLength = bytes.Length;
        _usingDecodedBuffer = true;
        return (-1, bytes.Length);
    }

    // -----------------------------------------------------------------------
    // Digit reading helpers
    // -----------------------------------------------------------------------

    private void ReadDigitSep()
    {
        if (_consumed >= _buffer.Length || !IsDigit(_buffer[_consumed]))
            ThrowError("Expected digit", PaktErrorCode.Syntax);
        _consumed++;
        _bytePositionInLine++;
        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (IsDigit(b) || b == '_') { _consumed++; _bytePositionInLine++; }
            else break;
        }
    }

    private void ReadPrefixedDigits(Func<byte, bool> check)
    {
        if (_consumed >= _buffer.Length || !check(_buffer[_consumed]))
            ThrowError("Expected digit after base prefix", PaktErrorCode.Syntax);
        _consumed++;
        _bytePositionInLine++;
        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (check(b) || b == '_') { _consumed++; _bytePositionInLine++; }
            else break;
        }
    }

    private void ReadExactDigits(int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (_consumed >= _buffer.Length)
                ThrowError("Expected digit, got EOF", PaktErrorCode.UnexpectedEof);
            if (!IsDigit(_buffer[_consumed]))
                ThrowError($"Expected digit, got '{(char)_buffer[_consumed]}'", PaktErrorCode.Syntax);
            _consumed++;
            _bytePositionInLine++;
        }
    }

    private void ReadExactHex(int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (_consumed >= _buffer.Length)
                ThrowError("Expected hex digit, got EOF", PaktErrorCode.UnexpectedEof);
            if (!IsHex(_buffer[_consumed]))
                ThrowError($"Expected hex digit, got '{(char)_buffer[_consumed]}'", PaktErrorCode.Syntax);
            _consumed++;
            _bytePositionInLine++;
        }
    }

    private static int HexVal(byte b)
    {
        if (b >= '0' && b <= '9') return b - '0';
        if (b >= 'a' && b <= 'f') return b - 'a' + 10;
        if (b >= 'A' && b <= 'F') return b - 'A' + 10;
        return -1;
    }

    private static long ParseInt64(ReadOnlySpan<byte> span)
    {
        var text = StripUnderscores(span);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt64(text[2..], 16);
        if (text.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
            return -Convert.ToInt64(text[3..], 16);
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt64(text[2..], 2);
        if (text.StartsWith("-0b", StringComparison.OrdinalIgnoreCase))
            return -Convert.ToInt64(text[3..], 2);
        if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt64(text[2..], 8);
        if (text.StartsWith("-0o", StringComparison.OrdinalIgnoreCase))
            return -Convert.ToInt64(text[3..], 8);
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string StripUnderscores(ReadOnlySpan<byte> span)
    {
        Span<char> chars = stackalloc char[span.Length];
        int count = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != '_')
                chars[count++] = (char)span[i];
        }
        return new string(chars[..count]);
    }

    private readonly void AssertTokenIs(PaktTokenType expected)
    {
        if (_tokenType != expected)
            throw new InvalidOperationException(
                $"Cannot get value when token type is {_tokenType}; expected {expected}");
    }
}
