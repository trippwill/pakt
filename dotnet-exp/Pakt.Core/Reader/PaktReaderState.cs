namespace Pakt;

/// <summary>
/// Captures the parser state between buffer refills for <see cref="PaktSequenceReader"/>.
/// Pass to the next <see cref="PaktSequenceReader"/> constructor to resume parsing.
/// </summary>
public readonly struct PaktReaderState
{
    internal readonly long _lineNumber;
    internal readonly long _bytePositionInLine;
    internal readonly PaktReaderPhase _phase;
    internal readonly PaktTokenType _tokenType;
    internal readonly ContainerStack _containerStack;
    internal readonly bool _isStreaming;
    internal readonly int _statementCount;
    internal readonly int _annotationNesting;
    internal readonly PaktReaderOptions _options;

    internal PaktReaderState(
        long lineNumber,
        long bytePositionInLine,
        PaktReaderPhase phase,
        PaktTokenType tokenType,
        ContainerStack containerStack,
        bool isStreaming,
        int statementCount,
        int annotationNesting,
        PaktReaderOptions options)
    {
        _lineNumber = lineNumber;
        _bytePositionInLine = bytePositionInLine;
        _phase = phase;
        _tokenType = tokenType;
        _containerStack = containerStack;
        _isStreaming = isStreaming;
        _statementCount = statementCount;
        _annotationNesting = annotationNesting;
        _options = options;
    }

    /// <summary>Gets the reader options associated with this state.</summary>
    public PaktReaderOptions Options => _options;
}