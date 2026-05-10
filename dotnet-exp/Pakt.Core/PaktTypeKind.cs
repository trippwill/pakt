namespace Pakt;

public enum PaktTypeKind : byte
{
    None = 0,
    String = 1,
    Int = 2,
    Decimal = 3,
    Float = 4,
    Bool = 5,
    Uuid = 6,
    Date = 7,
    Timestamp = 8,
    Binary = 9,
    AtomSet = 10,
    Struct = 11,
    Tuple = 12,
    List = 13,
    Map = 14,
    Alias = 15,
}

public static class PaktTypeKindExtensions
{
    /// <summary>
    /// Determines whether the specified type kind is a primitive type.
    /// </summary>
    /// <param name="kind">The type kind to check.</param>
    /// <returns><langword>true</langword> if the specified type kind is a scalar type; otherwise, <langword>false</langword>.</returns>
    public static bool IsScalar(this PaktTypeKind kind)
    {
        // Scalar types are those that can be represented as a single value in the payload.
        // This includes all types from String to Binary (inclusive).
        // AtomSet, Struct, Tuple, List, Map, and Alias are not considered scalar.
        return kind >= PaktTypeKind.String && kind <= PaktTypeKind.Binary;
    }
}
