using System.Buffers;
using System.Runtime.CompilerServices;

namespace Pakt;

/// <summary>
/// A validating reader that wraps <see cref="PaktReader"/> and enforces
/// type annotations. Each value token is checked against the declared type.
/// </summary>
public ref struct PaktValidatingReader
{
    private PaktReader _inner;

    // Type tree from the current statement's annotation
    private ValidationNode[] _typeNodes;
    private ByteRange[] _members;
    private int[] _childIndices;
    private byte[] _annotationBytes;
    private int _rootNodeIndex;

    // Validation state
    private ValidatorPhase _phase;
    private int _expectedNodeIndex; // which node describes the next expected value
    private ValidationFrameStack _frames;

    /// <summary>
    /// Create a validating reader over a <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    public PaktValidatingReader(
        ReadOnlySequence<byte> paktData,
        bool isFinalBlock,
        PaktValidatingReaderState state = default)
    {
        _inner = new PaktReader(paktData, isFinalBlock, state.InnerState);
        _phase = state.Phase;
        _typeNodes = [];
        _members = [];
        _childIndices = [];
        _annotationBytes = [];
        _rootNodeIndex = -1;
        _expectedNodeIndex = -1;
        _frames = default;

        // Restore from state if resuming mid-statement
        if (state.AnnotationBytes is { Length: > 0 })
        {
            _annotationBytes = state.AnnotationBytes;
            _rootNodeIndex = ValidationTypeParser.Parse(
                _annotationBytes, _inner.CurrentState.Options.MaxNestingDepth,
                out _typeNodes, out _members, out _childIndices);
            _expectedNodeIndex = state.RootNodeIndex;

            if (state.Frames is { Length: > 0 })
                _frames.RestoreFrom(state.Frames, state.FrameCount);
        }
    }

    /// <summary>
    /// Convenience constructor for complete in-memory data.
    /// </summary>
    public PaktValidatingReader(ReadOnlyMemory<byte> paktData, PaktReaderOptions? options = null)
        : this(new ReadOnlySequence<byte>(paktData), isFinalBlock: true,
            new PaktValidatingReaderState(
                new PaktReaderState(0, 0, PaktReaderPhase.Start, PaktTokenType.None,
                    default, false, 0, 0, options ?? PaktReaderOptions.Default),
                null, -1, ValidatorPhase.NoStatement, null, 0))
    {
    }

    // ── Public Properties (delegate to inner reader) ──

    /// <summary>The type of the current token.</summary>
    public PaktTokenType TokenType => _inner.TokenType;

    /// <summary>The raw bytes of the current token value.</summary>
    public ReadOnlySequence<byte> ValueSequence => _inner.ValueSequence;

    /// <summary>Whether the current string value contains escape sequences.</summary>
    public bool ValueIsEscaped => _inner.ValueIsEscaped;

    /// <summary>Current nesting depth.</summary>
    public int CurrentDepth => _inner.CurrentDepth;

    /// <summary>Total bytes consumed from the input.</summary>
    public long BytesConsumed => _inner.BytesConsumed;

    /// <summary>Byte offset where the current token starts.</summary>
    public long TokenStartIndex => _inner.TokenStartIndex;

    /// <summary>Whether this is the final block of input.</summary>
    public bool IsFinalBlock => _inner.IsFinalBlock;

    /// <summary>Current line number (1-based).</summary>
    public long LineNumber => _inner.LineNumber;

    /// <summary>Byte position within the current line (0-based).</summary>
    public long BytePositionInLine => _inner.BytePositionInLine;

    /// <summary>Whether the current collection was opened with streaming prefix (~[ or ~&lt;).</summary>
    public bool IsStreaming => _inner.IsStreaming;

    /// <summary>Capture the current state for cross-buffer resumption.</summary>
    public PaktValidatingReaderState CurrentState => new(
        _inner.CurrentState,
        _annotationBytes.Length > 0 ? _annotationBytes : null,
        _expectedNodeIndex,
        _phase,
        _frames.ToSnapshots(),
        _frames.Depth);

    // ── Typed Value Accessors (delegate) ──

    public string GetString() => _inner.GetString();
    public int GetInt32() => _inner.GetInt32();
    public long GetInt64() => _inner.GetInt64();
    public bool TryGetInt32(out int value) => _inner.TryGetInt32(out value);
    public bool TryGetInt64(out long value) => _inner.TryGetInt64(out value);
    public double GetDouble() => _inner.GetDouble();
    public bool TryGetDouble(out double value) => _inner.TryGetDouble(out value);
    public float GetFloat() => _inner.GetFloat();
    public decimal GetDecimal() => _inner.GetDecimal();
    public bool TryGetDecimal(out decimal value) => _inner.TryGetDecimal(out value);
    public bool GetBool() => _inner.GetBool();
    public Guid GetGuid() => _inner.GetGuid();
    public DateOnly GetDate() => _inner.GetDate();
    public DateTimeOffset GetTimestamp() => _inner.GetTimestamp();
    public byte[] GetBytes() => _inner.GetBytes();
    public string GetAtom() => _inner.GetAtom();

    // ── Read ──

    /// <summary>
    /// Advance to the next token, validating against the declared type annotation.
    /// </summary>
    public bool Read()
    {
        if (!_inner.Read())
            return false;

        PaktTokenType token = _inner.TokenType;

        switch (token)
        {
            case PaktTokenType.StatementName:
                ResetStatement();
                _phase = ValidatorPhase.NoStatement;
                return true;

            case PaktTokenType.TypeAnnotationStart:
                ParseAnnotation();
                _phase = ValidatorPhase.ExpectOperator;
                return true;

            case PaktTokenType.AssignOperator:
                _expectedNodeIndex = _rootNodeIndex;
                _phase = ValidatorPhase.AssignExpectValue;
                return true;

            case PaktTokenType.EndOfUnit:
                _phase = ValidatorPhase.Done;
                return true;

            // Structural tokens
            case PaktTokenType.StructStart:
            case PaktTokenType.TupleStart:
            case PaktTokenType.ListStart:
            case PaktTokenType.MapStart:
                ValidateCompositeOpen(token);
                return true;

            case PaktTokenType.StructEnd:
            case PaktTokenType.TupleEnd:
            case PaktTokenType.ListEnd:
            case PaktTokenType.MapEnd:
                ValidateCompositeClose(token);
                return true;

            case PaktTokenType.MapEntryBind:
                ValidateMapBind();
                return true;

            // Value tokens
            default:
                ValidateValueToken(token);
                return true;
        }
    }

    // ── Statement Setup ──

    private void ResetStatement()
    {
        _typeNodes = [];
        _members = [];
        _childIndices = [];
        _annotationBytes = [];
        _rootNodeIndex = -1;
        _expectedNodeIndex = -1;
        _frames.Clear();
    }

    private void ParseAnnotation()
    {
        ReadOnlySequence<byte> seq = _inner.ValueSequence;
        int len = checked((int)seq.Length);
        _annotationBytes = new byte[len];
        seq.CopyTo(_annotationBytes);

        _rootNodeIndex = ValidationTypeParser.Parse(
            _annotationBytes, _inner.CurrentState.Options.MaxNestingDepth,
            out _typeNodes, out _members, out _childIndices);
    }

    // ── Value Validation ──

    private void ValidateValueToken(PaktTokenType token)
    {
        if (_expectedNodeIndex < 0)
        {
            // No expected type — could be too many values in struct/tuple
            CheckArityOverflow();
            return;
        }

        ref readonly ValidationNode expected = ref _typeNodes[_expectedNodeIndex];

        // Handle nil
        if (token == PaktTokenType.Nil)
        {
            if (!expected.IsNullable)
                ThrowNilNonNullable();

            CompleteValue();
            return;
        }

        // Validate based on expected kind
        switch (expected.Kind)
        {
            case ValidationNodeKind.Scalar:
                if (token != expected.ExpectedToken)
                    ThrowTypeMismatch(expected, token);
                CompleteValue();
                break;

            case ValidationNodeKind.AtomSet:
                if (token != PaktTokenType.Atom)
                    ThrowTypeMismatch(expected, token);
                ValidateAtomMember(_expectedNodeIndex);
                CompleteValue();
                break;

            default:
                // Composite types should have been handled by ValidateCompositeOpen
                ThrowTypeMismatch(expected, token);
                break;
        }
    }

    private void ValidateAtomMember(int atomNodeIndex)
    {
        ref readonly ValidationNode atomNode = ref _typeNodes[atomNodeIndex];
        ReadOnlySequence<byte> valueSeq = _inner.ValueSequence;

        // Atom value includes leading |, member names in annotation don't
        // Copy to a heap buffer to avoid stackalloc scope issues
        byte[] valueBuf;
        int valueLen;
        if (valueSeq.IsSingleSegment)
        {
            valueBuf = [];
            valueLen = 0;
            ReadOnlySpan<byte> span = valueSeq.FirstSpan;
            // Strip leading |
            if (span.Length > 0 && span[0] == PaktConstants.Pipe)
                span = span[1..];

            ReadOnlySpan<byte> annoBytes = _annotationBytes;
            for (int i = atomNode.MemberStart; i < atomNode.MemberStart + atomNode.MemberCount; i++)
            {
                if (_members[i].Slice(annoBytes).SequenceEqual(span))
                    return;
            }
        }
        else
        {
            valueLen = checked((int)valueSeq.Length);
            valueBuf = new byte[valueLen];
            valueSeq.CopyTo(valueBuf);

            ReadOnlySpan<byte> atomValue = valueBuf.AsSpan(0, valueLen);
            if (atomValue.Length > 0 && atomValue[0] == PaktConstants.Pipe)
                atomValue = atomValue[1..];

            ReadOnlySpan<byte> annoBytes = _annotationBytes;
            for (int i = atomNode.MemberStart; i < atomNode.MemberStart + atomNode.MemberCount; i++)
            {
                if (_members[i].Slice(annoBytes).SequenceEqual(atomValue))
                    return;
            }
        }

        ThrowTypeMismatch("Atom value is not a member of the declared set");
    }

    // ── Composite Validation ──

    private void ValidateCompositeOpen(PaktTokenType token)
    {
        if (_expectedNodeIndex < 0)
        {
            CheckArityOverflow();
            return;
        }

        ref readonly ValidationNode expected = ref _typeNodes[_expectedNodeIndex];

        // Check nullable nil was already handled in ValidateValueToken

        // Verify the opening delimiter matches the expected composite kind
        PaktTokenType expectedOpen = expected.Kind switch
        {
            ValidationNodeKind.Struct => PaktTokenType.StructStart,
            ValidationNodeKind.Tuple => PaktTokenType.TupleStart,
            ValidationNodeKind.List => PaktTokenType.ListStart,
            ValidationNodeKind.Map => PaktTokenType.MapStart,
            _ => PaktTokenType.None,
        };

        if (expectedOpen == PaktTokenType.None || token != expectedOpen)
            ThrowTypeMismatch(expected, token);

        // Push a frame for this composite
        var frame = new ValidationFrame
        {
            TypeNodeIndex = _expectedNodeIndex,
            ChildIndex = 0,
            Phase = expected.Kind == ValidationNodeKind.Map
                ? MapPhase.ExpectKeyOrClose
                : default,
        };
        _frames.Push(frame);

        // Set expected to first child (if any)
        SetExpectedForCompositeChild(_expectedNodeIndex, childIndex: 0);
    }

    private void ValidateCompositeClose(PaktTokenType token)
    {
        if (_frames.Depth == 0)
        {
            // Closing at root level — handled by inner reader
            CompleteValue();
            return;
        }

        ValidationFrame frame = _frames.Pop();
        ref readonly ValidationNode compositeNode = ref _typeNodes[frame.TypeNodeIndex];

        // Arity check for struct and tuple
        if (compositeNode.Kind is ValidationNodeKind.Struct or ValidationNodeKind.Tuple)
        {
            if (frame.ChildIndex != compositeNode.ChildCount)
            {
                ThrowArityMismatch(compositeNode.ChildCount, frame.ChildIndex);
            }
        }

        // For maps: close is only valid in ExpectKeyOrClose
        if (compositeNode.Kind == ValidationNodeKind.Map
            && frame.Phase != MapPhase.ExpectKeyOrClose)
        {
            ThrowValidation("Map closed in unexpected state — missing value after '=>'");
        }

        CompleteValue();
    }

    private void ValidateMapBind()
    {
        if (_frames.Depth == 0)
            ThrowValidation("'=' bind outside of map context");

        ref ValidationFrame frame = ref _frames.Peek();
        ref readonly ValidationNode mapNode = ref _typeNodes[frame.TypeNodeIndex];

        if (mapNode.Kind != ValidationNodeKind.Map)
            ThrowValidation("'=' bind inside non-map composite");

        if (frame.Phase != MapPhase.ExpectBind)
            ThrowValidation("Unexpected '=' bind — expected key or close");

        // After =>, expect the value type
        _expectedNodeIndex = _childIndices[mapNode.ChildStart + 1];
        frame.Phase = MapPhase.ExpectValue;
    }

    // ── Value Completion ──

    /// <summary>
    /// Called after a complete value (scalar or composite close) to advance
    /// the parent's state: struct/tuple index, list count, map phase, or pack phase.
    /// </summary>
    private void CompleteValue()
    {
        // Pack-level completion (no frame on stack)
        if (_frames.Depth == 0)
        {
            switch (_phase)
            {
                case ValidatorPhase.AssignExpectValue:
                    // Assign value complete — next token should be a new statement
                    _phase = ValidatorPhase.NoStatement;
                    return;

                default:
                    return;
            }
        }

        // Inside a composite frame
        ref ValidationFrame frame = ref _frames.Peek();
        ref readonly ValidationNode parentNode = ref _typeNodes[frame.TypeNodeIndex];

        switch (parentNode.Kind)
        {
            case ValidationNodeKind.Struct:
            case ValidationNodeKind.Tuple:
                frame.ChildIndex++;
                // Set expected to next child type, or mark as expecting close
                if (frame.ChildIndex < parentNode.ChildCount)
                {
                    _expectedNodeIndex = _childIndices[parentNode.ChildStart + frame.ChildIndex];
                }
                else
                {
                    // All children consumed — next must be close delimiter
                    _expectedNodeIndex = -1;
                }
                break;

            case ValidationNodeKind.List:
                frame.ChildIndex++;
                // Expected stays the same (element type) — list is open-ended
                _expectedNodeIndex = _childIndices[parentNode.ChildStart];
                break;

            case ValidationNodeKind.Map:
                if (frame.Phase == MapPhase.ExpectBind)
                {
                    // A key was just consumed (from ExpectKeyOrClose → key token → CompleteValue)
                    // This shouldn't happen — ExpectBind is set by us below
                    ThrowValidation("Internal: value completed in ExpectBind phase");
                }

                if (frame.Phase == MapPhase.ExpectKeyOrClose)
                {
                    // A key was just consumed — expect => next
                    frame.Phase = MapPhase.ExpectBind;
                    _expectedNodeIndex = -1; // next should be => not a value
                }
                else if (frame.Phase == MapPhase.ExpectValue)
                {
                    // A value was just consumed after => — expect next key or close
                    frame.ChildIndex++; // count completed entries
                    frame.Phase = MapPhase.ExpectKeyOrClose;
                    _expectedNodeIndex = _childIndices[parentNode.ChildStart]; // key type
                }
                break;
        }
    }

    private void SetExpectedForCompositeChild(int compositeNodeIndex, int childIndex)
    {
        ref readonly ValidationNode composite = ref _typeNodes[compositeNodeIndex];
        switch (composite.Kind)
        {
            case ValidationNodeKind.Struct:
            case ValidationNodeKind.Tuple:
                if (childIndex < composite.ChildCount)
                    _expectedNodeIndex = _childIndices[composite.ChildStart + childIndex];
                else
                    _expectedNodeIndex = -1;
                break;

            case ValidationNodeKind.List:
                _expectedNodeIndex = _childIndices[composite.ChildStart]; // always element type
                break;

            case ValidationNodeKind.Map:
                _expectedNodeIndex = _childIndices[composite.ChildStart]; // key type initially
                break;
        }
    }

    // ── Error Helpers ──

    /// <summary>
    /// Check if we're inside a struct/tuple that has already consumed all its children.
    /// If so, the extra value is an arity error.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CheckArityOverflow()
    {
        if (_frames.Depth > 0)
        {
            ref readonly ValidationFrame frame = ref _frames.Peek();
            ref readonly ValidationNode parentNode = ref _typeNodes[frame.TypeNodeIndex];
            if (parentNode.Kind is ValidationNodeKind.Struct or ValidationNodeKind.Tuple
                && frame.ChildIndex >= parentNode.ChildCount)
            {
                ThrowArityMismatch(parentNode.ChildCount, frame.ChildIndex + 1);
            }
        }
    }

    private SourcePosition CurrentPosition =>
        new(_inner.TokenStartIndex, (int)_inner.LineNumber, _inner.BytePositionInLine);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowTypeMismatch(in ValidationNode expected, PaktTokenType actual) =>
        throw PaktParseError.TypeMismatch(CurrentPosition,
            $"Expected {DescribeNode(expected)}, got {actual}").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowTypeMismatch(string message) =>
        throw PaktParseError.TypeMismatch(CurrentPosition, message).ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowNilNonNullable() =>
        throw PaktParseError.NilNonNullable(CurrentPosition,
            "nil is not valid for non-nullable type").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowArityMismatch(int expected, int actual) =>
        throw PaktParseError.ArityMismatch(CurrentPosition,
            $"Expected {expected} values, got {actual}").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowValidation(string message) =>
        throw PaktParseError.Syntax(CurrentPosition, message).ToException();

    private static string DescribeNode(in ValidationNode node) => node.Kind switch
    {
        ValidationNodeKind.Scalar => node.ExpectedToken.ToString(),
        ValidationNodeKind.AtomSet => "atom set",
        ValidationNodeKind.Struct => "struct",
        ValidationNodeKind.Tuple => "tuple",
        ValidationNodeKind.List => "list",
        ValidationNodeKind.Map => "map",
        _ => "unknown",
    };
}