namespace Pakt.Serialization;

/// <summary>
/// Options for configuring PAKT serialization and deserialization behavior.
/// </summary>
public sealed class PaktSerializerOptions
{
    /// <summary>Default options instance.</summary>
    public static PaktSerializerOptions Default { get; } = new();

    /// <summary>
    /// Global converters applied to all types. Property-level
    /// <see cref="PaktConverterAttribute"/> takes precedence.
    /// </summary>
    public IList<object> Converters { get; } = new List<object>();
}
