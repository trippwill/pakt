namespace Pakt;

/// <summary>
/// Classifies a node in the validation type tree.
/// </summary>
internal enum ValidationNodeKind : byte
{
    /// <summary>A scalar type (str, int, dec, float, bool, uuid, date, ts, bin).</summary>
    Scalar,

    /// <summary>An atom set (|ident ident ...|).</summary>
    AtomSet,

    /// <summary>A struct type ({field:type ...}).</summary>
    Struct,

    /// <summary>A tuple type ((type ...)).</summary>
    Tuple,

    /// <summary>A list type ([type]).</summary>
    List,

    /// <summary>A map type (&lt;keytype =&gt; valtype&gt;).</summary>
    Map,
}

/// <summary>
/// A node in the flat validation type tree built from a type annotation.
/// Nodes are stored in a contiguous array; children are referenced via the
/// child index table (not necessarily contiguous in the node array).
/// </summary>
internal readonly struct ValidationNode
{
    /// <summary>The kind of type this node represents.</summary>
    public readonly ValidationNodeKind Kind;

    /// <summary>For <see cref="ValidationNodeKind.Scalar"/>: the expected token type.</summary>
    public readonly PaktTokenType ExpectedToken;

    /// <summary>Whether this type is nullable (has '?' suffix).</summary>
    public readonly bool IsNullable;

    /// <summary>Start index in the child index table for this node's children.</summary>
    public readonly int ChildStart;

    /// <summary>Number of child nodes (struct fields, tuple elements, list element=1, map key+value=2).</summary>
    public readonly int ChildCount;

    /// <summary>
    /// For <see cref="ValidationNodeKind.AtomSet"/>: start index in the member table.
    /// For <see cref="ValidationNodeKind.Struct"/>: start index in the field name table.
    /// </summary>
    public readonly int MemberStart;

    /// <summary>
    /// For <see cref="ValidationNodeKind.AtomSet"/>: number of atom members.
    /// For <see cref="ValidationNodeKind.Struct"/>: number of field names (== ChildCount).
    /// </summary>
    public readonly int MemberCount;

    public ValidationNode(
        ValidationNodeKind kind,
        PaktTokenType expectedToken,
        bool isNullable,
        int childStart,
        int childCount,
        int memberStart = 0,
        int memberCount = 0)
    {
        Kind = kind;
        ExpectedToken = expectedToken;
        IsNullable = isNullable;
        ChildStart = childStart;
        ChildCount = childCount;
        MemberStart = memberStart;
        MemberCount = memberCount;
    }
}

/// <summary>
/// A byte range referencing a slice of the annotation bytes.
/// Used for atom set members and struct field names.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
internal readonly struct ByteRange
{
    public readonly int Start;
    public readonly int Length;

    public ByteRange(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> source) => source.Slice(Start, Length);
}