using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Pakt.Serialization;

namespace Pakt;

/// <summary>
/// Asynchronous statement-level reader for PAKT documents.
/// This is the primary consumption API — iterates statements one at a time from a Stream.
/// </summary>
/// <remarks>
/// For MVP, the entire stream is buffered into memory. The async API shape is designed
/// for future optimization with true chunked streaming via PipeReader.
/// </remarks>
public sealed class PaktStreamReader : IAsyncDisposable
{
    private readonly byte[] _buffer;
    private readonly int _length;
    private readonly PaktReaderOptions _options;
    private int _offset;
    private bool _disposed;

    // Current statement metadata
    private string? _statementName;
    private PaktType? _statementType;
    private bool _isStream;
    private int _valueStart; // byte offset where value begins (after '=' or '<<')
    private bool _hasStatement;

    private PaktStreamReader(byte[] buffer, int length, PaktReaderOptions options)
    {
        _buffer = buffer;
        _length = length;
        _options = options;
    }

    /// <summary>
    /// Creates a new <see cref="PaktStreamReader"/> from a stream.
    /// The entire stream is read into memory.
    /// </summary>
    public static async ValueTask<PaktStreamReader> CreateAsync(
        Stream stream,
        PaktReaderOptions options = default,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] buffer;
        int length;

        if (stream is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            buffer = ArrayPool<byte>.Shared.Rent((int)ms.Length);
            length = (int)ms.Length;
            segment.AsSpan().CopyTo(buffer);
        }
        else
        {
            using var temp = new MemoryStream();
            await stream.CopyToAsync(temp, ct).ConfigureAwait(false);
            length = (int)temp.Length;
            buffer = ArrayPool<byte>.Shared.Rent(length);
            temp.GetBuffer().AsSpan(0, length).CopyTo(buffer);
        }

        return new PaktStreamReader(buffer, length, options);
    }

    /// <summary>
    /// Creates a new <see cref="PaktStreamReader"/> from a byte array.
    /// </summary>
    public static PaktStreamReader Create(ReadOnlySpan<byte> data, PaktReaderOptions options = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        return new PaktStreamReader(buffer, data.Length, options);
    }

    /// <summary>The name of the current statement.</summary>
    public string StatementName => _statementName ?? throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

    /// <summary>The type of the current statement.</summary>
    public PaktType StatementType => _statementType ?? throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

    /// <summary>Whether the current statement uses stream syntax (<c>&lt;&lt;</c>).</summary>
    public bool IsStream => _isStream;

    /// <summary>Advance to the next top-level statement.</summary>
    /// <returns><c>true</c> if a statement was read; <c>false</c> at end of document.</returns>
    public ValueTask<bool> ReadStatementAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        // If there's a previous statement that wasn't consumed, skip it
        if (_hasStatement)
        {
            SkipCurrentStatementCore();
        }

        var remaining = _buffer.AsSpan(_offset, _length - _offset);
        if (remaining.IsEmpty)
        {
            _hasStatement = false;
            return new ValueTask<bool>(false);
        }

        var reader = new PaktReader(remaining, _options);
        try
        {
            if (!reader.Read())
            {
                _hasStatement = false;
                return new ValueTask<bool>(false);
            }

            if (reader.TokenType != PaktTokenType.AssignStart && reader.TokenType != PaktTokenType.StreamStart)
                throw new PaktException($"Expected statement start, got {reader.TokenType}", reader.Position, PaktErrorCode.Syntax);

            _statementName = reader.StatementName;
            _statementType = reader.StatementType;
            _isStream = reader.IsStreamStatement;
            _valueStart = _offset + reader.BytesConsumed;
            _hasStatement = true;

            return new ValueTask<bool>(true);
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// Deserialize the current assignment's value using generated type info.
    /// Call after <see cref="ReadStatementAsync"/> returns true and <see cref="IsStream"/> is false.
    /// </summary>
    public T Deserialize<T>(PaktTypeInfo<T> typeInfo)
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");
        if (_isStream)
            throw new InvalidOperationException("Current statement is a stream. Use ReadStreamElements instead.");
        if (typeInfo.Deserialize is null)
            throw new InvalidOperationException($"PaktTypeInfo<{typeof(T).Name}> does not have a Deserialize delegate. Use source-generated type info.");

        var result = DeserializeCurrentValue(typeInfo);
        _hasStatement = false;
        return result;
    }

    /// <summary>
    /// Iterate elements of a stream statement as <see cref="IAsyncEnumerable{T}"/>.
    /// Call after <see cref="ReadStatementAsync"/> returns true and <see cref="IsStream"/> is true.
    /// </summary>
    public async IAsyncEnumerable<T> ReadStreamElements<T>(
        PaktTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");
        if (!_isStream)
            throw new InvalidOperationException("Current statement is not a stream. Use Deserialize instead.");
        if (typeInfo.Deserialize is null)
            throw new InvalidOperationException($"PaktTypeInfo<{typeof(T).Name}> does not have a Deserialize delegate. Use source-generated type info.");

        var elements = DeserializeStreamElementsCore(typeInfo);
        _hasStatement = false;
        foreach (var element in elements)
        {
            ct.ThrowIfCancellationRequested();
            yield return element;
        }
    }

    /// <summary>Skip the current statement without deserializing.</summary>
    public ValueTask SkipAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

        SkipCurrentStatementCore();
        _hasStatement = false;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _disposed = true;
        }
        return ValueTask.CompletedTask;
    }

    private T DeserializeCurrentValue<T>(PaktTypeInfo<T> typeInfo)
    {
        var synthetic = BuildSyntheticAssignment();
        var headerLen = synthetic.Length - (_length - _valueStart);
        var reader = new PaktReader(synthetic, _options);
        try
        {
            reader.Read(); // AssignStart
            reader.Read(); // Value start (StructStart, ScalarValue, etc.)
            var result = typeInfo.Deserialize!(ref reader);

            // Read through AssignEnd
            while (reader.Read())
            {
                if (reader.TokenType == PaktTokenType.AssignEnd)
                    break;
            }

            _offset = _valueStart + (reader.BytesConsumed - headerLen);
            return result;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private List<T> DeserializeStreamElementsCore<T>(PaktTypeInfo<T> typeInfo)
    {
        var synthetic = BuildSyntheticStream();
        var headerLen = synthetic.Length - (_length - _valueStart);
        var reader = new PaktReader(synthetic, _options);
        try
        {
            reader.Read(); // StreamStart
            var list = new List<T>();

            while (reader.Read())
            {
                if (reader.TokenType == PaktTokenType.StreamEnd)
                    break;

                list.Add(typeInfo.Deserialize!(ref reader));
            }

            _offset = _valueStart + (reader.BytesConsumed - headerLen);
            return list;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void SkipCurrentStatementCore()
    {
        byte[] synthetic;
        int headerLen;

        if (_isStream)
        {
            synthetic = BuildSyntheticStream();
            headerLen = synthetic.Length - (_length - _valueStart);
        }
        else
        {
            synthetic = BuildSyntheticAssignment();
            headerLen = synthetic.Length - (_length - _valueStart);
        }

        var reader = new PaktReader(synthetic, _options);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == PaktTokenType.AssignEnd || reader.TokenType == PaktTokenType.StreamEnd)
                    break;
            }

            _offset = _valueStart + (reader.BytesConsumed - headerLen);
        }
        finally
        {
            reader.Dispose();
        }
    }

    // Builds: _:<type> = <remaining_value_bytes>
    private byte[] BuildSyntheticAssignment()
    {
        var typeStr = _statementType!.ToString();
        var header = Encoding.UTF8.GetBytes($"_:{typeStr} = ");
        var valueSpan = _buffer.AsSpan(_valueStart, _length - _valueStart);
        var result = new byte[header.Length + valueSpan.Length];
        header.CopyTo(result, 0);
        valueSpan.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    // Builds: _:<type> << <remaining_value_bytes>
    private byte[] BuildSyntheticStream()
    {
        var typeStr = _statementType!.ToString();
        var header = Encoding.UTF8.GetBytes($"_:{typeStr} << ");
        var valueSpan = _buffer.AsSpan(_valueStart, _length - _valueStart);
        var result = new byte[header.Length + valueSpan.Length];
        header.CopyTo(result, 0);
        valueSpan.CopyTo(result.AsSpan(header.Length));
        return result;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PaktStreamReader));
    }
}
