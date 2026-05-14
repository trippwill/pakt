namespace Pakt.Generators.Models;

internal sealed class PropertyModel
{
    public required string ClrName { get; init; }
    public required string PaktName { get; init; }
    public required string ClrTypeFqn { get; init; }
    public required PaktTypeKind Kind { get; init; }
    public required bool IsNullable { get; init; }
    public required int Order { get; init; }
    public required bool IsIgnored { get; init; }

    // For collections
    public string? ElementTypeFqn { get; init; }
    public string? KeyTypeFqn { get; init; }
    public string? ValueTypeFqn { get; init; }

    // For nested types
    public string? NestedTypeFqn { get; init; }

    // For converters
    public string? ConverterTypeFqn { get; init; }
}

internal enum PaktTypeKind
{
    String,
    Int,
    Long,
    Decimal,
    Double,
    Float,
    Bool,
    Guid,
    DateOnly,
    DateTimeOffset,
    ByteArray,
    Struct,
    Tuple,
    List,
    Map,
    Atom,
}
