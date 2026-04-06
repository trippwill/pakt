using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pakt;

/// <summary>
/// A high-performance, forward-only reader for PAKT format data operating on UTF-8 encoded bytes.
/// Modeled after <see cref="System.Text.Json.Utf8JsonReader"/> but adapted for PAKT grammar.
/// </summary>
public ref partial struct PaktReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _consumed;
    private int _line;
    private int _bytePositionInLine;

    private PaktTokenType _tokenType;
    private PaktScalarType _scalarType;
    private int _valueStart;
    private int _valueLength;

    private ParserState _state;
    private Frame[] _stack;
    private int _stackDepth;

    private PaktType? _currentType;
    private string? _currentName;
    private string? _statementName;
    private PaktType? _statementType;
    private bool _isStreamStatement;
    private bool _isNullValue;

    private readonly PaktReaderOptions _options;
    private bool _disposed;

    // Decoded value buffer (for strings, bin, atoms that need processing)
    private byte[]? _decodedBuffer;
    private int _decodedLength;
    private bool _usingDecodedBuffer;

    /// <summary>
    /// Initializes a new <see cref="PaktReader"/> over the provided UTF-8 PAKT data.
    /// </summary>
    public PaktReader(ReadOnlySpan<byte> data, PaktReaderOptions options = default)
    {
        _buffer = data;
        _consumed = 0;
        _line = 1;
        _bytePositionInLine = 0;
        _tokenType = PaktTokenType.None;
        _scalarType = PaktScalarType.None;
        _valueStart = 0;
        _valueLength = 0;
        _state = ParserState.Top;
        _stack = ArrayPool<Frame>.Shared.Rent(options.MaxDepth > 0 ? options.MaxDepth : 64);
        _stackDepth = 0;
        _currentType = null;
        _currentName = null;
        _statementName = null;
        _statementType = null;
        _isStreamStatement = false;
        _isNullValue = false;
        _options = options.MaxDepth > 0 ? options : PaktReaderOptions.Default;
        _disposed = false;
        SkipBOM();
    }

    /// <summary>The type of the last token read.</summary>
    public readonly PaktTokenType TokenType => _tokenType;

    /// <summary>The scalar type of the current value, if <see cref="TokenType"/> is <see cref="PaktTokenType.ScalarValue"/>.</summary>
    public readonly PaktScalarType ScalarType => _scalarType;

    /// <summary>The current position in the source.</summary>
    public readonly PaktPosition Position => new(_line, _bytePositionInLine + 1);

    /// <summary>The raw UTF-8 bytes of the current scalar value.</summary>
    public readonly ReadOnlySpan<byte> ValueSpan =>
        _valueStart == -1 && _usingDecodedBuffer && _decodedBuffer is not null
            ? new ReadOnlySpan<byte>(_decodedBuffer, 0, _decodedLength)
            : _buffer.Slice(_valueStart, _valueLength);

    /// <summary>The name of the current top-level statement.</summary>
    public readonly string? StatementName => _statementName;

    /// <summary>The type of the current top-level statement.</summary>
    public readonly PaktType? StatementType => _statementType;

    /// <summary>Whether the current statement uses stream syntax (<c>&lt;&lt;</c>).</summary>
    public readonly bool IsStreamStatement => _isStreamStatement;

    /// <summary>The name of the current field or element (may be a field name, index like "[0]", or statement name).</summary>
    public readonly string? CurrentName => _currentName;

    /// <summary>The declared type of the current value being parsed.</summary>
    public readonly PaktType? CurrentType => _currentType;

    /// <summary>Whether the current value is nil.</summary>
    public readonly bool IsNullValue => _isNullValue;

    /// <summary>The total number of bytes consumed so far from the input buffer.</summary>
    public readonly int BytesConsumed => _consumed;

    /// <summary>
    /// Reads the next token from the PAKT data.
    /// </summary>
    /// <returns><c>true</c> if a token was read; <c>false</c> if the end of the data has been reached.</returns>
    public bool Read()
    {
        if (_disposed)
            ThrowObjectDisposed();

        _usingDecodedBuffer = false;

        while (true)
        {
            var result = Step();
            switch (result)
            {
                case StepResult.Emit:
                    return true;
                case StepResult.Continue:
                    continue;
                case StepResult.Eof:
                    return false;
            }
        }
    }

    /// <summary>
    /// Releases pooled resources. Must be called when done with the reader.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_stack != null)
            {
                ArrayPool<Frame>.Shared.Return(_stack);
                _stack = null!;
            }
            if (_decodedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_decodedBuffer);
                _decodedBuffer = null;
            }
            _disposed = true;
        }
    }

    // -----------------------------------------------------------------------
    // State machine
    // -----------------------------------------------------------------------

    private enum ParserState
    {
        Top,
        AssignStart,
        StreamStart,
        Value,
        StructOpen,
        StructField,
        StructSep,
        StructClose,
        TupleOpen,
        TupleElem,
        TupleSep,
        TupleClose,
        ListOpen,
        ListElem,
        ListSep,
        ListClose,
        MapOpen,
        MapKey,
        MapAfterKey,
        MapAssign,
        MapEntry,
        MapClose,
        StreamListItem,
        StreamListSep,
        StreamMapKey,
        StreamMapAfterKey,
        StreamMapSep,
        AssignEnd,
        StreamEnd,
    }

    private enum FrameKind
    {
        Assign,
        Stream,
        Struct,
        Tuple,
        List,
        Map,
    }

    private struct Frame
    {
        public FrameKind Kind;
        public ParserState Resume;
        public ParserState ChildResume;
        public string? Name;
        public PaktPosition Pos;

        // Struct
        public ImmutableArray<PaktField> StructFields;
        public int FieldIdx;

        // Tuple
        public ImmutableArray<PaktType> TupleElements;
        public int ElemIdx;

        // List
        public PaktType? ListElement;

        // Map
        public PaktType? MapKey;
        public PaktType? MapValue;
        public string? KeyStr;
    }

    private enum StepResult
    {
        Continue,
        Emit,
        Eof,
    }

    // Pending value type/name used by stateValue
    private PaktType? _valType;
    private string? _valName;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ref Frame Current() => ref _stack[_stackDepth - 1];

    private void Push(Frame frame)
    {
        if (_stackDepth >= _options.MaxDepth)
            ThrowError("Maximum nesting depth exceeded", PaktErrorCode.Syntax);
        _stack[_stackDepth++] = frame;
    }

    private Frame Pop()
    {
        return _stack[--_stackDepth];
    }

    private ParserState CurrentChildResume()
    {
        if (_stackDepth == 0)
            return ParserState.Top;
        return _stack[_stackDepth - 1].ChildResume;
    }

    private StepResult Step()
    {
        switch (_state)
        {
            case ParserState.Top:
                return StepTop();
            case ParserState.AssignStart:
                return StepAssignStart();
            case ParserState.StreamStart:
                return StepStreamStart();
            case ParserState.Value:
                return StepValue();
            case ParserState.StructOpen:
                return StepStructOpen();
            case ParserState.StructField:
                return StepStructField();
            case ParserState.StructSep:
                return StepStructSep();
            case ParserState.StructClose:
                return StepStructClose();
            case ParserState.TupleOpen:
                return StepTupleOpen();
            case ParserState.TupleElem:
                return StepTupleElem();
            case ParserState.TupleSep:
                return StepTupleSep();
            case ParserState.TupleClose:
                return StepTupleClose();
            case ParserState.ListOpen:
                return StepListOpen();
            case ParserState.ListElem:
                return StepListElem();
            case ParserState.ListSep:
                return StepListSep();
            case ParserState.ListClose:
                return StepListClose();
            case ParserState.MapOpen:
                return StepMapOpen();
            case ParserState.MapKey:
                return StepMapKey();
            case ParserState.MapAfterKey:
                _state = ParserState.MapAssign;
                return StepResult.Continue;
            case ParserState.MapAssign:
                return StepMapAssign();
            case ParserState.MapEntry:
                return StepMapEntry();
            case ParserState.MapClose:
                return StepMapClose();
            case ParserState.StreamListItem:
                return StepStreamListItem();
            case ParserState.StreamListSep:
                return StepStreamListSep();
            case ParserState.StreamMapKey:
                return StepStreamMapKey();
            case ParserState.StreamMapAfterKey:
                return StepStreamMapAfterKey();
            case ParserState.StreamMapSep:
                return StepStreamMapSep();
            case ParserState.AssignEnd:
                return StepAssignEnd();
            case ParserState.StreamEnd:
                return StepStreamEnd();
            default:
                ThrowError($"Unknown parser state: {_state}", PaktErrorCode.Syntax);
                return StepResult.Eof; // unreachable
        }
    }

    private StepResult StepTop()
    {
        SkipInsignificant(skipNewlines: true);
        if (_consumed >= _buffer.Length)
            return StepResult.Eof;

        // Read statement header: IDENT:type = value  or  IDENT:type << value, ...
        var identPos = Position;
        var name = ReadIdent();
        var type = ReadTypeAnnotation();

        SkipWS();
        if (_consumed >= _buffer.Length)
            ThrowError("Expected '=' or '<<' after statement header", PaktErrorCode.UnexpectedEof);

        bool isStream = false;
        byte b = _buffer[_consumed];
        if (b == '=')
        {
            _consumed++;
            _bytePositionInLine++;
        }
        else if (b == '<' && _consumed + 1 < _buffer.Length && _buffer[_consumed + 1] == '<')
        {
            _consumed += 2;
            _bytePositionInLine += 2;
            isStream = true;
            if (!type.IsList && !type.IsMap)
                ThrowError($"Stream type must be list or map, got {type}", PaktErrorCode.Syntax);
        }
        else
        {
            ThrowError("Expected '=' or '<<' after statement header", PaktErrorCode.Syntax);
        }

        SkipWS();

        _statementName = name;
        _statementType = type;
        _isStreamStatement = isStream;

        if (isStream)
        {
            var fr = new Frame
            {
                Kind = FrameKind.Stream,
                Resume = ParserState.Top,
                Name = name,
                Pos = identPos,
            };
            if (type.IsList)
                fr.ListElement = type.ListElement;
            else
            {
                fr.MapKey = type.MapKey;
                fr.MapValue = type.MapValue;
            }
            Push(fr);
            _state = ParserState.StreamStart;
        }
        else
        {
            Push(new Frame
            {
                Kind = FrameKind.Assign,
                Resume = ParserState.Top,
                ChildResume = ParserState.AssignEnd,
                Name = name,
                Pos = identPos,
            });
            _valType = type;
            _valName = name;
            _state = ParserState.AssignStart;
        }

        return StepResult.Continue;
    }

    private StepResult StepAssignStart()
    {
        ref var fr = ref Current();
        _state = ParserState.Value;
        _tokenType = PaktTokenType.AssignStart;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = _statementType;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepStreamStart()
    {
        ref var fr = ref Current();
        PaktTokenType kind;
        if (fr.ListElement is not null)
        {
            fr.ElemIdx = 0;
            _state = ParserState.StreamListItem;
            kind = PaktTokenType.StreamStart;
        }
        else
        {
            fr.KeyStr = "";
            _state = ParserState.StreamMapKey;
            kind = PaktTokenType.StreamStart;
        }
        _tokenType = kind;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = _statementType;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepValue()
    {
        SkipWS();
        var typ = _valType!;
        var name = _valName;

        // Nil check
        if (typ.IsNullable)
        {
            if (PeekNil())
            {
                var pos = Position;
                ReadNilKeyword();
                _state = CurrentChildResume();
                EmitNil(name, typ);
                return StepResult.Emit;
            }
        }
        else if (PeekNil())
        {
            ThrowError($"nil value for non-nullable type {typ}", PaktErrorCode.NilNonNullable);
        }

        if (typ.IsScalar && !typ.IsAtomSet)
        {
            var pos = Position;
            var (start, len) = ReadScalarDirect(typ.ScalarKind);
            _state = CurrentChildResume();
            _tokenType = PaktTokenType.ScalarValue;
            _scalarType = typ.ScalarKind;
            _currentName = name;
            _currentType = typ;
            _valueStart = start;
            _valueLength = len;
            _isNullValue = false;
            return StepResult.Emit;
        }

        if (typ.IsAtomSet)
        {
            var pos = Position;
            var (start, len) = ReadAtomValue(typ.AtomMembers);
            _state = CurrentChildResume();
            _tokenType = PaktTokenType.ScalarValue;
            _scalarType = PaktScalarType.Atom;
            _currentName = name;
            _currentType = typ;
            _valueStart = start;
            _valueLength = len;
            _isNullValue = false;
            return StepResult.Emit;
        }

        if (typ.IsStruct)
        {
            Push(new Frame
            {
                Kind = FrameKind.Struct,
                Resume = CurrentChildResume(),
                Name = name,
                StructFields = typ.StructFields,
            });
            _state = ParserState.StructOpen;
            return StepResult.Continue;
        }

        if (typ.IsTuple)
        {
            Push(new Frame
            {
                Kind = FrameKind.Tuple,
                Resume = CurrentChildResume(),
                Name = name,
                TupleElements = typ.TupleElements,
            });
            _state = ParserState.TupleOpen;
            return StepResult.Continue;
        }

        if (typ.IsList)
        {
            Push(new Frame
            {
                Kind = FrameKind.List,
                Resume = CurrentChildResume(),
                Name = name,
                ListElement = typ.ListElement,
            });
            _state = ParserState.ListOpen;
            return StepResult.Continue;
        }

        if (typ.IsMap)
        {
            Push(new Frame
            {
                Kind = FrameKind.Map,
                Resume = CurrentChildResume(),
                Name = name,
                MapKey = typ.MapKey,
                MapValue = typ.MapValue,
            });
            _state = ParserState.MapOpen;
            return StepResult.Continue;
        }

        ThrowError("Unknown type: no type variant set", PaktErrorCode.Syntax);
        return StepResult.Eof; // unreachable
    }

    // -- Struct states --

    private StepResult StepStructOpen()
    {
        ref var fr = ref Current();
        SkipWS();
        fr.Pos = Position;
        ExpectByte((byte)'{');
        fr.FieldIdx = 0;

        if (fr.StructFields.IsDefaultOrEmpty || fr.StructFields.Length == 0)
            _state = ParserState.StructClose;
        else
            _state = ParserState.StructField;

        _tokenType = PaktTokenType.StructStart;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepStructField()
    {
        ref var fr = ref Current();
        if (fr.FieldIdx == 0)
            SkipInsignificant(skipNewlines: true);

        if (_consumed >= _buffer.Length)
            ThrowError("Unterminated struct value", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed];
        if (b == '}')
        {
            ThrowError(
                $"Too few values in struct: expected {fr.StructFields.Length} fields, got {fr.FieldIdx}",
                PaktErrorCode.Syntax);
        }

        var field = fr.StructFields[fr.FieldIdx];
        fr.ChildResume = ParserState.StructSep;
        _valType = field.Type;
        _valName = field.Name;
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    private StepResult StepStructSep()
    {
        ref var fr = ref Current();
        fr.FieldIdx++;

        if (fr.FieldIdx < fr.StructFields.Length)
        {
            if (!TryReadSep())
            {
                SkipWS();
                if (_consumed < _buffer.Length && _buffer[_consumed] == '}')
                {
                    ThrowError(
                        $"Too few values in struct: expected {fr.StructFields.Length} fields, got {fr.FieldIdx}",
                        PaktErrorCode.Syntax);
                }
                ThrowError("Expected separator between struct fields", PaktErrorCode.Syntax);
            }
            _state = ParserState.StructField;
            return StepResult.Continue;
        }

        // All fields read; consume trailing sep and whitespace
        TryReadSep();
        SkipInsignificant(skipNewlines: true);
        _state = ParserState.StructClose;
        return StepResult.Continue;
    }

    private StepResult StepStructClose()
    {
        ExpectByte((byte)'}');
        var fr = Pop();
        _state = fr.Resume;
        _tokenType = PaktTokenType.StructEnd;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    // -- Tuple states --

    private StepResult StepTupleOpen()
    {
        ref var fr = ref Current();
        SkipWS();
        fr.Pos = Position;
        ExpectByte((byte)'(');
        fr.ElemIdx = 0;

        if (fr.TupleElements.IsDefaultOrEmpty || fr.TupleElements.Length == 0)
            _state = ParserState.TupleClose;
        else
            _state = ParserState.TupleElem;

        _tokenType = PaktTokenType.TupleStart;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepTupleElem()
    {
        ref var fr = ref Current();
        if (fr.ElemIdx == 0)
            SkipInsignificant(skipNewlines: true);

        if (_consumed >= _buffer.Length)
            ThrowError("Unterminated tuple value", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed];
        if (b == ')')
        {
            ThrowError(
                $"Too few values in tuple: expected {fr.TupleElements.Length} elements, got {fr.ElemIdx}",
                PaktErrorCode.Syntax);
        }

        fr.ChildResume = ParserState.TupleSep;
        _valType = fr.TupleElements[fr.ElemIdx];
        _valName = $"[{fr.ElemIdx}]";
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    private StepResult StepTupleSep()
    {
        ref var fr = ref Current();
        fr.ElemIdx++;

        if (fr.ElemIdx < fr.TupleElements.Length)
        {
            if (!TryReadSep())
            {
                SkipWS();
                if (_consumed < _buffer.Length && _buffer[_consumed] == ')')
                {
                    ThrowError(
                        $"Too few values in tuple: expected {fr.TupleElements.Length} elements, got {fr.ElemIdx}",
                        PaktErrorCode.Syntax);
                }
                ThrowError("Expected separator between tuple elements", PaktErrorCode.Syntax);
            }
            _state = ParserState.TupleElem;
            return StepResult.Continue;
        }

        TryReadSep();
        SkipInsignificant(skipNewlines: true);
        _state = ParserState.TupleClose;
        return StepResult.Continue;
    }

    private StepResult StepTupleClose()
    {
        ExpectByte((byte)')');
        var fr = Pop();
        _state = fr.Resume;
        _tokenType = PaktTokenType.TupleEnd;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    // -- List states --

    private StepResult StepListOpen()
    {
        ref var fr = ref Current();
        SkipWS();
        fr.Pos = Position;
        ExpectByte((byte)'[');
        fr.ElemIdx = 0;
        _state = ParserState.ListElem;
        _tokenType = PaktTokenType.ListStart;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepListElem()
    {
        ref var fr = ref Current();
        SkipInsignificant(skipNewlines: true);

        if (_consumed >= _buffer.Length)
            ThrowError("Unterminated list value", PaktErrorCode.UnexpectedEof);

        if (_buffer[_consumed] == ']')
        {
            _state = ParserState.ListClose;
            return StepResult.Continue;
        }

        fr.ChildResume = ParserState.ListSep;
        _valType = fr.ListElement;
        _valName = $"[{fr.ElemIdx}]";
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    private StepResult StepListSep()
    {
        ref var fr = ref Current();
        fr.ElemIdx++;

        if (!TryReadSep())
        {
            SkipWS();
            if (_consumed >= _buffer.Length)
                ThrowError("Unterminated list value", PaktErrorCode.UnexpectedEof);
            if (_buffer[_consumed] != ']')
                ThrowError($"Expected ',' or ']' in list, got '{(char)_buffer[_consumed]}'", PaktErrorCode.Syntax);
            _state = ParserState.ListClose;
            return StepResult.Continue;
        }

        _state = ParserState.ListElem;
        return StepResult.Continue;
    }

    private StepResult StepListClose()
    {
        ExpectByte((byte)']');
        var fr = Pop();
        _state = fr.Resume;
        _tokenType = PaktTokenType.ListEnd;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    // -- Map states --

    private StepResult StepMapOpen()
    {
        ref var fr = ref Current();
        SkipWS();
        fr.Pos = Position;
        ExpectByte((byte)'<');
        fr.KeyStr = "";
        _state = ParserState.MapKey;
        _tokenType = PaktTokenType.MapStart;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepMapKey()
    {
        ref var fr = ref Current();
        SkipInsignificant(skipNewlines: true);

        if (_consumed >= _buffer.Length)
            ThrowError("Unterminated map value", PaktErrorCode.UnexpectedEof);

        if (_buffer[_consumed] == '>')
        {
            _state = ParserState.MapClose;
            return StepResult.Continue;
        }

        var keyType = fr.MapKey!;
        return EmitMapKey(keyType, ParserState.MapAfterKey);
    }

    private StepResult StepMapAssign()
    {
        ref var fr = ref Current();
        SkipWS();
        ExpectByte((byte)';');
        SkipWS();
        fr.ChildResume = ParserState.MapEntry;
        _valType = fr.MapValue;
        _valName = fr.KeyStr;
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    private StepResult StepMapEntry()
    {
        if (!TryReadSep())
        {
            SkipWS();
            if (_consumed >= _buffer.Length)
                ThrowError("Unterminated map value", PaktErrorCode.UnexpectedEof);
            if (_buffer[_consumed] != '>')
                ThrowError($"Expected ',' or '>' in map, got '{(char)_buffer[_consumed]}'", PaktErrorCode.Syntax);
            _state = ParserState.MapClose;
            return StepResult.Continue;
        }
        _state = ParserState.MapKey;
        return StepResult.Continue;
    }

    private StepResult StepMapClose()
    {
        ExpectByte((byte)'>');
        var fr = Pop();
        _state = fr.Resume;
        _tokenType = PaktTokenType.MapEnd;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    // -- Stream states --

    private StepResult StepStreamListItem()
    {
        ref var fr = ref Current();
        if (IsStreamTerminated())
        {
            _state = ParserState.StreamEnd;
            return StepResult.Continue;
        }

        fr.ChildResume = ParserState.StreamListSep;
        _valType = fr.ListElement;
        _valName = $"[{fr.ElemIdx}]";
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    private StepResult StepStreamListSep()
    {
        ref var fr = ref Current();
        fr.ElemIdx++;

        if (!TryReadSep())
        {
            if (IsStreamTerminated())
            {
                _state = ParserState.StreamEnd;
                return StepResult.Continue;
            }
            ThrowError("Expected separator between stream items", PaktErrorCode.Syntax);
        }

        _state = ParserState.StreamListItem;
        return StepResult.Continue;
    }

    private StepResult StepStreamMapKey()
    {
        ref var fr = ref Current();
        if (IsStreamTerminated())
        {
            _state = ParserState.StreamEnd;
            return StepResult.Continue;
        }

        return EmitMapKey(fr.MapKey!, ParserState.StreamMapAfterKey);
    }

    private StepResult StepStreamMapAfterKey()
    {
        ref var fr = ref Current();
        SkipWS();
        ExpectByte((byte)';');
        SkipWS();
        fr.ChildResume = ParserState.StreamMapSep;
        _valType = fr.MapValue;
        _valName = fr.KeyStr;
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    private StepResult StepStreamMapSep()
    {
        if (!TryReadSep())
        {
            if (IsStreamTerminated())
            {
                _state = ParserState.StreamEnd;
                return StepResult.Continue;
            }
            ThrowError("Expected separator between stream map entries", PaktErrorCode.Syntax);
        }
        _state = ParserState.StreamMapKey;
        return StepResult.Continue;
    }

    private StepResult StepAssignEnd()
    {
        var fr = Pop();
        _state = ParserState.Top;
        _tokenType = PaktTokenType.AssignEnd;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    private StepResult StepStreamEnd()
    {
        var fr = Pop();
        _state = ParserState.Top;
        _tokenType = PaktTokenType.StreamEnd;
        _scalarType = PaktScalarType.None;
        _currentName = fr.Name;
        _currentType = null;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = false;
        return StepResult.Emit;
    }

    // -----------------------------------------------------------------------
    // Map key helper
    // -----------------------------------------------------------------------

    private StepResult EmitMapKey(PaktType keyType, ParserState afterState)
    {
        ref var fr = ref Current();

        if (keyType.IsNullable && PeekNil())
        {
            ReadNilKeyword();
            fr.KeyStr = "nil";
            _state = afterState;
            EmitNil(fr.KeyStr, keyType);
            return StepResult.Emit;
        }

        if (!keyType.IsNullable && PeekNil())
            ThrowError($"nil value for non-nullable type {keyType}", PaktErrorCode.NilNonNullable);

        if (keyType.IsScalar && !keyType.IsAtomSet)
        {
            var (start, len) = ReadScalarDirect(keyType.ScalarKind);
            string val;
            if (start == -1 && _usingDecodedBuffer && _decodedBuffer is not null)
                val = Encoding.UTF8.GetString(_decodedBuffer.AsSpan(0, len));
            else
                val = Encoding.UTF8.GetString(_buffer.Slice(start, len));
            fr.KeyStr = val;
            _state = afterState;
            _tokenType = PaktTokenType.ScalarValue;
            _scalarType = keyType.ScalarKind;
            _currentName = val;
            _currentType = keyType;
            _valueStart = start;
            _valueLength = len;
            _isNullValue = false;
            return StepResult.Emit;
        }

        if (keyType.IsAtomSet)
        {
            var (start, len) = ReadAtomValue(keyType.AtomMembers);
            string val;
            if (start == -1 && _usingDecodedBuffer && _decodedBuffer is not null)
                val = Encoding.UTF8.GetString(_decodedBuffer.AsSpan(0, len));
            else
                val = Encoding.UTF8.GetString(_buffer.Slice(start, len));
            fr.KeyStr = val;
            _state = afterState;
            _tokenType = PaktTokenType.ScalarValue;
            _scalarType = PaktScalarType.Atom;
            _currentName = val;
            _currentType = keyType;
            _valueStart = start;
            _valueLength = len;
            _isNullValue = false;
            return StepResult.Emit;
        }

        // Composite map keys: set up for value parsing
        fr.KeyStr = "";
        fr.ChildResume = afterState;
        _valType = keyType;
        _valName = "";
        _state = ParserState.Value;
        return StepResult.Continue;
    }

    // -----------------------------------------------------------------------
    // Emit helpers
    // -----------------------------------------------------------------------

    private void EmitNil(string? name, PaktType typ)
    {
        _tokenType = PaktTokenType.Nil;
        _scalarType = GetScalarTypeKind(typ);
        _currentName = name;
        _currentType = typ;
        _valueStart = 0;
        _valueLength = 0;
        _isNullValue = true;
    }

    private static PaktScalarType GetScalarTypeKind(PaktType t)
    {
        if (t.IsAtomSet) return PaktScalarType.Atom;
        if (t.IsScalar) return t.ScalarKind;
        return PaktScalarType.None;
    }

    // -----------------------------------------------------------------------
    // Stream termination check
    // -----------------------------------------------------------------------

    private bool IsStreamTerminated()
    {
        SkipInsignificant(skipNewlines: true);
        if (_consumed >= _buffer.Length)
            return true;

        byte b = _buffer[_consumed];
        return !CanStartValueInStream(b);
    }

    private bool CanStartValueInStream(byte b)
    {
        if (CanStartValue(b))
            return true;

        return b switch
        {
            (byte)'t' => PeekKeyword("true"u8) || PeekKeyword("false"u8),
            (byte)'f' => PeekKeyword("false"u8),
            (byte)'n' => PeekKeyword("nil"u8),
            (byte)'r' => PeekRawStringStart(),
            (byte)'b' or (byte)'x' => PeekBinLiteralStart(),
            _ => false,
        };
    }

    private static bool CanStartValue(byte b)
    {
        return b switch
        {
            (byte)'\'' or (byte)'"' => true,
            (byte)'{' => true,
            (byte)'(' => true,
            (byte)'[' => true,
            (byte)'<' => true,
            (byte)'|' => true,
            (byte)'.' => true,
            (byte)'-' => true,
            _ => IsDigit(b),
        };
    }

    // -----------------------------------------------------------------------
    // Low-level byte operations
    // -----------------------------------------------------------------------

    private void SkipBOM()
    {
        if (_buffer.Length >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
            _consumed = 3;
    }

    private void SkipWS()
    {
        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (b == ' ' || b == '\t')
            {
                _consumed++;
                _bytePositionInLine++;
            }
            else
                break;
        }
    }

    private void SkipWSAndNewlines()
    {
        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (b == ' ' || b == '\t')
            {
                _consumed++;
                _bytePositionInLine++;
            }
            else if (b == '\n')
            {
                _consumed++;
                _line++;
                _bytePositionInLine = 0;
            }
            else if (b == '\r')
            {
                _consumed++;
                if (_consumed < _buffer.Length && _buffer[_consumed] == '\n')
                    _consumed++;
                _line++;
                _bytePositionInLine = 0;
            }
            else
                break;
        }
    }

    private void SkipComment()
    {
        if (_consumed < _buffer.Length && _buffer[_consumed] == '#')
        {
            while (_consumed < _buffer.Length)
            {
                byte b = _buffer[_consumed];
                _consumed++;
                if (b == '\n')
                {
                    _line++;
                    _bytePositionInLine = 0;
                    return;
                }
                if (b == '\r')
                {
                    if (_consumed < _buffer.Length && _buffer[_consumed] == '\n')
                        _consumed++;
                    _line++;
                    _bytePositionInLine = 0;
                    return;
                }
                _bytePositionInLine++;
            }
        }
    }

    private void SkipInsignificant(bool skipNewlines)
    {
        while (_consumed < _buffer.Length)
        {
            byte b = _buffer[_consumed];
            if (b == ' ' || b == '\t')
            {
                _consumed++;
                _bytePositionInLine++;
            }
            else if (b == '#')
            {
                SkipComment();
            }
            else if (skipNewlines && (b == '\n' || b == '\r'))
            {
                _consumed++;
                if (b == '\r' && _consumed < _buffer.Length && _buffer[_consumed] == '\n')
                    _consumed++;
                _line++;
                _bytePositionInLine = 0;
            }
            else
                break;
        }
    }

    private byte ReadByte()
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Unexpected end of input", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed++];
        if (b == '\n')
        {
            _line++;
            _bytePositionInLine = 0;
        }
        else if (b == '\r')
        {
            if (_consumed < _buffer.Length && _buffer[_consumed] == '\n')
                _consumed++;
            _line++;
            _bytePositionInLine = 0;
        }
        else
        {
            _bytePositionInLine++;
        }
        return b;
    }

    private void ExpectByte(byte expected)
    {
        if (_consumed >= _buffer.Length)
            ThrowError($"Expected '{(char)expected}', got EOF", PaktErrorCode.UnexpectedEof);
        byte b = _buffer[_consumed];
        if (b != expected)
            ThrowError($"Expected '{(char)expected}', got '{(char)b}'", PaktErrorCode.Syntax);
        _consumed++;
        _bytePositionInLine++;
    }

    private bool PeekNil()
    {
        int i = _consumed;
        // skip whitespace
        while (i < _buffer.Length && (_buffer[i] == ' ' || _buffer[i] == '\t'))
            i++;
        if (i + 3 > _buffer.Length)
            return false;
        if (_buffer[i] != 'n' || _buffer[i + 1] != 'i' || _buffer[i + 2] != 'l')
            return false;
        if (i + 3 < _buffer.Length)
        {
            byte next = _buffer[i + 3];
            if (IsAlpha(next) || IsDigit(next) || next == '_' || next == '-')
                return false;
        }
        return true;
    }

    private void ReadNilKeyword()
    {
        SkipWS();
        ExpectByte((byte)'n');
        ExpectByte((byte)'i');
        ExpectByte((byte)'l');
    }

    private bool PeekKeyword(ReadOnlySpan<byte> kw)
    {
        int remaining = _buffer.Length - _consumed;
        if (remaining < kw.Length)
            return false;
        for (int i = 0; i < kw.Length; i++)
        {
            if (_buffer[_consumed + i] != kw[i])
                return false;
        }
        if (remaining > kw.Length)
        {
            byte next = _buffer[_consumed + kw.Length];
            if (IsAlpha(next) || IsDigit(next) || next == '_' || next == '-')
                return false;
        }
        return true;
    }

    private bool PeekRawStringStart()
    {
        if (_consumed + 1 >= _buffer.Length)
            return false;
        return _buffer[_consumed] == 'r' && (_buffer[_consumed + 1] == '\'' || _buffer[_consumed + 1] == '"');
    }

    private bool PeekBinLiteralStart()
    {
        if (_consumed + 1 >= _buffer.Length)
            return false;
        byte first = _buffer[_consumed];
        return (first == 'x' || first == 'b') && _buffer[_consumed + 1] == '\'';
    }

    // Separator: comma or newline
    private bool TryReadSep()
    {
        // First skip WS and comments (not newlines)
        SkipInsignificant(skipNewlines: false);
        if (_consumed >= _buffer.Length)
            return false;

        byte b = _buffer[_consumed];
        if (b == ',')
        {
            _consumed++;
            _bytePositionInLine++;
            SkipInsignificant(skipNewlines: true);
            return true;
        }
        if (b == '\n' || b == '\r')
        {
            _consumed++;
            if (b == '\r' && _consumed < _buffer.Length && _buffer[_consumed] == '\n')
                _consumed++;
            _line++;
            _bytePositionInLine = 0;
            SkipInsignificant(skipNewlines: true);
            return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // Character classification
    // -----------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAlpha(byte b) => (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHex(byte b) => IsDigit(b) || (b >= (byte)'a' && b <= (byte)'f') || (b >= (byte)'A' && b <= (byte)'F');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBin(byte b) => b == (byte)'0' || b == (byte)'1';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOct(byte b) => b >= (byte)'0' && b <= (byte)'7';

    // -----------------------------------------------------------------------
    // Identifier reading
    // -----------------------------------------------------------------------

    private string ReadIdent()
    {
        if (_consumed >= _buffer.Length)
            ThrowError("Expected identifier, got EOF", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed];
        if (!IsAlpha(b) && b != '_')
            ThrowError($"Expected identifier, got '{(char)b}'", PaktErrorCode.Syntax);

        int start = _consumed;
        _consumed++;
        _bytePositionInLine++;

        while (_consumed < _buffer.Length)
        {
            b = _buffer[_consumed];
            if (IsAlpha(b) || IsDigit(b) || b == '_' || b == '-')
            {
                _consumed++;
                _bytePositionInLine++;
            }
            else
                break;
        }

        return Encoding.UTF8.GetString(_buffer.Slice(start, _consumed - start));
    }

    // -----------------------------------------------------------------------
    // Error helpers
    // -----------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ThrowError(string message, PaktErrorCode code = PaktErrorCode.Syntax)
    {
        throw new PaktException(message, Position, code);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposed()
    {
        throw new ObjectDisposedException(nameof(PaktReader));
    }
}
