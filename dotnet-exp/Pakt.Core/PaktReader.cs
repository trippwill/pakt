using System.Buffers;
using System.IO.Pipelines;

namespace Pakt;

/// <summary>
/// Reader for Pakt data structures using System.IO.Pipelines.
/// </summary>
public sealed class PaktReader : IAsyncDisposable
{
    private readonly PipeReader _reader;
    private readonly PaktReaderOptions _options;

    /// <summary>
    /// Specifies the action to be taken by a read handler after processing an event.
    /// </summary>
    public enum HandlerResult
    {
        /// <summary>
        /// Continue reading the next event.
        /// </summary>
        Continue,

        /// <summary>
        /// Stop reading and return.
        /// </summary>
        Stop,
    }

    /// <summary>
    /// The delegate for processing Pakt events.
    /// </summary>
    /// <param name="evt">The Pakt event to process.</param>
    /// <returns>The action to take after processing the event.</returns>
    public delegate HandlerResult ReadHandler(scoped in PaktEvent evt);

    private PaktReader(PipeReader reader, PaktReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        _reader = reader;
        _options = options;
    }

    /// <summary>
    /// Creates a new instance of <see cref="PaktReader"/>.
    /// </summary>
    /// <param name="reader">The <see cref="PipeReader"/> to read from.</param>
    /// <param name="options">The reader options. If null, <see cref="PaktReaderOptions.Default"/> options are used.</param>
    /// <returns>A new <see cref="PaktReader"/> instance.</returns>
    public static PaktReader Create(PipeReader reader, PaktReaderOptions? options = null)
    {
        return new PaktReader(reader, options ?? PaktReaderOptions.Default);
    }

    /// <summary>
    /// Reads a single event asynchronously.
    /// </summary>
    /// <param name="handler">The handler to process the event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that returns true if an event was read, false otherwise.</returns>
    public async ValueTask<bool> ReadAsync(ReadHandler handler, CancellationToken cancellationToken = default)
    {
        ReadResult readResult = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return HandleRead(readResult.Buffer);

        bool HandleRead(scoped in ReadOnlySequence<byte> buffer)
        {
            HandlerResult handlerResult = handler(new PaktEvent(
                PaktEvent.Kind.UnitStart,
                offset: 0,
                default,
                payload: buffer
            ));

            return handlerResult == HandlerResult.Stop;
        }
    }

    /// <summary>
    /// Drains all events asynchronously.
    /// </summary>
    /// <param name="handler">The handler to process each event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async ValueTask DrainAsync(ReadHandler handler, CancellationToken cancellationToken = default)
    {
        while (await ReadAsync(handler, cancellationToken).ConfigureAwait(false))
        {
            // Continue reading until the handler signals to stop.
        }
    }

    /// <summary>
    /// Releases resources used by the reader.
    /// </summary>
    /// <returns>A task representing the asynchronous disposal operation.</returns>
    public async ValueTask DisposeAsync()
    {
    }
}