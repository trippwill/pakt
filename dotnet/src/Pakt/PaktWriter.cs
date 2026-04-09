using System.Buffers;
using System.Globalization;
using System.Text;

namespace Pakt;

/// <summary>
/// A forward-only writer that produces compact PAKT-formatted UTF-8 output
/// to an <see cref="IBufferWriter{T}"/>.
/// </summary>
public sealed class PaktWriter : IDisposable
{
    private readonly IBufferWriter<byte> _output;
    private WriterFrame[] _stack;
    private int _stackDepth;
    private bool _disposed;

    private enum CompositeKind : byte { None, Struct, Tuple, List, Map, Pack }
    private enum MapPhase : byte { Key, Value }

    private struct WriterFrame
    {
        public CompositeKind Kind;
        public int ElementCount;
        public MapPhase MapPhase;
    }

    /// <summary>
    /// Initializes a new <see cref="PaktWriter"/> that writes to the specified buffer.
    /// </summary>
    /// <param name="output">The buffer writer to write PAKT output to.</param>
    /// <param name="options">Optional writer configuration.</param>
    public PaktWriter(IBufferWriter<byte> output, PaktWriterOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _stack = new WriterFrame[64];
        _stackDepth = 0;
    }

    /// <summary>Writes the start of a top-level assignment: <c>name:type = </c>.</summary>
    public void WriteAssignmentStart(string name, PaktType type)
    {
        ThrowIfDisposed();
        WriteStatementHeader(name, type);
        WriteRaw(" = "u8);
    }

    /// <summary>Writes the end of a top-level assignment (newline).</summary>
    public void WriteAssignmentEnd()
    {
        ThrowIfDisposed();
        WriteRaw("\n"u8);
    }

    /// <summary>Writes the start of a top-level pack: <c>name:type &lt;&lt; </c>.</summary>
    public void WritePackStart(string name, PaktType type)
    {
        ThrowIfDisposed();
        WriteStatementHeader(name, type);
        WriteRaw(" << "u8);
        PushFrame(CompositeKind.Pack);
    }

    /// <summary>Writes the end of a top-level pack (newline).</summary>
    public void WritePackEnd()
    {
        ThrowIfDisposed();
        PopFrame(CompositeKind.Pack);
        WriteRaw("\n"u8);
    }

    /// <summary>Writes the start of a struct composite: <c>{</c>.</summary>
    public void WriteStructStart()
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("{"u8);
        PushFrame(CompositeKind.Struct);
    }

    /// <summary>Writes the end of a struct composite: <c>}</c> or <c> }</c>.</summary>
    public void WriteStructEnd()
    {
        ThrowIfDisposed();
        WriteCompositeEnd(CompositeKind.Struct, (byte)'}');
    }

    /// <summary>Writes the start of a tuple composite: <c>(</c>.</summary>
    public void WriteTupleStart()
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("("u8);
        PushFrame(CompositeKind.Tuple);
    }

    /// <summary>Writes the end of a tuple composite: <c>)</c> or <c> )</c>.</summary>
    public void WriteTupleEnd()
    {
        ThrowIfDisposed();
        WriteCompositeEnd(CompositeKind.Tuple, (byte)')');
    }

    /// <summary>Writes the start of a list composite: <c>[</c>.</summary>
    public void WriteListStart()
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("["u8);
        PushFrame(CompositeKind.List);
    }

    /// <summary>Writes the end of a list composite: <c>]</c> or <c> ]</c>.</summary>
    public void WriteListEnd()
    {
        ThrowIfDisposed();
        WriteCompositeEnd(CompositeKind.List, (byte)']');
    }

    /// <summary>Writes the start of a map composite: <c>&lt;</c>.</summary>
    public void WriteMapStart()
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("<"u8);
        PushFrame(CompositeKind.Map);
    }

    /// <summary>Writes the end of a map composite: <c>&gt;</c> or <c> &gt;</c>.</summary>
    public void WriteMapEnd()
    {
        ThrowIfDisposed();
        WriteCompositeEnd(CompositeKind.Map, (byte)'>');
    }

    /// <summary>Writes a string scalar value with single-quote escaping.</summary>
    public void WriteStringValue(ReadOnlySpan<char> value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("'"u8);
        WriteEscapedString(value);
        WriteRaw("'"u8);
    }

    /// <summary>Writes an integer scalar value.</summary>
    public void WriteIntValue(long value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        Span<byte> buf = stackalloc byte[21]; // -9223372036854775808 is 20 chars + sign
        value.TryFormat(buf, out int written, default, CultureInfo.InvariantCulture);
        WriteRaw(buf[..written]);
    }

    /// <summary>Writes a decimal scalar value. Always includes a decimal point.</summary>
    public void WriteDecimalValue(decimal value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        Span<char> charBuf = stackalloc char[64];
        value.TryFormat(charBuf, out int charWritten, default, CultureInfo.InvariantCulture);
        var formatted = charBuf[..charWritten];

        // Ensure decimal point is present
        if (!formatted.Contains('.'))
        {
            charBuf[charWritten] = '.';
            charBuf[charWritten + 1] = '0';
            charWritten += 2;
        }

        Span<byte> buf = stackalloc byte[charWritten];
        Encoding.UTF8.GetBytes(charBuf[..charWritten], buf);
        WriteRaw(buf);
    }

    /// <summary>Writes a floating-point scalar value.</summary>
    public void WriteFloatValue(double value)
    {
        ThrowIfDisposed();
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("NaN and Infinity are not valid PAKT float values.", nameof(value));

        PrependSeparator();
        Span<char> charBuf = stackalloc char[64];
        value.TryFormat(charBuf, out int charWritten, "G17", CultureInfo.InvariantCulture);
        var formatted = charBuf[..charWritten];

        // Normalize 'E' to 'e' for PAKT convention
        for (int i = 0; i < formatted.Length; i++)
        {
            if (formatted[i] == 'E')
                formatted[i] = 'e';
        }

        Span<byte> buf = stackalloc byte[charWritten];
        Encoding.UTF8.GetBytes(formatted, buf);
        WriteRaw(buf);
    }

    /// <summary>Writes a boolean scalar value (<c>true</c> or <c>false</c>).</summary>
    public void WriteBoolValue(bool value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw(value ? "true"u8 : "false"u8);
    }

    /// <summary>Writes a nil value.</summary>
    public void WriteNilValue()
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("nil"u8);
    }

    /// <summary>Writes a UUID scalar value in standard format.</summary>
    public void WriteUuidValue(Guid value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        Span<char> charBuf = stackalloc char[36];
        value.TryFormat(charBuf, out _, "D");
        Span<byte> buf = stackalloc byte[36];
        Encoding.UTF8.GetBytes(charBuf, buf);
        WriteRaw(buf);
    }

    /// <summary>Writes a date scalar value in <c>YYYY-MM-DD</c> format.</summary>
    public void WriteDateValue(DateOnly value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        Span<char> charBuf = stackalloc char[10];
        value.TryFormat(charBuf, out int written, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Span<byte> buf = stackalloc byte[written];
        Encoding.UTF8.GetBytes(charBuf[..written], buf);
        WriteRaw(buf);
    }

    /// <summary>Writes a timestamp scalar value in ISO 8601 format.</summary>
    public void WriteTimestampValue(DateTimeOffset value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        Span<char> charBuf = stackalloc char[32];
        int written;

        if (value.Offset == TimeSpan.Zero)
        {
            value.TryFormat(charBuf, out written, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
        else
        {
            value.TryFormat(charBuf, out written, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        Span<byte> buf = stackalloc byte[written];
        Encoding.UTF8.GetBytes(charBuf[..written], buf);
        WriteRaw(buf);
    }

    /// <summary>Writes a binary scalar value as hex: <c>x'hexdigits'</c>.</summary>
    public void WriteBinValue(ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("x'"u8);

        Span<byte> hexBuf = stackalloc byte[2];
        foreach (byte b in value)
        {
            hexBuf[0] = ToHexDigit(b >> 4);
            hexBuf[1] = ToHexDigit(b & 0x0F);
            WriteRaw(hexBuf);
        }

        WriteRaw("'"u8);
    }

    /// <summary>Writes an atom scalar value: <c>|value</c>.</summary>
    public void WriteAtomValue(ReadOnlySpan<char> value)
    {
        ThrowIfDisposed();
        PrependSeparator();
        WriteRaw("|"u8);
        WriteUtf8Chars(value);
    }

    /// <summary>Writes the map key-value separator: <c> ; </c>.</summary>
    public void WriteMapKeySeparator()
    {
        ThrowIfDisposed();
        WriteRaw(" ; "u8);
    }

    /// <summary>Flushes any buffered data to the underlying writer.</summary>
    public void Flush()
    {
        // IBufferWriter<byte> does not have a Flush method.
        // Data is written directly via GetSpan/Advance, so this is a no-op.
        // Provided for API completeness and forward compatibility.
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }

    private void WriteStatementHeader(string name, PaktType type)
    {
        var typeStr = type.ToString();
        WriteUtf8String(name);
        WriteRaw(":"u8);
        WriteUtf8String(typeStr);
    }

    private void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        var span = _output.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        _output.Advance(bytes.Length);
    }

    private void WriteUtf8String(string value)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        var span = _output.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(value, span);
        _output.Advance(written);
    }

    private void WriteUtf8Chars(ReadOnlySpan<char> value)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        var span = _output.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(value, span);
        _output.Advance(written);
    }

    private void WriteEscapedString(ReadOnlySpan<char> value)
    {
        Span<byte> charBuf = stackalloc byte[4];

        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    WriteRaw("\\\\"u8);
                    break;
                case '\'':
                    WriteRaw("\\'"u8);
                    break;
                case '\n':
                    WriteRaw("\\n"u8);
                    break;
                case '\r':
                    WriteRaw("\\r"u8);
                    break;
                case '\t':
                    WriteRaw("\\t"u8);
                    break;
                default:
                    ReadOnlySpan<char> ch = new(in c);
                    int written = Encoding.UTF8.GetBytes(ch, charBuf);
                    WriteRaw(charBuf[..written]);
                    break;
            }
        }
    }

    private void PrependSeparator()
    {
        if (_stackDepth == 0)
            return;

        ref var frame = ref _stack[_stackDepth - 1];

        if (frame.Kind == CompositeKind.Map)
        {
            if (frame.MapPhase == MapPhase.Key)
            {
                if (frame.ElementCount > 0)
                    WriteRaw(", "u8);
                else
                    WriteRaw(" "u8);
                frame.MapPhase = MapPhase.Value;
            }
            else
            {
                // Value phase: no auto-separator (caller wrote ` ; `)
                frame.ElementCount++;
                frame.MapPhase = MapPhase.Key;
            }
        }
        else if (frame.Kind == CompositeKind.Pack)
        {
            // Pack elements: no leading space (already in header), comma between elements
            if (frame.ElementCount > 0)
                WriteRaw(", "u8);
            frame.ElementCount++;
        }
        else
        {
            // Struct, Tuple, List
            if (frame.ElementCount > 0)
                WriteRaw(", "u8);
            else
                WriteRaw(" "u8);
            frame.ElementCount++;
        }
    }

    private void PushFrame(CompositeKind kind)
    {
        if (_stackDepth >= _stack.Length)
        {
            var newStack = new WriterFrame[_stack.Length * 2];
            _stack.CopyTo(newStack, 0);
            _stack = newStack;
        }

        _stack[_stackDepth] = new WriterFrame
        {
            Kind = kind,
            ElementCount = 0,
            MapPhase = MapPhase.Key,
        };
        _stackDepth++;
    }

    private void PopFrame(CompositeKind expected)
    {
        if (_stackDepth == 0 || _stack[_stackDepth - 1].Kind != expected)
            throw new InvalidOperationException($"Expected frame kind {expected} but found {(_stackDepth > 0 ? _stack[_stackDepth - 1].Kind : CompositeKind.None)}.");
        _stackDepth--;
    }

    private void WriteCompositeEnd(CompositeKind kind, byte closeBracket)
    {
        int count = _stackDepth > 0 ? _stack[_stackDepth - 1].ElementCount : 0;
        PopFrame(kind);
        if (count > 0)
            WriteRaw(" "u8);
        Span<byte> buf = [closeBracket];
        WriteRaw(buf);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static byte ToHexDigit(int value) =>
        (byte)(value < 10 ? '0' + value : 'A' + value - 10);
}
