namespace Pakt;

/// <summary>
/// Options for configuring <see cref="PaktWriter"/> behavior.
/// </summary>
public readonly struct PaktWriterOptions
{
    /// <summary>Default writer options.</summary>
    public static readonly PaktWriterOptions Default = new();

    /// <summary>
    /// Initializes default <see cref="PaktWriterOptions"/>.
    /// </summary>
    public PaktWriterOptions() { }
}
