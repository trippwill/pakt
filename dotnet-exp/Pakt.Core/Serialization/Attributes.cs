namespace Pakt;

/// <summary>
/// Register a CLR type for source generation on a <see cref="PaktSerializerContext"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class PaktSerializableAttribute : Attribute
{
    public Type Type { get; }
    public PaktSerializableAttribute(Type type) => Type = type;
}

/// <summary>
/// Override the PAKT field name for a property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktPropertyAttribute : Attribute
{
    public string Name { get; }
    public PaktPropertyAttribute(string name) => Name = name;
}

/// <summary>
/// Set explicit field ordering for a property.
/// Must be applied to all or none of a type's serializable properties.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktPropertyOrderAttribute : Attribute
{
    public int Order { get; }
    public PaktPropertyOrderAttribute(int order) => Order = order;
}

/// <summary>
/// Exclude a property from PAKT serialization.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktIgnoreAttribute : Attribute;

/// <summary>
/// Specify a custom converter for a property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PaktConverterAttribute : Attribute
{
    public Type ConverterType { get; }
    public PaktConverterAttribute(Type converterType) => ConverterType = converterType;
}
