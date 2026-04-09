namespace Pakt;

/// <summary>
/// Identifies a PAKT scalar type kind.
/// </summary>
public enum PaktScalarType
{
    /// <summary>No scalar type / not a scalar.</summary>
    None = 0,

    /// <summary>Quoted string: <c>str</c></summary>
    Str,

    /// <summary>Signed 64-bit integer: <c>int</c></summary>
    Int,

    /// <summary>Arbitrary-precision decimal: <c>dec</c></summary>
    Dec,

    /// <summary>IEEE 754 binary64 floating point: <c>float</c></summary>
    Float,

    /// <summary>Boolean (true/false): <c>bool</c></summary>
    Bool,

    /// <summary>UUID: <c>uuid</c></summary>
    Uuid,

    /// <summary>ISO 8601 date: <c>date</c></summary>
    Date,

    /// <summary>ISO 8601 timestamp with timezone: <c>ts</c></summary>
    Ts,

    /// <summary>Raw bytes (hex or base64 encoded): <c>bin</c></summary>
    Bin,

    /// <summary>Atom set value: <c>|member1, member2|</c></summary>
    Atom,
}
