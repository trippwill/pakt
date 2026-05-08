using System.Runtime.InteropServices;

namespace Pakt;

[StructLayout(LayoutKind.Auto)]
internal readonly struct PaktTypeNode
{
    public PaktTypeKind Kind { get; init; }

    /// <summary>
    /// For <see cref="PaktTypeKind.List"/>, the type of the list elements.
    /// </summary>
    public PaktTypeRef ElementType { get; init; }

    /// <summary>
    /// For <see cref="PaktTypeKind.Map"/>, the type of the map keys.
    /// </summary>
    public PaktTypeRef KeyType { get; init; }

    /// <summary>
    /// For <see cref="PaktTypeKind.Map"/>, the type of the map values.
    /// </summary>
    public PaktTypeRef ValueType { get; init; }

    /// <summary>
    /// Indicates whether the type is nullable.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// For struct, tuple, and atom-set: index of the first member in the arena's unified member list.
    /// </summary>
    public int FirstMemberIndex { get; init; }

    /// <summary>
    /// For struct, tuple, and atom-set: number of members.
    /// </summary>
    public int MemberCount { get; init; }
}