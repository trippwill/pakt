using System.Buffers;
using System.Diagnostics;

namespace Pakt;

internal sealed class StreamBuffer : IDisposable
{
    private readonly Stream _stream;
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer;
    private int _dataStart;
    private int _dataEnd;
    private readonly int _maxRetain;

    public StreamBuffer(
        Stream stream,
        ArrayPool<byte>? pool,
        int initialSize,
        int maxRetain)
    {
        _stream = stream;
        _pool = pool ?? ArrayPool<byte>.Shared;
        _buffer = _pool.Rent(initialSize);
        _dataStart = 0;
        _dataEnd = 0;
        _maxRetain = maxRetain;
    }

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(_dataStart, _dataEnd - _dataStart);

    public int UnconsumedLength => _dataEnd - _dataStart;

    public bool IsEmpty => _dataStart >= _dataEnd;

    public async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        int unconsumed = _dataEnd - _dataStart;

        // Check before compaction: unconsumed data exceeds max retention
        if (unconsumed > _maxRetain)
        {
            throw PaktParseError
                .BufferedBytesExceeded(default, "Unconsumed buffer data exceeds MaxTokenBytes")
                .ToException();
        }

        // Compact: move unconsumed data to buffer front
        if (_dataStart > 0 && unconsumed > 0)
        {
            _buffer.AsSpan(_dataStart, unconsumed).CopyTo(_buffer);
        }

        _dataEnd = unconsumed;
        _dataStart = 0;

        // Grow: if buffer is full after compaction, rent a larger one
        if (_dataEnd == _buffer.Length)
        {
            byte[] newBuf = _pool.Rent(_buffer.Length * 2);
            _buffer.AsSpan(0, _dataEnd).CopyTo(newBuf);
            _pool.Return(_buffer);
            _buffer = newBuf;
        }

        // Read from stream
        int read = await _stream
            .ReadAsync(_buffer.AsMemory(_dataEnd), ct)
            .ConfigureAwait(false);

        _dataEnd += read;
        return read > 0;
    }

    public void Advance(int consumed)
    {
        _dataStart += consumed;
        Debug.Assert(_dataStart <= _dataEnd);
    }

    public void Dispose()
    {
        _pool.Return(_buffer);
        _buffer = [];
    }
}