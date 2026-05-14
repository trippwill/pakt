namespace Pakt;

/// <summary>
/// Runtime deserialization policies applied at the unit level.
/// Struct field arity is enforced by the reader regardless of these options.
/// </summary>
public sealed class PaktSerializationOptions
{
    public static readonly PaktSerializationOptions Default = new();

    /// <summary>
    /// What to do when a unit contains a statement the context doesn't know about.
    /// </summary>
    public UnknownMemberPolicy UnknownStatements { get; init; } = UnknownMemberPolicy.Skip;

    /// <summary>
    /// What to do when an expected root statement is absent from the unit.
    /// </summary>
    public MissingMemberPolicy MissingStatements { get; init; } = MissingMemberPolicy.UseDefault;

    /// <summary>
    /// How to handle duplicate root statement names.
    /// </summary>
    public DuplicatePolicy DuplicateStatements { get; init; } = DuplicatePolicy.LastWins;

    /// <summary>
    /// How to handle duplicate map keys within a map value or map pack.
    /// </summary>
    public DuplicatePolicy DuplicateMapKeys { get; init; } = DuplicatePolicy.LastWins;
}

public enum UnknownMemberPolicy : byte
{
    Skip,
    Error,
}

public enum MissingMemberPolicy : byte
{
    UseDefault,
    Error,
}

public enum DuplicatePolicy : byte
{
    FirstWins,
    LastWins,
    Error,
}
