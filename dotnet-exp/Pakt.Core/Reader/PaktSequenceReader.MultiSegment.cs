using System.Runtime.CompilerServices;

namespace Pakt;

// Multi-segment support methods for PaktSequenceReader.
public ref partial struct PaktSequenceReader
{
    /// <summary>
    /// Peek at a byte at the given offset from current position,
    /// handling segment boundaries. Returns false if not enough data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPeek(int offset, out byte value)
    {
        int target = _consumed + offset;
        if (target < _buffer.Length)
        {
            value = _buffer[target];
            return true;
        }

        return TryPeekMultiSegment(offset, out value);
    }

    /// <summary>
    /// Peek at the next byte (offset 0). Inlined fast path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPeekCurrent(out byte value)
    {
        if (_consumed < _buffer.Length)
        {
            value = _buffer[_consumed];
            return true;
        }

        return TryPeekMultiSegment(0, out value);
    }

    /// <summary>
    /// Slow path: peek across segment boundaries.
    /// </summary>
    private bool TryPeekMultiSegment(int offset, out byte value)
    {
        value = 0;
        if (!_isMultiSegment)
            return false;

        // Walk segments from current position to find the byte at offset
        int remaining = offset - (_buffer.Length - _consumed);
        if (remaining < 0)
        {
            // Still in current segment (shouldn't reach here, but safety)
            value = _buffer[_consumed + offset];
            return true;
        }

        SequencePosition pos = _nextPosition;
        while (true)
        {
            SequencePosition prev = pos;
            if (!_sequence.TryGet(ref pos, out ReadOnlyMemory<byte> mem, advance: true))
                return false;

            if (mem.Length == 0)
                continue;

            if (remaining < mem.Length)
            {
                value = mem.Span[remaining];
                return true;
            }

            remaining -= mem.Length;
        }
    }

    /// <summary>
    /// State snapshot for rollback on speculative multi-segment operations.
    /// </summary>
    private readonly struct RollbackState
    {
        public readonly int Consumed;
        public readonly long TotalConsumed;
        public readonly long LineNumber;
        public readonly long BytePositionInLine;
        public readonly SequencePosition CurrentPosition;
        public readonly SequencePosition NextPosition;
        public readonly bool IsLastSegment;

        public RollbackState(ref PaktSequenceReader reader)
        {
            Consumed = reader._consumed;
            TotalConsumed = reader._totalConsumed;
            LineNumber = reader._lineNumber;
            BytePositionInLine = reader._bytePositionInLine;
            CurrentPosition = reader._currentPosition;
            NextPosition = reader._nextPosition;
            IsLastSegment = reader._isLastSegment;
        }
    }

    /// <summary>
    /// Save current position for potential rollback.
    /// </summary>
    private long SavePosition() => _totalConsumed + _consumed;

    /// <summary>
    /// Advance one byte, crossing segment boundary if needed.
    /// Returns false if no more data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AdvanceOne()
    {
        _consumed++;
        _bytePositionInLine++;

        if (_consumed >= _buffer.Length && _isMultiSegment && !_isLastSegment)
        {
            return GetNextSpan();
        }

        return true;
    }

    /// <summary>
    /// Advance one byte that is a newline (LF), updating line tracking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceNewline()
    {
        _consumed++;
        _lineNumber++;
        _bytePositionInLine = 0;
    }

    /// <summary>
    /// Consume bytes scanning for a delimiter, handling segment boundaries.
    /// Used for identifiers, numbers, etc. that end at a known stop byte.
    /// Returns the total length consumed (across segments if needed).
    /// The token start is captured as absolute offset.
    /// </summary>
    private int ScanUntilDelimiter(long tokenStartAbsolute)
    {
        int totalLen = 0;

        while (true)
        {
            ReadOnlySpan<byte> local = _buffer;

            while (_consumed < local.Length)
            {
                if (PaktConstants.Delimiters.Contains(local[_consumed]))
                    return totalLen;

                _consumed++;
                _bytePositionInLine++;
                totalLen++;
            }

            // Exhausted segment
            if (!_isMultiSegment || _isLastSegment)
                return totalLen;

            if (!GetNextSpan())
                return totalLen;
        }
    }

    /// <summary>
    /// Scan an identifier (ident start + ident parts) across segments.
    /// Returns length consumed.
    /// </summary>
    private int ScanIdentParts()
    {
        int totalLen = 0;

        while (true)
        {
            ReadOnlySpan<byte> local = _buffer;

            while (_consumed < local.Length)
            {
                if (!PaktConstants.IsIdentPart(local[_consumed]))
                    return totalLen;

                _consumed++;
                _bytePositionInLine++;
                totalLen++;
            }

            if (!_isMultiSegment || _isLastSegment)
                return totalLen;

            if (!GetNextSpan())
                return totalLen;
        }
    }
}