using System.Buffers;
using System.IO.Pipelines;

namespace Pakt;

/// <summary>
/// Async pull reader for PAKT units using System.IO.Pipelines.
/// <para>
/// Events are delivered via <see cref="ReadHandler"/> with <c>scoped in PaktEvent</c>.
/// The event (including its <see cref="PaktEvent.Payload"/>) is valid only during
/// the handler invocation — callers must copy any data they need to retain.
/// </para>
/// </summary>
public sealed class PaktReader
{
    private readonly PipeReader _pipeReader;
    private readonly Parser _parser;

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

    private PaktReader(PipeReader pipeReader, PaktReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipeReader);
        ArgumentNullException.ThrowIfNull(options);

        _pipeReader = pipeReader;
        _parser = new Parser(options);
    }

    /// <summary>
    /// Creates a new instance of <see cref="PaktReader"/>.
    /// </summary>
    /// <param name="pipeReader">The <see cref="PipeReader"/> to read from.</param>
    /// <param name="options">The reader options. If null, <see cref="PaktReaderOptions.Default"/> options are used.</param>
    /// <returns>A new <see cref="PaktReader"/> instance.</returns>
    public static PaktReader Create(PipeReader pipeReader, PaktReaderOptions? options = null)
    {
        return new PaktReader(pipeReader, options ?? PaktReaderOptions.Default);
    }

    /// <summary>
    /// Reads the next event and delivers it to the handler.
    /// Returns <langword>true</langword> if an event was read and the handler returned <see cref="HandlerResult.Continue"/>.
    /// Returns <langword>false</langword> if parsing is complete or the handler returned <see cref="HandlerResult.Stop"/>.
    /// </summary>
    public async ValueTask<bool> ReadAsync(ReadHandler handler, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult readResult = await _pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = readResult.Buffer;
            var reader = new SequenceReader<byte>(buffer);
            bool isFinal = readResult.IsCompleted;

            (bool handled, bool shouldContinue) = StepUntilEvent(ref reader, isFinal, handler);
            if (handled)
            {
                _pipeReader.AdvanceTo(reader.Position);
                return shouldContinue;
            }

            // MoreData — tell PipeReader what we consumed vs examined
            _pipeReader.AdvanceTo(reader.Position, buffer.End);

            if (isFinal)
                throw PaktParseError.UnexpectedEndOfInput(_parser.CurrentPosition).ToException();
        }
    }

    /// <summary>
    /// Reads all events, calling the handler for each, until parsing completes
    /// or the handler returns <see cref="HandlerResult.Stop"/>.
    /// </summary>
    public async ValueTask DrainAsync(ReadHandler handler, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ReadResult readResult = await _pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = readResult.Buffer;
            var reader = new SequenceReader<byte>(buffer);
            bool isFinal = readResult.IsCompleted;

            if (!DrainBuffer(ref reader, isFinal, handler))
            {
                _pipeReader.AdvanceTo(reader.Position);
                return;
            }

            // MoreData — tell PipeReader what we consumed vs examined
            _pipeReader.AdvanceTo(reader.Position, buffer.End);

            if (isFinal)
                throw PaktParseError.UnexpectedEndOfInput(_parser.CurrentPosition).ToException();
        }
    }

    /// <summary>
    /// Steps the parser until one event is emitted, MoreData is needed, or parsing completes.
    /// Returns (handled: true, wantMore) when an event was emitted or parsing completed.
    /// Returns (handled: false, _) when MoreData is needed.
    /// </summary>
    private (bool Handled, bool ShouldContinue) StepUntilEvent(
        ref SequenceReader<byte> reader, bool isFinal, ReadHandler handler)
    {
        while (true)
        {
            Parser.StepResult step = _parser.Step(ref reader, isFinal);
            switch (step.Status)
            {
                case Parser.StepStatus.Continue:
                    continue;

                case Parser.StepStatus.Event:
                    bool shouldContinue = handler(step.PaktEvent) == HandlerResult.Continue;
                    return (Handled: true, ShouldContinue: shouldContinue);

                case Parser.StepStatus.MoreData:
                    return (Handled: false, ShouldContinue: false);

                case Parser.StepStatus.Complete:
                    return (Handled: true, ShouldContinue: false);

                case Parser.StepStatus.Error:
                    throw step.ParseError!.Value.ToException();

                default:
                    throw new InvalidOperationException($"Unexpected step status: {step.Status}");
            }
        }
    }

    /// <summary>
    /// Drains all events from the current buffer, calling the handler for each.
    /// Returns <langword>true</langword> if MoreData is needed (caller should read more from PipeReader).
    /// Returns <langword>false</langword> if parsing completed or handler returned Stop.
    /// </summary>
    private bool DrainBuffer(
        ref SequenceReader<byte> reader,
        bool isFinal,
        ReadHandler handler)
    {
        while (true)
        {
            Parser.StepResult step = _parser.Step(ref reader, isFinal);
            switch (step.Status)
            {
                case Parser.StepStatus.Continue:
                    continue;

                case Parser.StepStatus.Event:
                    if (handler(step.PaktEvent) == HandlerResult.Stop)
                        return false;
                    continue;

                case Parser.StepStatus.MoreData:
                    return true;

                case Parser.StepStatus.Complete:
                    return false;

                case Parser.StepStatus.Error:
                    throw step.ParseError!.Value.ToException();

                default:
                    throw new InvalidOperationException($"Unexpected step status: {step.Status}");
            }
        }
    }
}