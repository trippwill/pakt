using System.Buffers;

namespace Pakt;

/// <summary>
/// Async-fill, sync-consume structural reader over <see cref="Stream"/>.
/// Buffers complete NUL-framed units, then delegates parsing to <see cref="PaktMemoryReader"/>.
/// <para>
/// Consume pattern:
/// <code>
/// await using var reader = new PaktStreamReader(stream);
/// while (await reader.ReadUnitAsync(ct))
/// {
///     while (reader.Read()) { /* process tokens */ }
/// }
/// </code>
/// </para>
/// </summary>
public sealed class PaktStreamReader : IAsyncDisposable, IPaktReader
{
    private readonly StreamBuffer _buffer;
    private readonly PaktReaderOptions _options;
    private readonly ArrayPool<byte> _pool;
    private PaktMemoryReader? _unitReader;
    private bool _streamEnded;

    public PaktStreamReader(Stream stream, PaktReaderOptions? options = null)
    {
        _options = options ?? PaktReaderOptions.Default;
        _pool = _options.BufferPool ?? ArrayPool<byte>.Shared;
        _buffer = new StreamBuffer(stream, _pool, _options.InitialBufferSize, _options.MaxTokenBytes);
    }

    // ───────────────────── public properties ─────────────────────

    public PaktTokenType TokenType => _unitReader?.TokenType ?? PaktTokenType.None;

    public ReadOnlySpan<byte> ValueSpan => _unitReader is not null ? _unitReader.ValueSpan : default;

    public int Depth => _unitReader?.Depth ?? 0;

    public long ByteOffset => _unitReader?.ByteOffset ?? 0;
    public int Line => _unitReader?.Line ?? 0;
    public int Column => _unitReader?.Column ?? 0;

    // ───────────────────── async unit fill ─────────────────────

    /// <summary>
    /// Buffer the next complete NUL-delimited unit from the stream.
    /// Returns <c>false</c> when no more units are available.
    /// After this returns <c>true</c>, call <see cref="Read"/> to consume tokens.
    /// </summary>
    public async ValueTask<bool> ReadUnitAsync(CancellationToken ct = default)
    {
        _unitReader?.Dispose();
        _unitReader = null;

        if (_streamEnded && _buffer.IsEmpty)
            return false;

        // Buffer until we find a NUL byte or EOF
        while (true)
        {
            // Scan current buffer for NUL
            ReadOnlySpan<byte> span = _buffer.Span;
            int nulIdx = span.IndexOf((byte)0);

            if (nulIdx >= 0)
            {
                // Found NUL — unit is bytes [0..nulIdx), NUL consumed
                ReadOnlyMemory<byte> unitData = GetBufferMemory(nulIdx);
                _unitReader = new PaktMemoryReader(unitData, _options);
                _buffer.Advance(nulIdx + 1); // consume unit + NUL
                return true;
            }

            if (_streamEnded)
            {
                // No more data — remaining bytes are the final unit (if any)
                if (_buffer.IsEmpty)
                    return false;

                ReadOnlyMemory<byte> unitData = GetBufferMemory(_buffer.UnconsumedLength);
                _unitReader = new PaktMemoryReader(unitData, _options);
                _buffer.Advance(_buffer.UnconsumedLength);
                return true;
            }

            // Need more data
            bool gotData = await _buffer.FillAsync(ct).ConfigureAwait(false);
            if (!gotData)
                _streamEnded = true;
        }
    }

    /// <summary>
    /// Copy the specified number of bytes from the buffer into a standalone memory block.
    /// Required because StreamBuffer may compact/reallocate after Advance.
    /// </summary>
    private ReadOnlyMemory<byte> GetBufferMemory(int length)
    {
        if (length == 0)
            return ReadOnlyMemory<byte>.Empty;

        byte[] copy = _pool.Rent(length);
        _buffer.Span[..length].CopyTo(copy);
        return new ReadOnlyMemory<byte>(copy, 0, length);
    }

    // ───────────────────── sync read (delegates to unit reader) ─────────────────────

    /// <inheritdoc />
    public bool Read()
    {
        if (_unitReader is null)
            return false;
        return _unitReader.Read();
    }

    // ───────────────────── typed accessors (delegate to unit reader) ─────────────────────

    public string ReadString() => EnsureReader().ReadString();
    public string? ReadStringOrNil() => EnsureReader().ReadStringOrNil();
    public int ReadInt32() => EnsureReader().ReadInt32();
    public long ReadInt64() => EnsureReader().ReadInt64();
    public double ReadDouble() => EnsureReader().ReadDouble();
    public decimal ReadDecimal() => EnsureReader().ReadDecimal();
    public bool ReadBool() => EnsureReader().ReadBool();
    public bool TryReadNil() => EnsureReader().TryReadNil();
    public ReadOnlySpan<byte> ReadRawValue() => EnsureReader().ReadRawValue();
    public void ExpectToken(PaktTokenType expected) => EnsureReader().ExpectToken(expected);
    public bool TryExpectToken(PaktTokenType expected) => EnsureReader().TryExpectToken(expected);
    public bool VerifyTypeAnnotation(ReadOnlySpan<byte> expectedSignature) =>
        EnsureReader().VerifyTypeAnnotation(expectedSignature);

    private PaktMemoryReader EnsureReader() =>
        _unitReader ?? throw new InvalidOperationException("No unit loaded. Call ReadUnitAsync first.");

    // ───────────────────── dispose ─────────────────────

    public ValueTask DisposeAsync()
    {
        _unitReader?.Dispose();
        _unitReader = null;
        _buffer.Dispose();
        return default;
    }
}
