namespace Pakt.Serialization;

/// <summary>
/// When placed on a partial class that derives from <see cref="PaktSerializerContext"/>,
/// instructs the source generator to produce serialization metadata for <paramref name="type"/>.
/// Multiple attributes may be applied to register multiple types.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class PaktSerializableAttribute : Attribute
{
    /// <summary>Initializes a new instance targeting the specified type.</summary>
    public PaktSerializableAttribute(Type type)
    {
        Type = type;
    }

    /// <summary>The type to generate serialization metadata for.</summary>
    public Type Type { get; }
}

/// <summary>
/// Overrides the PAKT field name for a property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktPropertyAttribute : Attribute
{
    /// <summary>Initializes a new instance with the specified PAKT field name.</summary>
    public PaktPropertyAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The PAKT field name to use instead of the default.</summary>
    public string Name { get; }
}

/// <summary>
/// Declares a property as a PAKT atom set with the given allowed members.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktAtomAttribute : Attribute
{
    /// <summary>Initializes a new instance with the allowed atom members.</summary>
    public PaktAtomAttribute(params string[] members)
    {
        Members = members;
    }

    /// <summary>The allowed atom set members.</summary>
    public string[] Members { get; }
}

/// <summary>
/// Excludes a property from PAKT serialization and deserialization.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktIgnoreAttribute : Attribute
{
}

/// <summary>
/// Overrides the serialization order for a property within a PAKT struct.
/// When used on any property of a type, all properties of that type must also have this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktPropertyOrderAttribute : Attribute
{
    /// <summary>Initializes a new instance with the specified order.</summary>
    public PaktPropertyOrderAttribute(int order)
    {
        Order = order;
    }

    /// <summary>The zero-based serialization order.</summary>
    public int Order { get; }
}

/// <summary>
/// Specifies a custom <see cref="PaktConverter{T}"/> for a property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktConverterAttribute : Attribute
{
    /// <summary>Initializes a new instance with the converter type.</summary>
    public PaktConverterAttribute(Type converterType)
    {
        ConverterType = converterType;
    }

    /// <summary>The converter type, which must derive from <see cref="PaktConverter{T}"/>.</summary>
    public Type ConverterType { get; }
}

/// <summary>
/// Disambiguates a property's scalar PAKT type when the C# type maps to multiple possibilities
/// (e.g., <see cref="DateTimeOffset"/> can be <c>time</c> or <c>datetime</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktScalarAttribute : Attribute
{
    /// <summary>Initializes a new instance with the specified scalar type.</summary>
    public PaktScalarAttribute(PaktScalarType scalarType)
    {
        ScalarType = scalarType;
    }

    /// <summary>The PAKT scalar type to use.</summary>
    public PaktScalarType ScalarType { get; }
}
