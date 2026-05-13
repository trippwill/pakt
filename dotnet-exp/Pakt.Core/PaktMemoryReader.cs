using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Pakt;

/// <summary>
/// Synchronous structural reader over <see cref="ReadOnlyMemory{T}"/> input.
/// Produces <see cref="PaktTokenType"/> tokens driven by type context parsed from annotations.
/// </summary>
public sealed class PaktMemoryReader : IPaktReader, IDisposable
{
    // ──────────────────────── internal types ────────────────────────

    private enum Phase : byte
    {
        Start,
        EmitAnnotationStart,
        EmitAnnotationEnd,
        EmitOperator,
        InValue,
        BetweenStatements,
        Done,
    }

    private enum TypeKind : byte
    {
        Str,
        Int,
        Dec,
        Float,
        Bool,
        Uuid,
        Date,
        Ts,
        Bin,
        Struct,
        Tuple,
        List,
        Map,
        AtomSet,
    }

    private struct TypeNode
    {
        public TypeKind Kind;
        public bool IsNullable;
        public int ChildCount;
        public int ChildIndicesStart;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
    private struct ValueContext
    {
        public int TypeNodeIndex;
        public int FieldIndex;
        public int MapPhase; // 0=key, 1=bind, 2=value
    }

    // ──────────────────────── fields ────────────────────────

    private readonly ReadOnlyMemory<byte> _data;
    private readonly PaktReaderOptions _options;

    private PaktLexerState _lexerState;
    private int _consumed;
    private Phase _phase;

    // Type tree — rebuilt per statement
    private TypeNode[] _typeNodes;
    private int _typeNodeCount;
    private int[] _childIndices;
    private int _childIndexCount;

    // Value context stack
    private readonly ValueContext[] _contextStack;
    private int _contextDepth;

    // Current statement
    private int _rootTypeIndex;
    private bool _isPack;
    private bool _rootValueEmitted;
    private int _packMapPhase;

    // Current token
    private PaktTokenType _tokenType;
    private int _valueOffset;
    private int _valueLength;
    private int _depth;

    // Annotation span (absolute offsets into _data)
    private int _annotationOffset;
    private int _annotationLength;

    // Operator saved during header parse
    private PaktTokenType _pendingOp;
    private int _pendingOpOffset;
    private int _pendingOpLength;

    private int _statementCount;

    // ──────────────────────── constructor ────────────────────────

    public PaktMemoryReader(ReadOnlyMemory<byte> data, PaktReaderOptions? options = null)
    {
        _data = data;
        _options = options ?? PaktReaderOptions.Default;
        _typeNodes = new TypeNode[32];
        _childIndices = new int[64];
        _contextStack = new ValueContext[_options.MaxNestingDepth];
    }

    public void Dispose() { }

    // ──────────────────────── properties ────────────────────────

    public PaktTokenType TokenType => _tokenType;

    public ReadOnlySpan<byte> ValueSpan =>
        _valueLength > 0 ? _data.Span.Slice(_valueOffset, _valueLength) : default;

    public int Depth => _depth;

    public long ByteOffset => _consumed;
    public int Line => _lexerState.Line == 0 ? 1 : _lexerState.Line;
    public int Column => _lexerState.Column == 0 ? 1 : _lexerState.Column;

    // ──────────────────────── Read ────────────────────────

    public bool Read()
    {
        return _phase switch
        {
            Phase.Start or Phase.BetweenStatements => ReadStatementOrEnd(),
            Phase.EmitAnnotationStart => EmitAnnotationStart(),
            Phase.EmitAnnotationEnd => EmitAnnotationEnd(),
            Phase.EmitOperator => EmitOperatorToken(),
            Phase.InValue => ReadNextValue(),
            _ => false,
        };
    }

    // ──────────────────────── statement header ────────────────────────

    private bool ReadStatementOrEnd()
    {
        int s = _consumed;
        var lexer = new PaktLexer(_data.Span[s..], true, ref _lexerState);
        var res = lexer.Read(out var tok);

        if (res == PaktReadResult.EndOfInput)
        {
            _consumed = (int)_lexerState.TotalConsumed;
            _phase = Phase.Done;
            return Emit(PaktTokenType.EndOfUnit, 0, 0);
        }

        if (tok.Kind is PaktLexicalTokenKind.Nul or PaktLexicalTokenKind.Eof)
        {
            _consumed = (int)_lexerState.TotalConsumed;
            _phase = Phase.Done;
            return Emit(PaktTokenType.EndOfUnit, s + tok.Offset, tok.Length);
        }

        if (tok.Kind != PaktLexicalTokenKind.Ident)
            ThrowSyntax("Expected statement name or end of unit");

        var nameBytes = _data.Span.Slice(s + tok.Offset, tok.Length);
        if (IsKeyword(nameBytes))
            ThrowSyntax("Reserved keyword cannot be a statement name");

        int nameOff = s + tok.Offset;
        int nameLen = tok.Length;

        _statementCount++;
        if (_statementCount > _options.MaxStatementCount)
            ThrowSyntax("Statement count limit exceeded");

        return ParseStatementHeader(ref lexer, s, nameOff, nameLen);
    }

    private bool ParseStatementHeader(ref PaktLexer lexer, int s, int nameOff, int nameLen)
    {
        // Colon — no layout permitted between name and colon
        var res = lexer.Read(out var tok);
        if (res != PaktReadResult.Token || tok.Kind != PaktLexicalTokenKind.Colon)
            ThrowSyntax("Expected ':' after statement name");

        // Parse type annotation
        _typeNodeCount = 0;
        _childIndexCount = 0;
        _annotationOffset = -1;
        int annotEnd = 0;
        _rootTypeIndex = ParseTypeWithNullable(
            ref lexer, s, ref annotEnd,
            out bool hasLeftover, out var leftoverTok);

        if (_annotationOffset < 0)
            ThrowSyntax("Empty type annotation");
        _annotationLength = annotEnd - _annotationOffset;

        // Operator: '=' or '<<'
        PaktLexicalToken opTok;
        if (hasLeftover)
        {
            opTok = leftoverTok;
        }
        else
        {
            res = lexer.Read(out opTok);
            if (res != PaktReadResult.Token)
                ThrowSyntax("Expected '=' or '<<'");
        }

        if (opTok.Kind == PaktLexicalTokenKind.Assign)
        {
            _isPack = false;
            _pendingOp = PaktTokenType.AssignOperator;
        }
        else if (opTok.Kind == PaktLexicalTokenKind.Pack)
        {
            _isPack = true;
            _pendingOp = PaktTokenType.PackOperator;
        }
        else
        {
            ThrowSyntax("Expected '=' or '<<' after type annotation");
        }

        _pendingOpOffset = s + opTok.Offset;
        _pendingOpLength = opTok.Length;
        _consumed = (int)_lexerState.TotalConsumed;
        _phase = Phase.EmitAnnotationStart;
        _contextDepth = 0;
        _rootValueEmitted = false;
        _packMapPhase = 0;
        _depth = 0;

        return Emit(PaktTokenType.StatementName, nameOff, nameLen);
    }

    // ──────────────────────── annotation framing ────────────────────────

    private bool EmitAnnotationStart()
    {
        _phase = Phase.EmitAnnotationEnd;
        return Emit(PaktTokenType.TypeAnnotationStart, _annotationOffset, _annotationLength);
    }

    private bool EmitAnnotationEnd()
    {
        _phase = Phase.EmitOperator;
        return Emit(PaktTokenType.TypeAnnotationEnd, 0, 0);
    }

    private bool EmitOperatorToken()
    {
        _phase = Phase.InValue;
        return Emit(_pendingOp, _pendingOpOffset, _pendingOpLength);
    }

    // ──────────────────────── value reading ────────────────────────

    private bool ReadNextValue()
    {
        if (_contextDepth > 0)
            return ReadCompositeChild();

        if (_isPack)
            return ReadPackValue();

        if (!_rootValueEmitted)
        {
            _rootValueEmitted = true;
            return ReadValueByType(_rootTypeIndex);
        }

        // Assign statement complete
        _phase = Phase.BetweenStatements;
        return ReadStatementOrEnd();
    }

    private bool ReadValueByType(int typeIdx)
    {
        ref var node = ref _typeNodes[typeIdx];

        // Nullable: check for nil
        if (node.IsNullable && PeekIsNilIdent())
            return ConsumeNilToken();

        return node.Kind switch
        {
            TypeKind.Str => ConsumeScalar(PaktTokenType.String, PaktLexicalTokenKind.String),
            TypeKind.Int => ConsumeScalar(PaktTokenType.Int, PaktLexicalTokenKind.Number),
            TypeKind.Dec => ConsumeScalar(PaktTokenType.Decimal, PaktLexicalTokenKind.Number),
            TypeKind.Float => ConsumeScalar(PaktTokenType.Float, PaktLexicalTokenKind.Number),
            TypeKind.Bool => ConsumeBoolToken(),
            TypeKind.Uuid => ConsumeScalar(PaktTokenType.Uuid, PaktLexicalTokenKind.Number),
            TypeKind.Date => ConsumeScalar(PaktTokenType.Date, PaktLexicalTokenKind.Number),
            TypeKind.Ts => ConsumeTimestampTokens(),
            TypeKind.Bin => ConsumeScalar(PaktTokenType.Binary, PaktLexicalTokenKind.Binary),
            TypeKind.AtomSet => ConsumeAtomToken(),
            TypeKind.Struct => OpenComposite(
                typeIdx, PaktTokenType.StructStart, PaktLexicalTokenKind.LBrace),
            TypeKind.Tuple => OpenComposite(
                typeIdx, PaktTokenType.TupleStart, PaktLexicalTokenKind.LParen),
            TypeKind.List => OpenComposite(
                typeIdx, PaktTokenType.ListStart, PaktLexicalTokenKind.LBrack),
            TypeKind.Map => OpenComposite(
                typeIdx, PaktTokenType.MapStart, PaktLexicalTokenKind.LAngle),
            _ => throw new InvalidOperationException($"Unhandled type kind {node.Kind}"),
        };
    }

    private bool ConsumeScalar(PaktTokenType structural, PaktLexicalTokenKind lexical)
    {
        var tok = LexNext(out int s);
        if (tok.Kind != lexical)
            ThrowSyntax($"Expected {lexical} token, got {tok.Kind}");
        return Emit(structural, s + tok.Offset, tok.Length);
    }

    private bool ConsumeBoolToken()
    {
        var tok = LexNext(out int s);
        if (tok.Kind != PaktLexicalTokenKind.Ident)
            ThrowSyntax("Expected 'true' or 'false'");
        var span = _data.Span.Slice(s + tok.Offset, tok.Length);
        if (!span.SequenceEqual("true"u8) && !span.SequenceEqual("false"u8))
            ThrowSyntax("Expected 'true' or 'false'");
        return Emit(PaktTokenType.Bool, s + tok.Offset, tok.Length);
    }

    private bool ConsumeAtomToken()
    {
        var tok = LexNext(out int s);
        if (tok.Kind != PaktLexicalTokenKind.AtomPrefix)
            ThrowSyntax("Expected atom value (|name)");
        return Emit(PaktTokenType.Atom, s + tok.Offset, tok.Length);
    }

    private bool ConsumeNilToken()
    {
        var tok = LexNext(out int s);
        Debug.Assert(tok.Kind == PaktLexicalTokenKind.Ident);
        return Emit(PaktTokenType.Nil, s + tok.Offset, tok.Length);
    }

    /// <summary>
    /// Timestamps span multiple lexical tokens because the lexer splits on ':'.
    /// Consumes Number (Colon Number)* to reassemble the full timestamp span.
    /// </summary>
    private bool ConsumeTimestampTokens()
    {
        var tok = LexNext(out int s);
        if (tok.Kind != PaktLexicalTokenKind.Number)
            ThrowSyntax("Expected timestamp value");

        int valStart = s + tok.Offset;
        int valEnd = s + tok.Offset + tok.Length;

        while (PeekLexicalKind() == PaktLexicalTokenKind.Colon)
        {
            var colonTok = LexNext(out int cs);
            valEnd = cs + colonTok.Offset + colonTok.Length;

            if (PeekLexicalKind() == PaktLexicalTokenKind.Number)
            {
                var numTok = LexNext(out int ns);
                valEnd = ns + numTok.Offset + numTok.Length;
            }
        }

        return Emit(PaktTokenType.Timestamp, valStart, valEnd - valStart);
    }

    private bool OpenComposite(
        int typeIdx,
        PaktTokenType startType,
        PaktLexicalTokenKind delimiter)
    {
        var tok = LexNext(out int s);
        if (tok.Kind != delimiter)
            ThrowSyntax($"Expected opening delimiter for {startType}");

        if (_contextDepth >= _options.MaxNestingDepth)
            ThrowNesting();

        _contextStack[_contextDepth] = new ValueContext { TypeNodeIndex = typeIdx };
        _contextDepth++;
        int d = _depth;
        _depth++;

        return Emit(startType, s + tok.Offset, tok.Length, d);
    }

    private bool CloseComposite(PaktTokenType endType, int off, int len)
    {
        _contextDepth--;
        _depth--;
        return Emit(endType, off, len, _depth);
    }

    // ──────────────────────── composite children ────────────────────────

    private bool ReadCompositeChild()
    {
        ref var ctx = ref _contextStack[_contextDepth - 1];
        ref var node = ref _typeNodes[ctx.TypeNodeIndex];

        return node.Kind switch
        {
            TypeKind.Struct => ReadStructChild(ref ctx, ref node),
            TypeKind.Tuple => ReadTupleChild(ref ctx, ref node),
            TypeKind.List => ReadListChild(ref ctx, ref node),
            TypeKind.Map => ReadMapChild(ref ctx, ref node),
            _ => throw new InvalidOperationException(),
        };
    }

    private bool ReadStructChild(ref ValueContext ctx, ref TypeNode node)
    {
        if (ctx.FieldIndex >= node.ChildCount)
        {
            var tok = LexNext(out int s);
            if (tok.Kind != PaktLexicalTokenKind.RBrace)
                ThrowSyntax("Expected '}' — too many values in struct");
            return CloseComposite(PaktTokenType.StructEnd, s + tok.Offset, tok.Length);
        }

        int childIdx = _childIndices[node.ChildIndicesStart + ctx.FieldIndex];
        ctx.FieldIndex++;
        return ReadValueByType(childIdx);
    }

    private bool ReadTupleChild(ref ValueContext ctx, ref TypeNode node)
    {
        if (ctx.FieldIndex >= node.ChildCount)
        {
            var tok = LexNext(out int s);
            if (tok.Kind != PaktLexicalTokenKind.RParen)
                ThrowSyntax("Expected ')' — too many values in tuple");
            return CloseComposite(PaktTokenType.TupleEnd, s + tok.Offset, tok.Length);
        }

        int childIdx = _childIndices[node.ChildIndicesStart + ctx.FieldIndex];
        ctx.FieldIndex++;
        return ReadValueByType(childIdx);
    }

    private bool ReadListChild(ref ValueContext ctx, ref TypeNode node)
    {
        if (PeekLexicalKind() == PaktLexicalTokenKind.RBrack)
        {
            var tok = LexNext(out int s);
            return CloseComposite(PaktTokenType.ListEnd, s + tok.Offset, tok.Length);
        }

        int elemIdx = _childIndices[node.ChildIndicesStart];
        return ReadValueByType(elemIdx);
    }

    private bool ReadMapChild(ref ValueContext ctx, ref TypeNode node)
    {
        switch (ctx.MapPhase)
        {
            case 0: // key or close
                if (PeekLexicalKind() == PaktLexicalTokenKind.RAngle)
                {
                    var tok = LexNext(out int s);
                    return CloseComposite(PaktTokenType.MapEnd, s + tok.Offset, tok.Length);
                }

                ctx.MapPhase = 1;
                return ReadValueByType(_childIndices[node.ChildIndicesStart]);

            case 1: // bind (=>)
            {
                var tok = LexNext(out int s);
                if (tok.Kind != PaktLexicalTokenKind.Bind)
                    ThrowSyntax("Expected '=>' in map entry");
                ctx.MapPhase = 2;
                return Emit(PaktTokenType.MapEntryBind, s + tok.Offset, tok.Length);
            }

            default: // value
                ctx.MapPhase = 0;
                return ReadValueByType(_childIndices[node.ChildIndicesStart + 1]);
        }
    }

    // ──────────────────────── pack values ────────────────────────

    private bool ReadPackValue()
    {
        ref var root = ref _typeNodes[_rootTypeIndex];

        if (root.Kind == TypeKind.List)
        {
            if (_rootValueEmitted && IsPackTerminated())
            {
                _phase = Phase.BetweenStatements;
                return ReadStatementOrEnd();
            }

            _rootValueEmitted = true;
            return ReadValueByType(_childIndices[root.ChildIndicesStart]);
        }

        if (root.Kind == TypeKind.Map)
        {
            switch (_packMapPhase)
            {
                case 0: // key or end
                    if (_rootValueEmitted && IsPackTerminated())
                    {
                        _phase = Phase.BetweenStatements;
                        return ReadStatementOrEnd();
                    }

                    _rootValueEmitted = true;
                    _packMapPhase = 1;
                    return ReadValueByType(_childIndices[root.ChildIndicesStart]);

                case 1: // bind
                {
                    var tok = LexNext(out int s);
                    if (tok.Kind != PaktLexicalTokenKind.Bind)
                        ThrowSyntax("Expected '=>' in map pack entry");
                    _packMapPhase = 2;
                    return Emit(PaktTokenType.MapEntryBind, s + tok.Offset, tok.Length);
                }

                default: // value
                    _packMapPhase = 0;
                    return ReadValueByType(_childIndices[root.ChildIndicesStart + 1]);
            }
        }

        ThrowSyntax("Pack type must be a list or map");
        return false;
    }

    private bool IsPackTerminated()
    {
        var kind = PeekLexicalKind();
        if (kind is PaktLexicalTokenKind.Nul or PaktLexicalTokenKind.Eof)
            return true;
        if (kind != PaktLexicalTokenKind.Semicolon)
            return false;

        // Consume all consecutive semicolons
        do { _ = LexNext(out _); }
        while (PeekLexicalKind() == PaktLexicalTokenKind.Semicolon);

        return true;
    }

    // ──────────────────────── type annotation parser ────────────────────────

    private int ParseTypeWithNullable(
        ref PaktLexer lexer,
        int sliceBase,
        ref int annotEnd,
        out bool hasLeftover,
        out PaktLexicalToken leftoverTok)
    {
        hasLeftover = false;
        leftoverTok = default;

        int nodeIdx = ParseBaseType(ref lexer, sliceBase, ref annotEnd);

        // Nullable suffix?
        var res = lexer.Read(out var tok);
        if (res == PaktReadResult.Token)
        {
            if (tok.Kind == PaktLexicalTokenKind.Nullable)
            {
                _typeNodes[nodeIdx].IsNullable = true;
                TrackAnnotEnd(sliceBase + tok.Offset + tok.Length, ref annotEnd);
            }
            else
            {
                hasLeftover = true;
                leftoverTok = tok;
            }
        }

        return nodeIdx;
    }

    private int ParseBaseType(ref PaktLexer lexer, int sliceBase, ref int annotEnd)
    {
        var res = lexer.Read(out var tok);
        if (res != PaktReadResult.Token)
            ThrowSyntax("Expected type");

        TrackAnnotStart(sliceBase + tok.Offset);
        TrackAnnotEnd(sliceBase + tok.Offset + tok.Length, ref annotEnd);

        return tok.Kind switch
        {
            PaktLexicalTokenKind.Ident => MakeScalarNode(tok, sliceBase),
            PaktLexicalTokenKind.LBrace => ParseStructType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.LParen => ParseTupleType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.LBrack => ParseListType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.LAngle => ParseMapType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.Pipe => ParseAtomSetType(ref lexer, sliceBase, ref annotEnd),
            _ => ThrowSyntax<int>($"Unexpected token {tok.Kind} in type annotation"),
        };
    }

    private int MakeScalarNode(PaktLexicalToken tok, int sliceBase)
    {
        var span = _data.Span.Slice(sliceBase + tok.Offset, tok.Length);

        TypeKind kind = default;
        if (span.SequenceEqual("str"u8)) kind = TypeKind.Str;
        else if (span.SequenceEqual("int"u8)) kind = TypeKind.Int;
        else if (span.SequenceEqual("dec"u8)) kind = TypeKind.Dec;
        else if (span.SequenceEqual("float"u8)) kind = TypeKind.Float;
        else if (span.SequenceEqual("bool"u8)) kind = TypeKind.Bool;
        else if (span.SequenceEqual("uuid"u8)) kind = TypeKind.Uuid;
        else if (span.SequenceEqual("date"u8)) kind = TypeKind.Date;
        else if (span.SequenceEqual("ts"u8)) kind = TypeKind.Ts;
        else if (span.SequenceEqual("bin"u8)) kind = TypeKind.Bin;
        else
            return ThrowSyntax<int>($"Unknown scalar type '{Encoding.UTF8.GetString(span)}'");

        return AllocNode(new TypeNode { Kind = kind });
    }

    private int ParseStructType(ref PaktLexer lexer, int sliceBase, ref int annotEnd)
    {
        int nodeIdx = AllocNode(default);
        Span<int> children = stackalloc int[64];
        int childCount = ParseStructFields(ref lexer, sliceBase, ref annotEnd, children);

        int start = AllocChildSlots(childCount);
        children[..childCount].CopyTo(_childIndices.AsSpan(start, childCount));

        _typeNodes[nodeIdx] = new TypeNode
        {
            Kind = TypeKind.Struct,
            ChildCount = childCount,
            ChildIndicesStart = start,
        };
        return nodeIdx;
    }

    private int ParseStructFields(
        ref PaktLexer lexer,
        int sliceBase,
        ref int annotEnd,
        scoped Span<int> children)
    {
        int childCount = 0;
        bool hasPending = false;
        PaktLexicalToken pendingTok = default;

        while (true)
        {
            PaktLexicalToken tok;
            if (hasPending)
            {
                tok = pendingTok;
                hasPending = false;
            }
            else
            {
                var res = lexer.Read(out tok);
                if (res != PaktReadResult.Token)
                    ThrowSyntax("Unterminated struct type");
            }

            TrackAnnotEnd(sliceBase + tok.Offset + tok.Length, ref annotEnd);

            if (tok.Kind == PaktLexicalTokenKind.RBrace)
                break;

            if (tok.Kind != PaktLexicalTokenKind.Ident)
                ThrowSyntax("Expected field name or '}'");

            var cr = lexer.Read(out var ct);
            if (cr != PaktReadResult.Token || ct.Kind != PaktLexicalTokenKind.Colon)
                ThrowSyntax("Expected ':' after field name");

            int fieldType = ParseTypeWithNullable(
                ref lexer, sliceBase, ref annotEnd,
                out hasPending, out pendingTok);

            if (childCount >= children.Length)
                ThrowSyntax("Too many struct fields");
            children[childCount++] = fieldType;
        }

        return childCount;
    }

    private int ParseTupleType(ref PaktLexer lexer, int sliceBase, ref int annotEnd)
    {
        int nodeIdx = AllocNode(default);
        Span<int> children = stackalloc int[64];
        int childCount = ParseTupleElements(ref lexer, sliceBase, ref annotEnd, children);

        int start = AllocChildSlots(childCount);
        children[..childCount].CopyTo(_childIndices.AsSpan(start, childCount));

        _typeNodes[nodeIdx] = new TypeNode
        {
            Kind = TypeKind.Tuple,
            ChildCount = childCount,
            ChildIndicesStart = start,
        };
        return nodeIdx;
    }

    private int ParseTupleElements(
        ref PaktLexer lexer,
        int sliceBase,
        ref int annotEnd,
        scoped Span<int> children)
    {
        int childCount = 0;
        bool hasPending = false;
        PaktLexicalToken pendingTok = default;

        while (true)
        {
            PaktLexicalToken tok;
            if (hasPending)
            {
                tok = pendingTok;
                hasPending = false;
            }
            else
            {
                var res = lexer.Read(out tok);
                if (res != PaktReadResult.Token)
                    ThrowSyntax("Unterminated tuple type");
            }

            TrackAnnotEnd(sliceBase + tok.Offset + tok.Length, ref annotEnd);

            if (tok.Kind == PaktLexicalTokenKind.RParen)
                break;

            TrackAnnotStart(sliceBase + tok.Offset);
            int elemType = ParseBaseTypeFromToken(
                ref lexer, sliceBase, ref annotEnd, tok);

            // Nullable check
            var nr = lexer.Read(out var nt);
            if (nr == PaktReadResult.Token)
            {
                if (nt.Kind == PaktLexicalTokenKind.Nullable)
                {
                    _typeNodes[elemType].IsNullable = true;
                    TrackAnnotEnd(sliceBase + nt.Offset + nt.Length, ref annotEnd);
                }
                else
                {
                    hasPending = true;
                    pendingTok = nt;
                }
            }

            if (childCount >= children.Length)
                ThrowSyntax("Too many tuple elements");
            children[childCount++] = elemType;
        }

        return childCount;
    }

    private int ParseListType(ref PaktLexer lexer, int sliceBase, ref int annotEnd)
    {
        int elemType = ParseTypeWithNullable(
            ref lexer, sliceBase, ref annotEnd,
            out bool hasPending, out var pendingTok);

        // Expect closing bracket
        PaktLexicalToken closeTok;
        if (hasPending)
        {
            closeTok = pendingTok;
        }
        else
        {
            var res = lexer.Read(out closeTok);
            if (res != PaktReadResult.Token)
                ThrowSyntax("Unterminated list type");
        }

        if (closeTok.Kind != PaktLexicalTokenKind.RBrack)
            ThrowSyntax("Expected ']' in list type");
        TrackAnnotEnd(sliceBase + closeTok.Offset + closeTok.Length, ref annotEnd);

        int nodeIdx = AllocNode(default);
        int start = AllocChildSlots(1);
        _childIndices[start] = elemType;

        _typeNodes[nodeIdx] = new TypeNode
        {
            Kind = TypeKind.List,
            ChildCount = 1,
            ChildIndicesStart = start,
        };
        return nodeIdx;
    }

    private int ParseMapType(ref PaktLexer lexer, int sliceBase, ref int annotEnd)
    {
        // Key type
        int keyType = ParseTypeWithNullable(
            ref lexer, sliceBase, ref annotEnd,
            out bool hasPending, out var pendingTok);

        // Expect '=>'
        PaktLexicalToken bindTok;
        if (hasPending)
        {
            bindTok = pendingTok;
        }
        else
        {
            var res = lexer.Read(out bindTok);
            if (res != PaktReadResult.Token)
                ThrowSyntax("Expected '=>' in map type");
        }

        if (bindTok.Kind != PaktLexicalTokenKind.Bind)
            ThrowSyntax("Expected '=>' in map type");

        // Value type
        int valType = ParseTypeWithNullable(
            ref lexer, sliceBase, ref annotEnd,
            out hasPending, out pendingTok);

        // Expect '>'
        PaktLexicalToken closeTok;
        if (hasPending)
        {
            closeTok = pendingTok;
        }
        else
        {
            var res = lexer.Read(out closeTok);
            if (res != PaktReadResult.Token)
                ThrowSyntax("Unterminated map type");
        }

        if (closeTok.Kind != PaktLexicalTokenKind.RAngle)
            ThrowSyntax("Expected '>' in map type");
        TrackAnnotEnd(sliceBase + closeTok.Offset + closeTok.Length, ref annotEnd);

        int nodeIdx = AllocNode(default);
        int start = AllocChildSlots(2);
        _childIndices[start] = keyType;
        _childIndices[start + 1] = valType;

        _typeNodes[nodeIdx] = new TypeNode
        {
            Kind = TypeKind.Map,
            ChildCount = 2,
            ChildIndicesStart = start,
        };
        return nodeIdx;
    }

    private int ParseAtomSetType(ref PaktLexer lexer, int sliceBase, ref int annotEnd)
    {
        // Read idents until closing pipe
        int memberCount = 0;
        while (true)
        {
            var res = lexer.Read(out var tok);
            if (res != PaktReadResult.Token)
                ThrowSyntax("Unterminated atom set type");

            TrackAnnotEnd(sliceBase + tok.Offset + tok.Length, ref annotEnd);

            if (tok.Kind == PaktLexicalTokenKind.Pipe)
                break;

            if (tok.Kind != PaktLexicalTokenKind.Ident)
                ThrowSyntax("Expected atom member or '|'");

            var span = _data.Span.Slice(sliceBase + tok.Offset, tok.Length);
            if (IsKeyword(span))
                ThrowSyntax("Keywords cannot be atom members");

            memberCount++;
        }

        if (memberCount == 0)
            ThrowSyntax("Empty atom sets are not allowed");

        return AllocNode(new TypeNode
        {
            Kind = TypeKind.AtomSet,
            ChildCount = memberCount,
        });
    }

    /// <summary>
    /// Dispatch a type parse starting from an already-consumed token.
    /// Used by the tuple parser where we've consumed the first token to check for RParen.
    /// </summary>
    private int ParseBaseTypeFromToken(
        ref PaktLexer lexer,
        int sliceBase,
        ref int annotEnd,
        PaktLexicalToken tok)
    {
        return tok.Kind switch
        {
            PaktLexicalTokenKind.Ident => MakeScalarNode(tok, sliceBase),
            PaktLexicalTokenKind.LBrace => ParseStructType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.LParen => ParseTupleType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.LBrack => ParseListType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.LAngle => ParseMapType(ref lexer, sliceBase, ref annotEnd),
            PaktLexicalTokenKind.Pipe => ParseAtomSetType(ref lexer, sliceBase, ref annotEnd),
            _ => ThrowSyntax<int>($"Unexpected token {tok.Kind} in type annotation"),
        };
    }

    // ──────────────────────── typed accessors ────────────────────────

    public string ReadString()
    {
        if (!Read())
            ThrowUnexpectedEnd("string");
        if (_tokenType != PaktTokenType.String)
            ThrowUnexpectedToken(PaktTokenType.String, _tokenType);
        return DecodeString(ValueSpan);
    }

    public string? ReadStringOrNil()
    {
        return TryReadNil() ? null : ReadString();
    }

    public int ReadInt32()
    {
        if (!Read())
            ThrowUnexpectedEnd("int32");
        if (_tokenType != PaktTokenType.Int)
            ThrowUnexpectedToken(PaktTokenType.Int, _tokenType);
        return (int)ParseIntegerValue(ValueSpan);
    }

    public long ReadInt64()
    {
        if (!Read())
            ThrowUnexpectedEnd("int64");
        if (_tokenType != PaktTokenType.Int)
            ThrowUnexpectedToken(PaktTokenType.Int, _tokenType);
        return ParseIntegerValue(ValueSpan);
    }

    public double ReadDouble()
    {
        if (!Read())
            ThrowUnexpectedEnd("double");
        if (_tokenType is not (PaktTokenType.Float or PaktTokenType.Decimal or PaktTokenType.Int))
            ThrowUnexpectedToken(PaktTokenType.Float, _tokenType);
        return ParseDoubleValue(ValueSpan);
    }

    public decimal ReadDecimal()
    {
        if (!Read())
            ThrowUnexpectedEnd("decimal");
        if (_tokenType is not (PaktTokenType.Decimal or PaktTokenType.Int))
            ThrowUnexpectedToken(PaktTokenType.Decimal, _tokenType);
        return ParseDecimalValue(ValueSpan);
    }

    public bool ReadBool()
    {
        if (!Read())
            ThrowUnexpectedEnd("bool");
        if (_tokenType != PaktTokenType.Bool)
            ThrowUnexpectedToken(PaktTokenType.Bool, _tokenType);
        return ValueSpan.SequenceEqual("true"u8);
    }

    public ReadOnlySpan<byte> ReadRawValue()
    {
        if (!Read())
            ThrowUnexpectedEnd("value");
        return ValueSpan;
    }

    public void ExpectToken(PaktTokenType expected)
    {
        if (!Read())
            ThrowUnexpectedEnd(expected.ToString());
        if (_tokenType != expected)
            ThrowUnexpectedToken(expected, _tokenType);
    }

    public bool TryExpectToken(PaktTokenType expected)
    {
        if (!Read())
            return false;
        return _tokenType == expected;
    }

    public bool TryReadNil()
    {
        if (!PeekIsNilIdent())
            return false;

        // The state machine will handle nil on the next Read()
        bool ok = Read();
        Debug.Assert(ok && _tokenType == PaktTokenType.Nil);
        return true;
    }

    public bool VerifyTypeAnnotation(ReadOnlySpan<byte> expectedSignature)
    {
        return ValueSpan.SequenceEqual(expectedSignature);
    }

    // ──────────────────────── lexer helpers ────────────────────────

    /// <summary>Consume the next lexical token and advance <c>_consumed</c>.</summary>
    private PaktLexicalToken LexNext(out int sliceStart)
    {
        sliceStart = _consumed;
        var lexer = new PaktLexer(_data.Span[sliceStart..], true, ref _lexerState);
        var res = lexer.Read(out var tok);
        if (res != PaktReadResult.Token)
            ThrowUnexpectedEnd("token");
        _consumed = (int)_lexerState.TotalConsumed;
        return tok;
    }

    /// <summary>Peek at the next lexical token kind without consuming.</summary>
    private PaktLexicalTokenKind PeekLexicalKind()
    {
        var saved = _lexerState;
        var lexer = new PaktLexer(_data.Span[_consumed..], true, ref _lexerState);
        var res = lexer.Read(out var tok);
        _lexerState = saved;
        return res == PaktReadResult.Token ? tok.Kind : PaktLexicalTokenKind.Eof;
    }

    /// <summary>Peek whether the next lexical token is <c>nil</c>.</summary>
    private bool PeekIsNilIdent()
    {
        var saved = _lexerState;
        var lexer = new PaktLexer(_data.Span[_consumed..], true, ref _lexerState);
        var res = lexer.Read(out var tok);
        bool isNil = res == PaktReadResult.Token
            && tok.Kind == PaktLexicalTokenKind.Ident
            && _data.Span.Slice(_consumed + tok.Offset, tok.Length).SequenceEqual("nil"u8);
        _lexerState = saved;
        return isNil;
    }

    // ──────────────────────── emit / tracking ────────────────────────

    private bool Emit(PaktTokenType type, int off, int len)
    {
        _tokenType = type;
        _valueOffset = off;
        _valueLength = len;
        return true;
    }

    private bool Emit(PaktTokenType type, int off, int len, int depth)
    {
        _tokenType = type;
        _valueOffset = off;
        _valueLength = len;
        _depth = depth;
        return true;
    }

    private void TrackAnnotStart(int absOff)
    {
        if (_annotationOffset < 0)
            _annotationOffset = absOff;
    }

    private void TrackAnnotEnd(int absEnd, ref int annotEnd)
    {
        if (absEnd > annotEnd)
            annotEnd = absEnd;
    }

    // ──────────────────────── node allocation ────────────────────────

    private int AllocNode(TypeNode node)
    {
        if (_typeNodeCount >= _typeNodes.Length)
            Array.Resize(ref _typeNodes, _typeNodes.Length * 2);
        int idx = _typeNodeCount++;
        _typeNodes[idx] = node;
        return idx;
    }

    private int AllocChildSlots(int count)
    {
        while (_childIndexCount + count > _childIndices.Length)
            Array.Resize(ref _childIndices, _childIndices.Length * 2);
        int start = _childIndexCount;
        _childIndexCount += count;
        return start;
    }

    // ──────────────────────── scalar parsing ────────────────────────

    private static string DecodeString(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return string.Empty;

        // Determine string variant by prefix/delimiters
        bool isRaw = raw[0] == (byte)'r';
        int prefixLen = isRaw ? 1 : 0;

        if (raw.Length < prefixLen + 2)
            return string.Empty;

        ReadOnlySpan<byte> afterPrefix = raw[prefixLen..];
        bool isTriple = afterPrefix.Length >= 6
            && afterPrefix[0] == (byte)'\''
            && afterPrefix[1] == (byte)'\''
            && afterPrefix[2] == (byte)'\'';

        int delimLen = isTriple ? 3 : 1;
        ReadOnlySpan<byte> content = afterPrefix[delimLen..^delimLen];

        if (isRaw || !content.Contains((byte)'\\'))
            return Encoding.UTF8.GetString(content);

        return DecodeEscapedString(content);
    }

    private static string DecodeEscapedString(ReadOnlySpan<byte> content)
    {
        var sb = new StringBuilder(content.Length);
        int i = 0;
        while (i < content.Length)
        {
            byte b = content[i];
            if (b != (byte)'\\')
            {
                // Fast path: find run of non-escape bytes
                int start = i;
                while (i < content.Length && content[i] != (byte)'\\')
                    i++;
                sb.Append(Encoding.UTF8.GetString(content[start..i]));
                continue;
            }

            // Escape
            if (i + 1 >= content.Length) break;
            byte esc = content[i + 1];
            switch (esc)
            {
                case (byte)'\\': sb.Append('\\'); i += 2; break;
                case (byte)'\'': sb.Append('\''); i += 2; break;
                case (byte)'n': sb.Append('\n'); i += 2; break;
                case (byte)'r': sb.Append('\r'); i += 2; break;
                case (byte)'t': sb.Append('\t'); i += 2; break;
                case (byte)'u':
                    if (i + 5 < content.Length)
                    {
                        int cp = HexVal(content[i + 2]) << 12
                            | HexVal(content[i + 3]) << 8
                            | HexVal(content[i + 4]) << 4
                            | HexVal(content[i + 5]);
                        sb.Append((char)cp);
                        i += 6;
                    }
                    else
                    {
                        sb.Append('\\');
                        i++;
                    }
                    break;
                default:
                    sb.Append('\\');
                    i++;
                    break;
            }
        }

        return sb.ToString();
    }

    private static long ParseIntegerValue(ReadOnlySpan<byte> raw)
    {
        // Strip underscores if present
        if (raw.Contains((byte)'_'))
            return ParseIntegerSlow(raw);

        // Check for base prefix
        bool neg = raw.Length > 0 && raw[0] == (byte)'-';
        ReadOnlySpan<byte> digits = neg ? raw[1..] : raw;

        if (digits.Length >= 2 && digits[0] == (byte)'0')
        {
            byte prefix = digits[1];
            if (prefix is (byte)'x' or (byte)'X')
                return ParseHexLong(digits[2..], neg);
            if (prefix is (byte)'b' or (byte)'B')
                return ParseBinaryLong(digits[2..], neg);
            if (prefix is (byte)'o' or (byte)'O')
                return ParseOctalLong(digits[2..], neg);
        }

        if (Utf8Parser.TryParse(raw, out long value, out int consumed) && consumed == raw.Length)
            return value;

        return ParseIntegerSlow(raw);
    }

    private static long ParseIntegerSlow(ReadOnlySpan<byte> raw)
    {
        Span<byte> clean = stackalloc byte[raw.Length];
        int len = 0;
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] != (byte)'_')
                clean[len++] = raw[i];

        return ParseIntegerValue(clean[..len]); // recurse without underscores
    }

    private static long ParseHexLong(ReadOnlySpan<byte> hex, bool neg)
    {
        long result = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            if (hex[i] == (byte)'_') continue;
            result = (result << 4) | (uint)HexVal(hex[i]);
        }

        return neg ? -result : result;
    }

    private static long ParseBinaryLong(ReadOnlySpan<byte> bin, bool neg)
    {
        long result = 0;
        for (int i = 0; i < bin.Length; i++)
        {
            if (bin[i] == (byte)'_') continue;
            result = (result << 1) | (uint)(bin[i] - (byte)'0');
        }

        return neg ? -result : result;
    }

    private static long ParseOctalLong(ReadOnlySpan<byte> oct, bool neg)
    {
        long result = 0;
        for (int i = 0; i < oct.Length; i++)
        {
            if (oct[i] == (byte)'_') continue;
            result = (result << 3) | (uint)(oct[i] - (byte)'0');
        }

        return neg ? -result : result;
    }

    private static double ParseDoubleValue(ReadOnlySpan<byte> raw)
    {
        ReadOnlySpan<byte> input = StripUnderscores(raw, out Span<byte> buf) ? buf : raw;

        if (Utf8Parser.TryParse(input, out double value, out int consumed) && consumed == input.Length)
            return value;

        return double.Parse(
            Encoding.UTF8.GetString(input),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }

    private static decimal ParseDecimalValue(ReadOnlySpan<byte> raw)
    {
        ReadOnlySpan<byte> input = StripUnderscores(raw, out Span<byte> buf) ? buf : raw;

        if (Utf8Parser.TryParse(input, out decimal value, out int consumed) && consumed == input.Length)
            return value;

        return decimal.Parse(
            Encoding.UTF8.GetString(input),
            NumberStyles.Number,
            CultureInfo.InvariantCulture);
    }

    private static bool StripUnderscores(ReadOnlySpan<byte> raw, out Span<byte> result)
    {
        if (!raw.Contains((byte)'_'))
        {
            result = default;
            return false;
        }

        byte[] buf = new byte[raw.Length];
        int len = 0;
        for (int i = 0; i < raw.Length; i++)
            if (raw[i] != (byte)'_')
                buf[len++] = raw[i];
        result = buf.AsSpan(0, len);
        return true;
    }

    // ──────────────────────── static helpers ────────────────────────

    private static bool IsKeyword(ReadOnlySpan<byte> span) =>
        span.SequenceEqual("true"u8)
        || span.SequenceEqual("false"u8)
        || span.SequenceEqual("nil"u8);

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => 0,
    };

    // ──────────────────────── error helpers ────────────────────────

    [DoesNotReturn]
    private void ThrowSyntax(string message) =>
        throw PaktParseError
            .Syntax(new SourcePosition(_consumed, Line, Column), message)
            .ToException();

    [DoesNotReturn]
    private T ThrowSyntax<T>(string message)
    {
        ThrowSyntax(message);
        return default!;
    }

    [DoesNotReturn]
    private void ThrowNesting() =>
        throw PaktParseError
            .NestingDepthExceeded(
                new SourcePosition(_consumed, Line, Column),
                "Maximum nesting depth exceeded")
            .ToException();

    [DoesNotReturn]
    private static void ThrowUnexpectedEnd(string context) =>
        throw PaktParseError
            .UnexpectedEndOfInput(default, $"Unexpected end of input while reading {context}")
            .ToException();

    [DoesNotReturn]
    private static void ThrowUnexpectedToken(PaktTokenType expected, PaktTokenType actual) =>
        throw new PaktParseException(
            $"Expected {expected}, got {actual}",
            default,
            expected,
            actual);
}
