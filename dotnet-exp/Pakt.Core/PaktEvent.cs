using System.Buffers;

namespace Pakt;

/// <summary>
/// <see cref="PaktReader"/> event.
/// </summary>
public readonly ref partial struct PaktEvent
{
    /// <summary>
    /// Gets the kind of event.
    /// </summary>
    public Kind EventKind { get; }

    /// <summary>
    /// Gets the offset of the payload.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// Gets the type reference of the payload.
    /// </summary>
    public PaktTypeRef Type { get; }

    /// <summary>
    /// Gets the payload data of the event.
    /// </summary>
    public ReadOnlySequence<byte> Payload { get; }
}