namespace Pakt;

/// <summary>
/// Represents a position in PAKT source text.
/// </summary>
/// <param name="Line">1-based line number.</param>
/// <param name="Column">1-based column number.</param>
public readonly record struct PaktPosition(int Line, int Column)
{
    /// <summary>A position representing no known location.</summary>
    public static readonly PaktPosition None = new(0, 0);

    /// <inheritdoc/>
    public override string ToString() => $"{Line}:{Column}";
}
