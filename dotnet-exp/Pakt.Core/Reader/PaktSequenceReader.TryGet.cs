using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pakt;

// Typed value accessors for PaktSequenceReader.
public ref partial struct PaktSequenceReader
{
    private const int StackAllocThreshold = 256;

    // ───────────────────── String ─────────────────────

    /// <summary>
    /// Decode the current string token value to a CLR string.
    /// Handles quote stripping, escape sequences, raw strings, and multiline.
    /// </summary>
    public string GetString()
    {
        ReadOnlySequence<byte> seq = _valueSequence;
        if (seq.Length == 0) return string.Empty;

        // Copy to contiguous buffer for decoding
        ReadOnlySpan<byte> raw = seq.IsSingleSegment
            ? seq.FirstSpan
            : CopyToScratch(seq);

        return DecodeStringValue(raw, _valueIsEscaped);
    }

    private static string DecodeStringValue(ReadOnlySpan<byte> raw, bool hasEscapes)
    {
        if (raw.Length < 2) return string.Empty;

        // Determine prefix and delimiter
        bool isRaw = raw[0] == (byte)'r';
        int prefixLen = isRaw ? 1 : 0;
        ReadOnlySpan<byte> afterPrefix = raw[prefixLen..];

        bool isTriple = afterPrefix.Length >= 6
            && afterPrefix[0] == PaktConstants.SingleQuote
            && afterPrefix[1] == PaktConstants.SingleQuote
            && afterPrefix[2] == PaktConstants.SingleQuote;

        int delimLen = isTriple ? 3 : 1;
        ReadOnlySpan<byte> content = afterPrefix[delimLen..^delimLen];

        if (isRaw || !hasEscapes)
            return Encoding.UTF8.GetString(content);

        return UnescapeString(content);
    }

    private static string UnescapeString(ReadOnlySpan<byte> content)
    {
        var sb = new StringBuilder(content.Length);
        int i = 0;
        while (i < content.Length)
        {
            byte b = content[i];
            if (b != PaktConstants.Backslash)
            {
                int start = i;
                while (i < content.Length && content[i] != PaktConstants.Backslash)
                    i++;
                sb.Append(Encoding.UTF8.GetString(content[start..i]));
                continue;
            }

            if (i + 1 >= content.Length) break;
            byte esc = content[i + 1];
            switch (esc)
            {
                case (byte)'\\': sb.Append('\\'); i += 2; break;
                case (byte)'\'': sb.Append('\''); i += 2; break;
                case (byte)'n': sb.Append('\n'); i += 2; break;
                case (byte)'r': sb.Append('\r'); i += 2; break;
                case (byte)'t': sb.Append('\t'); i += 2; break;
                case (byte)'u':
                    if (i + 5 < content.Length)
                    {
                        int cp = HexVal(content[i + 2]) << 12
                            | HexVal(content[i + 3]) << 8
                            | HexVal(content[i + 4]) << 4
                            | HexVal(content[i + 5]);
                        sb.Append((char)cp);
                        i += 6;
                    }
                    else { sb.Append('\\'); i++; }
                    break;
                default:
                    sb.Append('\\'); i++; break;
            }
        }
        return sb.ToString();
    }

    // ───────────────────── Integers ─────────────────────

    public int GetInt32()
    {
        long val = GetInt64();
        if (val is < int.MinValue or > int.MaxValue)
            ThrowSyntax($"Value {val} is out of range for Int32");
        return (int)val;
    }

    public bool TryGetInt32(out int value)
    {
        if (TryGetInt64(out long lval) && lval is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)lval;
            return true;
        }
        value = 0;
        return false;
    }

    public long GetInt64()
    {
        if (!TryGetInt64(out long val))
            ThrowSyntax("Invalid integer value");
        return val;
    }

    public bool TryGetInt64(out long value)
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        return ParseInteger(raw, out value);
    }

    private static bool ParseInteger(ReadOnlySpan<byte> raw, out long value)
    {
        value = 0;

        // Strip underscores if present
        if (raw.Contains((byte)'_'))
        {
            Span<byte> clean = stackalloc byte[raw.Length];
            int len = 0;
            for (int i = 0; i < raw.Length; i++)
                if (raw[i] != (byte)'_')
                    clean[len++] = raw[i];
            return ParseInteger(clean[..len], out value);
        }

        bool neg = raw.Length > 0 && raw[0] == (byte)'-';
        ReadOnlySpan<byte> digits = neg ? raw[1..] : raw;

        // Base prefix
        if (digits.Length >= 2 && digits[0] == (byte)'0')
        {
            byte prefix = digits[1];
            if (prefix is (byte)'x' or (byte)'X')
                return ParseBaseLong(digits[2..], 16, neg, out value);
            if (prefix is (byte)'b' or (byte)'B')
                return ParseBaseLong(digits[2..], 2, neg, out value);
            if (prefix is (byte)'o' or (byte)'O')
                return ParseBaseLong(digits[2..], 8, neg, out value);
        }

        if (Utf8Parser.TryParse(raw, out value, out int consumed) && consumed == raw.Length)
            return true;

        return false;
    }

    private static bool ParseBaseLong(ReadOnlySpan<byte> digits, int @base, bool neg, out long value)
    {
        value = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            if (digits[i] == (byte)'_') continue;
            int d = @base switch
            {
                16 => HexVal(digits[i]),
                2 => digits[i] - '0',
                8 => digits[i] - '0',
                _ => -1,
            };
            if (d < 0 || d >= @base) return false;
            value = value * @base + d;
        }
        if (neg) value = -value;
        return true;
    }

    // ───────────────────── Floating Point ─────────────────────

    public double GetDouble()
    {
        if (!TryGetDouble(out double val))
            ThrowSyntax("Invalid floating-point value");
        return val;
    }

    public bool TryGetDouble(out double value)
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        ReadOnlySpan<byte> input = StripUnderscores(raw, out Span<byte> buf) ? buf : raw;

        if (Utf8Parser.TryParse(input, out value, out int consumed) && consumed == input.Length)
            return true;

        // Fallback
        string str = Encoding.UTF8.GetString(input);
        return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public float GetFloat()
    {
        double d = GetDouble();
        return (float)d;
    }

    public decimal GetDecimal()
    {
        if (!TryGetDecimal(out decimal val))
            ThrowSyntax("Invalid decimal value");
        return val;
    }

    public bool TryGetDecimal(out decimal value)
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        ReadOnlySpan<byte> input = StripUnderscores(raw, out Span<byte> buf) ? buf : raw;

        if (Utf8Parser.TryParse(input, out value, out int consumed) && consumed == input.Length)
            return true;

        string str = Encoding.UTF8.GetString(input);
        return decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    // ───────────────────── Bool ─────────────────────

    public bool GetBool()
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        if (raw.SequenceEqual("true"u8)) return true;
        if (raw.SequenceEqual("false"u8)) return false;
        ThrowSyntax("Expected 'true' or 'false'");
        return false;
    }

    // ───────────────────── Guid (UUID) ─────────────────────

    public Guid GetGuid()
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        if (Utf8Parser.TryParse(raw, out Guid value, out int consumed, 'D') && consumed == raw.Length)
            return value;
        ThrowSyntax("Invalid UUID value");
        return default;
    }

    // ───────────────────── Date ─────────────────────

    public DateOnly GetDate()
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        string str = Encoding.UTF8.GetString(raw);
        if (DateOnly.TryParse(str, CultureInfo.InvariantCulture, out DateOnly val))
            return val;
        ThrowSyntax("Invalid date value");
        return default;
    }

    // ───────────────────── Timestamp ─────────────────────

    public DateTimeOffset GetTimestamp()
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        string str = Encoding.UTF8.GetString(raw);
        if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset val))
            return val;
        ThrowSyntax("Invalid timestamp value");
        return default;
    }

    // ───────────────────── Binary ─────────────────────

    /// <summary>
    /// Decode the current binary literal (x'hex' or b'base64') to a byte array.
    /// </summary>
    public byte[] GetBytes()
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        if (raw.Length < 3) return [];

        byte prefix = raw[0];
        // Strip prefix and quotes: x'...' or b'...'
        ReadOnlySpan<byte> content = raw[2..^1];

        if (prefix == (byte)'x')
        {
            return Convert.FromHexString(Encoding.ASCII.GetString(content));
        }

        if (prefix == (byte)'b')
        {
            return Convert.FromBase64String(Encoding.ASCII.GetString(content));
        }

        ThrowSyntax("Invalid binary prefix");
        return [];
    }

    // ───────────────────── Atom ─────────────────────

    /// <summary>
    /// Get the atom value (the identifier after the | prefix).
    /// </summary>
    public string GetAtom()
    {
        ReadOnlySpan<byte> raw = GetValueSpanContiguous();
        // Strip the | prefix
        return Encoding.UTF8.GetString(raw[1..]);
    }

    // ───────────────────── Helpers ─────────────────────

    /// <summary>
    /// Get the value bytes as a contiguous span. For single-segment sequences,
    /// returns the span directly. For multi-segment, copies to stackalloc or rented buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetValueSpanContiguous()
    {
        ReadOnlySequence<byte> seq = _valueSequence;
        if (seq.IsSingleSegment)
            return seq.FirstSpan;

        return CopyToScratch(seq);
    }

    private ReadOnlySpan<byte> CopyToScratch(ReadOnlySequence<byte> seq)
    {
        int len = checked((int)seq.Length);
        byte[] buf = new byte[len]; // TODO: pool for larger values
        seq.CopyTo(buf);
        return buf;
    }

    private static bool StripUnderscores(ReadOnlySpan<byte> raw, out Span<byte> result)
    {
        if (!raw.Contains((byte)'_'))
        {
            result = default;
            return false;
        }

        byte[] buf = new byte[raw.Length];
        int len = 0;
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] != (byte)'_')
                buf[len++] = raw[i];
        result = buf.AsSpan(0, len);
        return true;
    }

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0,
    };
}
