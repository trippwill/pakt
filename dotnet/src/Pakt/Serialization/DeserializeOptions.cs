namespace Pakt.Serialization;

/// <summary>
/// Options for configuring PAKT deserialization behavior.
/// </summary>
public sealed class DeserializeOptions
{
    /// <summary>Default options instance.</summary>
    public static DeserializeOptions Default { get; } = new();

    /// <summary>
    /// How to handle unknown fields in PAKT data.
    /// </summary>
    public UnknownFieldPolicy UnknownFields { get; init; } = UnknownFieldPolicy.Skip;

    /// <summary>
    /// How to handle missing fields in the target CLR type.
    /// </summary>
    public MissingFieldPolicy MissingFields { get; init; } = MissingFieldPolicy.ZeroValue;

    /// <summary>
    /// How to handle duplicate names in materialized targets.
    /// </summary>
    public DuplicatePolicy Duplicates { get; init; } = DuplicatePolicy.LastWins;

    /// <summary>
    /// Custom converters registered by target CLR type.
    /// </summary>
    public IList<object> Converters { get; } = new List<object>();
}

/// <summary>
/// Controls handling for unknown fields or statements.
/// </summary>
public enum UnknownFieldPolicy
{
    /// <summary>Silently skip unknown fields.</summary>
    Skip,

    /// <summary>Treat unknown fields as an error.</summary>
    Error,
}

/// <summary>
/// Controls handling for missing fields in the target CLR type.
/// </summary>
public enum MissingFieldPolicy
{
    /// <summary>Leave missing fields at their zero value.</summary>
    ZeroValue,

    /// <summary>Treat missing fields as an error.</summary>
    Error,
}

/// <summary>
/// Controls handling for duplicate names when materializing CLR targets.
/// </summary>
public enum DuplicatePolicy
{
    /// <summary>Later values replace earlier values.</summary>
    LastWins,

    /// <summary>Later values are ignored.</summary>
    FirstWins,

    /// <summary>Duplicate names are an error.</summary>
    Error,

    /// <summary>Duplicate values are accumulated into a collection target.</summary>
    Accumulate,
}
