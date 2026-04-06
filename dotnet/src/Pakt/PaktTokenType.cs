namespace Pakt;

/// <summary>
/// Identifies the type of token read by <see cref="PaktReader"/>.
/// </summary>
public enum PaktTokenType
{
    /// <summary>No token has been read yet.</summary>
    None = 0,

    /// <summary>Start of a top-level assignment: <c>name:type =</c></summary>
    AssignStart,

    /// <summary>End of a top-level assignment.</summary>
    AssignEnd,

    /// <summary>Start of a top-level stream: <c>name:type &lt;&lt;</c></summary>
    StreamStart,

    /// <summary>End of a top-level stream.</summary>
    StreamEnd,

    /// <summary>A scalar value (string, int, decimal, float, bool, uuid, date, time, datetime, bin, atom).</summary>
    ScalarValue,

    /// <summary>A nil value for a nullable type.</summary>
    Nil,

    /// <summary>Start of a struct composite: <c>{</c></summary>
    StructStart,

    /// <summary>End of a struct composite: <c>}</c></summary>
    StructEnd,

    /// <summary>Start of a tuple composite: <c>(</c></summary>
    TupleStart,

    /// <summary>End of a tuple composite: <c>)</c></summary>
    TupleEnd,

    /// <summary>Start of a list composite: <c>[</c></summary>
    ListStart,

    /// <summary>End of a list composite: <c>]</c></summary>
    ListEnd,

    /// <summary>Start of a map composite: <c>&lt;</c></summary>
    MapStart,

    /// <summary>End of a map composite: <c>&gt;</c></summary>
    MapEnd,

    /// <summary>A comment token (only emitted when comments are requested).</summary>
    Comment,
}
