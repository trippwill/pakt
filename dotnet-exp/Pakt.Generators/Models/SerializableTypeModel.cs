namespace Pakt.Generators.Models;

/// <summary>
/// Model of a type registered with <c>[PaktSerializable]</c>.
/// Built by the parser from Roslyn symbols; consumed by the emitter.
/// </summary>
internal sealed class SerializableTypeModel
{
    public required string FullyQualifiedName { get; init; }
    public required string Name { get; init; }
    public required string? Namespace { get; init; }
    public required List<PropertyModel> Properties { get; init; }
    public required bool HasParameterlessConstructor { get; init; }
}
