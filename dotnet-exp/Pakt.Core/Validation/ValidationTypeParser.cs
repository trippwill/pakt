using System.Runtime.CompilerServices;

namespace Pakt;

/// <summary>
/// Parses a PAKT type annotation (the bytes between statement name colon and operator)
/// into a flat <see cref="ValidationNode"/> array with associated member tables.
/// </summary>
internal ref struct ValidationTypeParser
{
    private readonly ReadOnlySpan<byte> _annotation;
    private int _pos;
    private readonly int _maxDepth;

    // Growable output buffers
    private ValidationNode[] _nodes;
    private int _nodeCount;
    private ByteRange[] _members;
    private int _memberCount;
    private int[] _childIndices;
    private int _childIndexCount;

    private ValidationTypeParser(ReadOnlySpan<byte> annotation, int maxDepth)
    {
        _annotation = annotation;
        _pos = 0;
        _maxDepth = maxDepth;
        _nodes = new ValidationNode[8];
        _nodeCount = 0;
        _members = new ByteRange[8];
        _memberCount = 0;
        _childIndices = new int[8];
        _childIndexCount = 0;
    }

    /// <summary>
    /// Parse a type annotation into a validation tree.
    /// </summary>
    /// <param name="annotation">The raw annotation bytes (trimmed of surrounding layout).</param>
    /// <param name="maxDepth">Maximum nesting depth for bounded recursion.</param>
    /// <param name="nodes">The resulting flat node array.</param>
    /// <param name="members">Byte ranges for atom members and struct field names.</param>
    /// <param name="childIndices">Child node index table for composites.</param>
    /// <returns>The index of the root node.</returns>
    public static int Parse(
        ReadOnlySpan<byte> annotation,
        int maxDepth,
        out ValidationNode[] nodes,
        out ByteRange[] members,
        out int[] childIndices)
    {
        var parser = new ValidationTypeParser(annotation, maxDepth);
        int root = parser.ParseType(0);
        nodes = parser._nodes[..parser._nodeCount];
        members = parser._members[..parser._memberCount];
        childIndices = parser._childIndices[..parser._childIndexCount];
        return root;
    }

    /// <summary>
    /// Overload without childIndices for backward compatibility in tests.
    /// </summary>
    public static int Parse(
        ReadOnlySpan<byte> annotation,
        int maxDepth,
        out ValidationNode[] nodes,
        out ByteRange[] members)
    {
        return Parse(annotation, maxDepth, out nodes, out members, out _);
    }

    private int ParseType(int depth)
    {
        if (depth > _maxDepth)
            ThrowNestingExceeded();

        SkipLayout();

        if (_pos >= _annotation.Length)
            ThrowUnexpectedEnd("type");

        byte b = _annotation[_pos];

        int nodeIndex;
        if (b == PaktConstants.Pipe)
            nodeIndex = ParseAtomSet();
        else if (b == PaktConstants.LBrace)
            nodeIndex = ParseStructType(depth);
        else if (b == PaktConstants.LParen)
            nodeIndex = ParseTupleType(depth);
        else if (b == PaktConstants.LBrack)
            nodeIndex = ParseListType(depth);
        else if (b == PaktConstants.LAngle)
            nodeIndex = ParseMapType(depth);
        else if (PaktConstants.IsIdentStart(b))
            nodeIndex = ParseScalarType();
        else
        {
            ThrowSyntax($"Unexpected byte 0x{b:X2} in type position");
            return -1; // unreachable
        }

        // Check nullable suffix
        SkipLayout();
        if (_pos < _annotation.Length && _annotation[_pos] == PaktConstants.Question)
        {
            _pos++;
            ref ValidationNode node = ref _nodes[nodeIndex];
            node = new ValidationNode(
                node.Kind, node.ExpectedToken, isNullable: true,
                node.ChildStart, node.ChildCount, node.MemberStart, node.MemberCount);
        }

        return nodeIndex;
    }

    private int ParseScalarType()
    {
        int start = _pos;
        while (_pos < _annotation.Length && PaktConstants.IsIdentPart(_annotation[_pos]))
            _pos++;

        ReadOnlySpan<byte> name = _annotation[start.._pos];
        PaktTokenType expected = MatchScalarType(name);

        return AddNode(new ValidationNode(
            ValidationNodeKind.Scalar, expected, isNullable: false,
            childStart: 0, childCount: 0));
    }

    private static PaktTokenType MatchScalarType(ReadOnlySpan<byte> name)
    {
        // Ordered by expected frequency
        if (name.SequenceEqual("str"u8)) return PaktTokenType.String;
        if (name.SequenceEqual("int"u8)) return PaktTokenType.Int;
        if (name.SequenceEqual("bool"u8)) return PaktTokenType.Bool;
        if (name.SequenceEqual("dec"u8)) return PaktTokenType.Decimal;
        if (name.SequenceEqual("float"u8)) return PaktTokenType.Float;
        if (name.SequenceEqual("uuid"u8)) return PaktTokenType.Uuid;
        if (name.SequenceEqual("date"u8)) return PaktTokenType.Date;
        if (name.SequenceEqual("ts"u8)) return PaktTokenType.Timestamp;
        if (name.SequenceEqual("bin"u8)) return PaktTokenType.Binary;

        ThrowUnknownType(name);
        return default; // unreachable
    }

    private int ParseAtomSet()
    {
        _pos++; // skip opening |
        SkipLayout();

        int memberStart = _memberCount;
        int memberCount = 0;

        while (_pos < _annotation.Length && _annotation[_pos] != PaktConstants.Pipe)
        {
            SkipLayout();
            if (_pos >= _annotation.Length || _annotation[_pos] == PaktConstants.Pipe)
                break;

            int identStart = _pos;
            if (!PaktConstants.IsIdentStart(_annotation[_pos]))
                ThrowSyntax("Expected identifier in atom set");

            while (_pos < _annotation.Length && PaktConstants.IsIdentPart(_annotation[_pos]))
                _pos++;

            int identLen = _pos - identStart;
            ReadOnlySpan<byte> ident = _annotation.Slice(identStart, identLen);

            // Check reserved keywords
            if (ident.SequenceEqual("true"u8) || ident.SequenceEqual("false"u8) || ident.SequenceEqual("nil"u8))
                ThrowSyntax("Reserved keyword in atom set");

            // Check for duplicates within this atom set
            ReadOnlySpan<byte> annoSpan = _annotation;
            for (int i = memberStart; i < _memberCount; i++)
            {
                if (_members[i].Slice(annoSpan).SequenceEqual(ident))
                    ThrowSyntax("Duplicate atom member");
            }

            AddMember(new ByteRange(identStart, identLen));
            memberCount++;
        }

        if (memberCount == 0)
            ThrowSyntax("Empty atom set");

        if (_pos >= _annotation.Length)
            ThrowUnexpectedEnd("atom set closing |");

        _pos++; // skip closing |

        return AddNode(new ValidationNode(
            ValidationNodeKind.AtomSet, PaktTokenType.Atom, isNullable: false,
            childStart: 0, childCount: 0,
            memberStart: memberStart, memberCount: memberCount));
    }

    private int ParseStructType(int depth)
    {
        _pos++; // skip {
        SkipLayout();

        int memberStart = _memberCount;
        int fieldCount = 0;

        // Collect child indices temporarily (they may be interleaved with
        // grandchildren in the main table during recursive parsing)
        Span<int> tempChildren = stackalloc int[64];

        // Reserve node slot — we'll fill it after parsing children
        int structIndex = AddNode(default);

        while (_pos < _annotation.Length && _annotation[_pos] != PaktConstants.RBrace)
        {
            SkipLayout();
            if (_pos >= _annotation.Length || _annotation[_pos] == PaktConstants.RBrace)
                break;

            // Parse field name
            int nameStart = _pos;
            if (!PaktConstants.IsIdentStart(_annotation[_pos]))
                ThrowSyntax("Expected field name in struct type");

            while (_pos < _annotation.Length && PaktConstants.IsIdentPart(_annotation[_pos]))
                _pos++;

            int nameLen = _pos - nameStart;

            // Expect colon
            if (_pos >= _annotation.Length || _annotation[_pos] != PaktConstants.Colon)
                ThrowSyntax("Expected ':' after struct field name");
            _pos++; // skip :

            // Check for duplicate field names
            ReadOnlySpan<byte> fieldName = _annotation.Slice(nameStart, nameLen);
            ReadOnlySpan<byte> annoSpan = _annotation;
            for (int i = memberStart; i < _memberCount; i++)
            {
                if (_members[i].Slice(annoSpan).SequenceEqual(fieldName))
                    ThrowSyntax("Duplicate struct field name");
            }

            AddMember(new ByteRange(nameStart, nameLen));

            // Parse field type and collect its node index
            if (fieldCount >= tempChildren.Length)
                ThrowNestingExceeded();
            tempChildren[fieldCount] = ParseType(depth + 1);
            fieldCount++;

            SkipLayout();
        }

        if (_pos >= _annotation.Length)
            ThrowUnexpectedEnd("struct type closing }");

        _pos++; // skip }

        // Now add child indices contiguously at the end
        int childTableStart = _childIndexCount;
        for (int i = 0; i < fieldCount; i++)
            AddChildIndex(tempChildren[i]);

        _nodes[structIndex] = new ValidationNode(
            ValidationNodeKind.Struct, PaktTokenType.StructStart, isNullable: false,
            childStart: childTableStart, childCount: fieldCount,
            memberStart: memberStart, memberCount: fieldCount);

        return structIndex;
    }

    private int ParseTupleType(int depth)
    {
        _pos++; // skip (
        SkipLayout();

        int elemCount = 0;
        Span<int> tempChildren = stackalloc int[64];

        int tupleIndex = AddNode(default);

        while (_pos < _annotation.Length && _annotation[_pos] != PaktConstants.RParen)
        {
            SkipLayout();
            if (_pos >= _annotation.Length || _annotation[_pos] == PaktConstants.RParen)
                break;

            if (elemCount >= tempChildren.Length)
                ThrowNestingExceeded();
            tempChildren[elemCount] = ParseType(depth + 1);
            elemCount++;

            SkipLayout();
        }

        if (_pos >= _annotation.Length)
            ThrowUnexpectedEnd("tuple type closing )");

        _pos++; // skip )

        int childTableStart = _childIndexCount;
        for (int i = 0; i < elemCount; i++)
            AddChildIndex(tempChildren[i]);

        _nodes[tupleIndex] = new ValidationNode(
            ValidationNodeKind.Tuple, PaktTokenType.TupleStart, isNullable: false,
            childStart: childTableStart, childCount: elemCount);

        return tupleIndex;
    }

    private int ParseListType(int depth)
    {
        _pos++; // skip [
        SkipLayout();

        int listIndex = AddNode(default);
        int elementIdx = -1;

        if (_pos < _annotation.Length && _annotation[_pos] != PaktConstants.RBrack)
        {
            elementIdx = ParseType(depth + 1);
        }

        SkipLayout();

        if (_pos >= _annotation.Length)
            ThrowUnexpectedEnd("list type closing ]");

        _pos++; // skip ]

        int childTableStart = _childIndexCount;
        AddChildIndex(elementIdx);

        _nodes[listIndex] = new ValidationNode(
            ValidationNodeKind.List, PaktTokenType.ListStart, isNullable: false,
            childStart: childTableStart, childCount: 1);

        return listIndex;
    }

    private int ParseMapType(int depth)
    {
        _pos++; // skip <
        SkipLayout();

        int mapIndex = AddNode(default);

        // Key type
        if (_pos >= _annotation.Length || _annotation[_pos] == PaktConstants.RAngle)
        {
            ThrowSyntax("Map type requires key and value types");
        }

        int keyIdx = ParseType(depth + 1);

        SkipLayout();

        // Expect '=>' bind operator
        if (_pos + 1 >= _annotation.Length
            || _annotation[_pos] != PaktConstants.EqualsSign
            || _annotation[_pos + 1] != PaktConstants.RAngle)
        {
            ThrowSyntax("Expected '=>' in map type");
        }
        _pos += 2; // skip =>

        SkipLayout();

        // Value type
        int valIdx = ParseType(depth + 1);

        SkipLayout();

        if (_pos >= _annotation.Length)
            ThrowUnexpectedEnd("map type closing >");

        if (_annotation[_pos] != PaktConstants.RAngle)
            ThrowSyntax("Expected '>' closing map type");

        _pos++; // skip >

        int childTableStart = _childIndexCount;
        AddChildIndex(keyIdx);
        AddChildIndex(valIdx);

        _nodes[mapIndex] = new ValidationNode(
            ValidationNodeKind.Map, PaktTokenType.MapStart, isNullable: false,
            childStart: childTableStart, childCount: 2);

        return mapIndex;
    }

    // ── Helpers ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipLayout()
    {
        while (_pos < _annotation.Length && PaktConstants.IsLayout(_annotation[_pos]))
            _pos++;
    }

    private int AddNode(ValidationNode node)
    {
        if (_nodeCount == _nodes.Length)
            Array.Resize(ref _nodes, _nodes.Length * 2);

        int index = _nodeCount++;
        _nodes[index] = node;
        return index;
    }

    private void AddMember(ByteRange range)
    {
        if (_memberCount == _members.Length)
            Array.Resize(ref _members, _members.Length * 2);

        _members[_memberCount++] = range;
    }

    private void AddChildIndex(int nodeIndex)
    {
        if (_childIndexCount == _childIndices.Length)
            Array.Resize(ref _childIndices, _childIndices.Length * 2);

        _childIndices[_childIndexCount++] = nodeIndex;
    }

    // ── Error Helpers ──

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowSyntax(string message) =>
        throw PaktParseError.Syntax(default, $"Type annotation: {message} at position {_pos}").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowUnexpectedEnd(string context) =>
        throw PaktParseError.UnexpectedEndOfInput(default, $"Type annotation: unexpected end in {context}").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowNestingExceeded() =>
        throw PaktParseError.NestingDepthExceeded(default, "Type annotation nesting depth exceeded").ToException();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnknownType(ReadOnlySpan<byte> name) =>
        throw PaktParseError.Syntax(default,
            $"Type annotation: unknown scalar type '{System.Text.Encoding.UTF8.GetString(name)}'").ToException();
}