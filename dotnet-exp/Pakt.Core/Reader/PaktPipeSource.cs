using System.Buffers;
using System.IO.Pipelines;

namespace Pakt;

/// <summary>
/// Wraps a <see cref="PipeReader"/> and provides buffer management for
/// incremental stream-backed PAKT deserialization.
/// <para>
/// <see cref="PipeReader.AdvanceTo(SequencePosition, SequencePosition)"/> handles
/// buffer compaction, memory pooling, and unconsumed-byte retention automatically.
/// The <see cref="ReadOnlySequence{T}"/> returned by <see cref="PipeReader.ReadAsync"/>
/// may be multi-segment, which <see cref="PaktReader"/> already handles.
/// </para>
/// </summary>
public sealed class PaktPipeSource : IAsyncDisposable
{
    private readonly PipeReader _pipeReader;
    private readonly bool _ownsPipeReader;
    private PaktReaderState _readerState;
    private ReadOnlySequence<byte> _currentData;
    private bool _isCompleted;

    private PaktPipeSource(PipeReader pipeReader, bool ownsPipeReader, PaktReaderOptions? options = null)
    {
        _pipeReader = pipeReader;
        _ownsPipeReader = ownsPipeReader;
        _readerState = options is not null
            ? new PaktReaderState(0, 0, PaktReaderPhase.Start, PaktTokenType.None,
                default, false, 0, 0, options)
            : default;
    }

    /// <summary>Create from a <see cref="Stream"/> with configurable buffer size.</summary>
    internal static PaktPipeSource Create(
        Stream stream,
        PaktReaderOptions? options = null,
        int bufferSize = 16384)
    {
        var pipeReader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: bufferSize,
            minimumReadSize: 1024,
            leaveOpen: false));
        return new PaktPipeSource(pipeReader, ownsPipeReader: true, options);
    }

    /// <summary>
    /// Create from in-memory data (fast path).
    /// <see cref="PipeReader.ReadAsync"/> returns the complete data with
    /// <c>IsCompleted = true</c> on the first call, so the refill loop never runs.
    /// </summary>
    internal static PaktPipeSource CreateFromMemory(
        ReadOnlyMemory<byte> data,
        PaktReaderOptions? options = null)
    {
        var pipeReader = PipeReader.Create(new ReadOnlySequence<byte>(data));
        return new PaktPipeSource(pipeReader, ownsPipeReader: true, options);
    }

    public bool IsFinalBlock => _isCompleted;
    public PaktReaderState ReaderState => _readerState;

    /// <summary>Perform the initial read from the pipe.</summary>
    public async ValueTask InitialReadAsync(CancellationToken ct = default)
    {
        var result = await _pipeReader.ReadAsync(ct).ConfigureAwait(false);
        _currentData = result.Buffer;
        _isCompleted = result.IsCompleted;
    }

    /// <summary>Create a <see cref="PaktReader"/> over the current buffer.</summary>
    public PaktReader CreateReader()
    {
        return new PaktReader(_currentData, _isCompleted, _readerState);
    }

    /// <summary>
    /// Advance the pipe past consumed bytes, save reader state, and read more data.
    /// </summary>
    /// <returns><c>true</c> if more data is available; <c>false</c> if the pipe is exhausted.</returns>
    public async ValueTask<bool> AdvanceAndRefillAsync(
        long bytesConsumed,
        PaktReaderState state,
        CancellationToken ct = default)
    {
        _readerState = state;

        var consumed = _currentData.GetPosition(bytesConsumed);
        var examined = _currentData.End;

        _pipeReader.AdvanceTo(consumed, examined);

        if (_isCompleted && bytesConsumed >= _currentData.Length)
            return false;

        var result = await _pipeReader.ReadAsync(ct).ConfigureAwait(false);
        _currentData = result.Buffer;
        _isCompleted = result.IsCompleted;

        return _currentData.Length > 0 || !_isCompleted;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsPipeReader)
            await _pipeReader.CompleteAsync().ConfigureAwait(false);
    }
}