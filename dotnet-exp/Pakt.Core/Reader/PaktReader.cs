using System.Buffers;
using System.Runtime.CompilerServices;

namespace Pakt;

/// <summary>
/// High-performance, low-allocation reader for PAKT v0.1a encoded data.
/// Operates over <see cref="ReadOnlySequence{T}"/> for both memory and streaming scenarios.
/// <para>
/// Usage pattern:
/// <code>
/// var reader = new PaktReader(data, isFinalBlock: true, state: default);
/// while (reader.Read())
/// {
///     switch (reader.TokenType) { /* process tokens */ }
/// }
/// </code>
/// </para>
/// </summary>
public ref partial struct PaktReader
{
    // ── Input source ──
    private ReadOnlySpan<byte> _buffer;
    private readonly bool _isFinalBlock;
    private readonly ReadOnlySequence<byte> _sequence;
    private readonly bool _isMultiSegment;

    // ── Position tracking ──
    private int _consumed;
    private long _totalConsumed;
    private long _lineNumber;
    private long _bytePositionInLine;

    // ── Segment walking ──
    private bool _isLastSegment;
    private SequencePosition _currentPosition;
    private SequencePosition _nextPosition;

    // ── Parser state ──
    private PaktTokenType _tokenType;
    private PaktReaderPhase _phase;
    private ContainerStack _containerStack;
    private bool _isPack;
    private int _statementCount;
    private int _annotationNesting;

    // ── Output token (set by Read() in later phases) ──
    private ReadOnlySequence<byte> _valueSequence = default;
    private bool _valueIsEscaped = false;
    private long _tokenStartIndex = 0;

    // ── Options ──
    private readonly PaktReaderOptions _options;

    // ── Computed ──
    private readonly bool IsLastSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isFinalBlock && (!_isMultiSegment || _isLastSegment);
    }

    // ───────────────────── Constructors ─────────────────────

    /// <summary>
    /// Create a reader over a <see cref="ReadOnlySequence{T}"/> of UTF-8 PAKT data.
    /// </summary>
    public PaktReader(
        ReadOnlySequence<byte> paktData,
        bool isFinalBlock,
        PaktReaderState state = default)
    {
        _sequence = paktData;
        _isFinalBlock = isFinalBlock;
        _options = state._options ?? PaktReaderOptions.Default;

        // Restore state
        _lineNumber = state._lineNumber;
        _bytePositionInLine = state._bytePositionInLine;
        _phase = state._phase;
        _tokenType = state._tokenType;
        _containerStack = state._containerStack;
        _isPack = state._isPack;
        _statementCount = state._statementCount;
        _annotationNesting = state._annotationNesting;

        // Initialize segment walking
        ReadOnlyMemory<byte> first = paktData.First;
        _buffer = first.Span;
        _isMultiSegment = !paktData.IsSingleSegment;

        if (_isMultiSegment)
        {
            _currentPosition = paktData.Start;
            _nextPosition = _currentPosition;

            // Advance past the first segment
            if (paktData.TryGet(ref _nextPosition, out _, advance: true))
            {
                // _nextPosition now points to the second segment
            }

            _isLastSegment = false;
        }
        else
        {
            _currentPosition = default;
            _nextPosition = default;
            _isLastSegment = true;
        }

        _consumed = 0;
        _totalConsumed = 0;

        // BOM skip
        if (state._phase == PaktReaderPhase.Start
            && _buffer.Length >= 3
            && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
        {
            _consumed = 3;
            _bytePositionInLine += 3;
        }
    }

    /// <summary>
    /// Convenience constructor for complete in-memory data.
    /// </summary>
    public PaktReader(ReadOnlyMemory<byte> paktData, PaktReaderOptions? options = null)
        : this(new ReadOnlySequence<byte>(paktData), isFinalBlock: true,
            new PaktReaderState(0, 0, PaktReaderPhase.Start, PaktTokenType.None,
                default, false, 0, 0, options ?? PaktReaderOptions.Default))
    {
    }

    // ───────────────────── Public Properties ─────────────────────

    /// <summary>The type of the current token.</summary>
    public PaktTokenType TokenType => _tokenType;

    /// <summary>The raw bytes of the current token value.</summary>
    public ReadOnlySequence<byte> ValueSequence => _valueSequence;

    /// <summary>Whether the current string value contains escape sequences.</summary>
    public bool ValueIsEscaped => _valueIsEscaped;

    /// <summary>Current nesting depth.</summary>
    public int CurrentDepth => _containerStack.CurrentDepth;

    /// <summary>Total bytes consumed from the input.</summary>
    public long BytesConsumed => _totalConsumed + _consumed;

    /// <summary>Byte offset where the current token starts.</summary>
    public long TokenStartIndex => _tokenStartIndex;

    /// <summary>Whether this is the final block of input.</summary>
    public bool IsFinalBlock => _isFinalBlock;

    /// <summary>Current line number (1-based).</summary>
    public long LineNumber => _lineNumber;

    /// <summary>Byte position within the current line (0-based).</summary>
    public long BytePositionInLine => _bytePositionInLine;

    /// <summary>Capture the current state for cross-buffer resumption.</summary>
    public PaktReaderState CurrentState => new(
        _lineNumber,
        _bytePositionInLine,
        _phase,
        _tokenType,
        _containerStack,
        _isPack,
        _statementCount,
        _annotationNesting,
        _options);

    // ───────────────────── Read ─────────────────────
    // Implemented in PaktReader.Read.cs (partial)
}
