using System.Buffers;

namespace Pakt;

/// <summary>
/// <see cref="PaktReader"/> event.
/// </summary>
public readonly ref partial struct PaktEvent
{
    internal PaktEvent(
        Kind eventKind,
        long offset,
        PaktTypeKind typeKind,
        ReadOnlySequence<byte> payload)
    {
        EventKind = eventKind;
        Offset = offset;
        TypeKind = typeKind;
        Payload = payload;
    }

    /// <summary>
    /// Gets the kind of event.
    /// </summary>
    public readonly Kind EventKind;

    /// <summary>
    /// Gets the offset of the event in the source.
    /// </summary>
    public readonly long Offset;

    /// <summary>
    /// Gets the type kind. Meaningful on type-related and value events.
    /// </summary>
    public readonly PaktTypeKind TypeKind;

    /// <summary>
    /// Gets the payload data of the event.
    /// </summary>
    public readonly ReadOnlySequence<byte> Payload;

    internal static PaktEvent UnitStart(long offset)
        => new(Kind.UnitStart, offset, default, default);

    internal static PaktEvent UnitEnd(long offset)
        => new(Kind.UnitEnd, offset, default, default);

    internal static PaktEvent StatementStart(long offset, ReadOnlySequence<byte> name)
        => new(Kind.StatementStart, offset, default, name);

    internal static PaktEvent AssignStart(long offset)
        => new(Kind.AssignStart, offset, default, default);

    internal static PaktEvent PackStart(long offset)
        => new(Kind.PackStart, offset, default, default);

    internal static PaktEvent PackEnd(long offset)
        => new(Kind.PackEnd, offset, default, default);

    internal static PaktEvent AssignEnd(long offset)
        => new(Kind.AssignEnd, offset, default, default);
}