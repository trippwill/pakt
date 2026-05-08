namespace Pakt;

/// <summary>
/// Event stream contract:
/// <code>
/// StatementStart(name)
///   → type events (StructTypeStart, FieldDecl, ScalarType, NullableModifier, etc.)
///   → AssignStart | PackStart
///   → value events (StructValueStart, ScalarValue, etc.)
///   → AssignEnd | PackEnd
/// </code>
/// NullableModifier is postfix: it modifies the immediately preceding completed type.
/// For scalars: <c>ScalarType → NullableModifier</c>.
/// For composites: <c>XTypeStart ... XTypeEnd → NullableModifier</c>.
/// MapTypeStart/End children are positional: first child is key type, second is value type.
/// </summary>
public readonly ref partial struct PaktEvent
{
    public enum Kind
    {
        // — Framing —
        UnitStart,
        UnitEnd,
        StatementStart,
        AssignStart,
        AssignEnd,
        PackStart,
        PackEnd,

        // — Type annotation (emitted during pending type event drain) —
        ScalarType,
        StructTypeStart,
        StructTypeEnd,
        FieldDecl,
        TupleTypeStart,
        TupleTypeEnd,
        ElementDecl,
        ListTypeStart,
        ListTypeEnd,
        MapTypeStart,
        MapTypeEnd,
        AtomSetStart,
        AtomSetEnd,
        AtomDecl,
        NullableModifier,

        // — Value —
        ScalarValue,
        AtomValue,
        NilValue,
        StructValueStart,
        StructValueEnd,
        TupleValueStart,
        TupleValueEnd,
        ListValueStart,
        ListValueEnd,
        MapValueStart,
        MapValueEnd,
        MapEntryStart,
        MapEntryEnd,
    }
}