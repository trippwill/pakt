using System.Buffers;

namespace Pakt;

/// <summary>
/// Manages a growable buffer fed from a <see cref="Stream"/>, with NUL-delimited unit framing.
/// Bytes after a NUL boundary are preserved as leftover for the next unit.
/// </summary>
internal sealed class PaktFramedSource : IAsyncDisposable
{
    private const int DefaultBufferSize = 4096;

    private readonly Stream _stream;
    private IMemoryOwner<byte> _owner;
    private int _filled;
    private int _consumed;
    private bool _unitEnded;
    private bool _streamExhausted;

    public PaktFramedSource(Stream stream, int initialBufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (initialBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialBufferSize));

        _stream = stream;
        _owner = MemoryPool<byte>.Shared.Rent(initialBufferSize);
    }

    /// <summary>
    /// The current readable window (filled minus consumed bytes, up to NUL boundary if present).
    /// </summary>
    public ReadOnlyMemory<byte> Available => _owner.Memory.Slice(_consumed, _filled - _consumed);

    /// <summary>
    /// Whether the current unit has been fully received (NUL found or stream EOF).
    /// </summary>
    public bool UnitComplete => _unitEnded || _streamExhausted;

    /// <summary>
    /// Marks <paramref name="count"/> bytes as consumed by the parser.
    /// </summary>
    public void Advance(int count)
    {
        if (count < 0 || _consumed + count > _filled)
            throw new ArgumentOutOfRangeException(nameof(count));
        _consumed += count;
    }

    /// <summary>
    /// Reads more data from the stream into the buffer.
    /// Compacts consumed bytes first. Stops at NUL boundaries.
    /// Returns <c>false</c> when no more data is available for the current unit.
    /// </summary>
    public async ValueTask<bool> FillAsync(CancellationToken ct = default)
    {
        if (_unitEnded)
            return false;

        Compact();
        EnsureCapacity(_filled + 1);

        var writable = _owner.Memory.Slice(_filled);
        var read = await _stream.ReadAsync(writable, ct).ConfigureAwait(false);
        if (read == 0)
        {
            _streamExhausted = true;
            _unitEnded = true;
            return _filled > _consumed;
        }

        // Scan for NUL in the newly read bytes.
        var newBytes = _owner.Memory.Span.Slice(_filled, read);
        var nulIndex = newBytes.IndexOf((byte)0x00);

        if (nulIndex >= 0)
        {
            // Only expose bytes up to (not including) the NUL.
            // The NUL itself is consumed as the delimiter.
            // Bytes after the NUL are leftover for the next unit.
            _filled += nulIndex;
            _unitEnded = true;
            // Shift leftover bytes (after NUL) to the front of the post-filled area.
            // We keep them in the buffer for BeginNextUnit.
            var leftoverStart = nulIndex + 1;
            var leftoverCount = read - leftoverStart;
            if (leftoverCount > 0)
            {
                var src = _owner.Memory.Span.Slice(_filled + leftoverStart, leftoverCount);
                var dst = _owner.Memory.Span.Slice(_filled, leftoverCount);
                src.CopyTo(dst);
            }
            // Store leftover count in _filled temporarily: _filled points to end of current unit data,
            // and we track leftover count separately.
            // Actually, let's use a cleaner approach: store the leftover after _filled.
            // _filled = end of current unit data
            // _leftoverCount = bytes after _filled that belong to the next unit
            _leftoverCount = leftoverCount;
        }
        else
        {
            _filled += read;
        }

        return _filled > _consumed;
    }

    private int _leftoverCount;

    /// <summary>
    /// Prepares the source for the next NUL-delimited unit.
    /// Any leftover bytes from the previous read are made available.
    /// </summary>
    public void BeginNextUnit()
    {
        // Move leftover bytes to the front of the buffer.
        if (_leftoverCount > 0)
        {
            var src = _owner.Memory.Span.Slice(_filled, _leftoverCount);
            src.CopyTo(_owner.Memory.Span);
            _filled = _leftoverCount;
        }
        else
        {
            _filled = 0;
        }

        _consumed = 0;
        _leftoverCount = 0;
        _unitEnded = false;
    }

    public ValueTask DisposeAsync()
    {
        _owner.Dispose();
        return default;
    }

    private void Compact()
    {
        if (_consumed == 0)
            return;

        var remaining = _filled - _consumed;
        if (remaining > 0)
        {
            var src = _owner.Memory.Span.Slice(_consumed, remaining);
            src.CopyTo(_owner.Memory.Span);
        }

        _filled = remaining;
        _consumed = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (_owner.Memory.Length >= required)
            return;

        var newSize = Math.Max(required, _owner.Memory.Length * 2);
        var replacement = MemoryPool<byte>.Shared.Rent(newSize);
        _owner.Memory.Span.Slice(0, _filled).CopyTo(replacement.Memory.Span);
        _owner.Dispose();
        _owner = replacement;
    }
}
