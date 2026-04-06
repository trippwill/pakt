using System.Collections.Immutable;

namespace Pakt;

/// <summary>
/// Represents a PAKT type annotation. A type is either a scalar, an atom set, or a composite
/// (struct, tuple, list, map). Any type may be nullable.
/// </summary>
public sealed class PaktType : IEquatable<PaktType>
{
    private PaktType() { }

    /// <summary>The scalar kind, if this is a scalar type.</summary>
    public PaktScalarType ScalarKind { get; private init; }

    /// <summary>Atom set members, if this is an atom set type.</summary>
    public ImmutableArray<string> AtomMembers { get; private init; }

    /// <summary>Struct fields (name→type), if this is a struct type.</summary>
    public ImmutableArray<PaktField> StructFields { get; private init; }

    /// <summary>Tuple element types, if this is a tuple type.</summary>
    public ImmutableArray<PaktType> TupleElements { get; private init; }

    /// <summary>List element type, if this is a list type.</summary>
    public PaktType? ListElement { get; private init; }

    /// <summary>Map key type, if this is a map type.</summary>
    public PaktType? MapKey { get; private init; }

    /// <summary>Map value type, if this is a map type.</summary>
    public PaktType? MapValue { get; private init; }

    /// <summary>Whether this type is nullable (trailing <c>?</c>).</summary>
    public bool IsNullable { get; private init; }

    /// <summary>True if this is a scalar type.</summary>
    public bool IsScalar => ScalarKind != PaktScalarType.None;

    /// <summary>True if this is an atom set type.</summary>
    public bool IsAtomSet => !AtomMembers.IsDefaultOrEmpty;

    /// <summary>True if this is a struct type.</summary>
    public bool IsStruct => !StructFields.IsDefaultOrEmpty;

    /// <summary>True if this is a tuple type.</summary>
    public bool IsTuple => !TupleElements.IsDefaultOrEmpty;

    /// <summary>True if this is a list type.</summary>
    public bool IsList => ListElement is not null;

    /// <summary>True if this is a map type.</summary>
    public bool IsMap => MapKey is not null;

    /// <summary>Creates a scalar type.</summary>
    public static PaktType Scalar(PaktScalarType kind, bool nullable = false) => new()
    {
        ScalarKind = kind,
        IsNullable = nullable,
    };

    /// <summary>Creates an atom set type.</summary>
    public static PaktType AtomSet(ImmutableArray<string> members, bool nullable = false) => new()
    {
        ScalarKind = PaktScalarType.Atom,
        AtomMembers = members,
        IsNullable = nullable,
    };

    /// <summary>Creates a struct type.</summary>
    public static PaktType Struct(ImmutableArray<PaktField> fields, bool nullable = false) => new()
    {
        StructFields = fields,
        IsNullable = nullable,
    };

    /// <summary>Creates a tuple type.</summary>
    public static PaktType Tuple(ImmutableArray<PaktType> elements, bool nullable = false) => new()
    {
        TupleElements = elements,
        IsNullable = nullable,
    };

    /// <summary>Creates a list type.</summary>
    public static PaktType List(PaktType element, bool nullable = false) => new()
    {
        ListElement = element,
        IsNullable = nullable,
    };

    /// <summary>Creates a map type.</summary>
    public static PaktType Map(PaktType key, PaktType value, bool nullable = false) => new()
    {
        MapKey = key,
        MapValue = value,
        IsNullable = nullable,
    };

    /// <summary>Returns the PAKT type annotation string.</summary>
    public override string ToString()
    {
        var suffix = IsNullable ? "?" : "";

        if (IsScalar && !IsAtomSet)
            return ScalarKind.ToString().ToLowerInvariant() + suffix;

        if (IsAtomSet)
            return $"|{string.Join(", ", AtomMembers)}|{suffix}";

        if (IsStruct)
            return $"{{{string.Join(", ", StructFields.Select(f => $"{f.Name}:{f.Type}"))}}}{suffix}";

        if (IsTuple)
            return $"({string.Join(", ", TupleElements)}){suffix}";

        if (IsList)
            return $"[{ListElement}]{suffix}";

        if (IsMap)
            return $"<{MapKey} ; {MapValue}>{suffix}";

        return "unknown" + suffix;
    }

    /// <inheritdoc/>
    public bool Equals(PaktType? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ScalarKind == other.ScalarKind
            && IsNullable == other.IsNullable
            && SequenceEqual(AtomMembers, other.AtomMembers)
            && SequenceEqual(StructFields, other.StructFields)
            && SequenceEqual(TupleElements, other.TupleElements)
            && Equals(ListElement, other.ListElement)
            && Equals(MapKey, other.MapKey)
            && Equals(MapValue, other.MapValue);
    }

    private static bool SequenceEqual<T>(ImmutableArray<T> a, ImmutableArray<T> b) where T : IEquatable<T>
    {
        if (a.IsDefault && b.IsDefault) return true;
        if (a.IsDefault || b.IsDefault) return false;
        return a.SequenceEqual(b);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PaktType);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ScalarKind);
        hash.Add(IsNullable);
        hash.Add(ListElement);
        hash.Add(MapKey);
        hash.Add(MapValue);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A named field within a PAKT struct type.
/// </summary>
/// <param name="Name">Field name.</param>
/// <param name="Type">Field type.</param>
public readonly record struct PaktField(string Name, PaktType Type);
