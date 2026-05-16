namespace Pakt;

public enum PaktTokenType : byte
{
    None,

    // Statement framing
    StatementName,
    TypeAnnotationStart, TypeAnnotationEnd,
    AssignOperator,

    // Scalar values
    String, Int, Decimal, Float, Bool,
    Date, Timestamp, Uuid, Binary,
    Atom, Nil,

    // Composite structure
    StructStart, StructEnd,
    TupleStart, TupleEnd,
    ListStart, ListEnd,
    MapStart, MapEnd,
    MapEntryBind,

    // Unit
    EndOfUnit,
}