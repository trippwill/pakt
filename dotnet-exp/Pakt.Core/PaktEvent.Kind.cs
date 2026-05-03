namespace Pakt;

public readonly ref partial struct PaktEvent
{
    public enum Kind
    {
        Unknown,
        UnitStart,
        UnitEnd,
        AssignStart,
        AssignEnd,
        PackStart,
        PackEnd,
        StructStart,
        StructField,
        StructEnd,
        TupleStart,
        TupleItem,
        TupleEnd,
        ListStart,
        ListItem,
        ListEnd,
        MapStart,
        MapKey,
        MapValue,
        MapEnd,
        Scalar,
        Atom,
        Nil,
    }
}