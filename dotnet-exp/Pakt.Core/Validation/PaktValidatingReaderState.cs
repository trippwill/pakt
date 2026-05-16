namespace Pakt;

/// <summary>
/// Validation phase tracking for <see cref="PaktValidatingReader"/>.
/// </summary>
internal enum ValidatorPhase : byte
{
    /// <summary>No statement in progress. Expect StatementName or EndOfUnit.</summary>
    NoStatement,

    /// <summary>Have annotation, expect operator (=).</summary>
    ExpectOperator,

    /// <summary>Assign mode: expect a single root value.</summary>
    AssignExpectValue,

    /// <summary>Inside a composite value (driven by frame stack).</summary>
    InComposite,

    /// <summary>Parsing complete.</summary>
    Done,
}

/// <summary>
/// Captures the validating reader state for cross-buffer resumption.
/// </summary>
public readonly struct PaktValidatingReaderState
{
    internal readonly PaktReaderState InnerState;
    internal readonly byte[]? AnnotationBytes;
    internal readonly int RootNodeIndex;
    internal readonly ValidatorPhase Phase;
    internal readonly ValidationFrameSnapshot[]? Frames;
    internal readonly int FrameCount;

    internal PaktValidatingReaderState(
        PaktReaderState innerState,
        byte[]? annotationBytes,
        int rootNodeIndex,
        ValidatorPhase phase,
        ValidationFrameSnapshot[]? frames,
        int frameCount)
    {
        InnerState = innerState;
        AnnotationBytes = annotationBytes;
        RootNodeIndex = rootNodeIndex;
        Phase = phase;
        Frames = frames;
        FrameCount = frameCount;
    }
}