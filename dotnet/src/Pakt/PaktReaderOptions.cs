namespace Pakt;

/// <summary>
/// Options for configuring <see cref="PaktReader"/> behavior.
/// </summary>
public readonly struct PaktReaderOptions
{
    /// <summary>Default reader options.</summary>
    public static readonly PaktReaderOptions Default = new();

    /// <summary>
    /// Maximum nesting depth for composite types. Default is 64.
    /// </summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>
    /// Initializes default <see cref="PaktReaderOptions"/>.
    /// </summary>
    public PaktReaderOptions() { }
}
