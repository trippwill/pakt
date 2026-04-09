namespace Pakt.Serialization;

/// <summary>
/// Metadata for a single property within a serializable type.
/// </summary>
public sealed class PaktPropertyInfo
{
    /// <summary>
    /// Initializes a new <see cref="PaktPropertyInfo"/>.
    /// </summary>
    public PaktPropertyInfo(
        string clrName,
        string paktName,
        PaktType paktType,
        int order,
        bool isIgnored = false)
    {
        ClrName = clrName;
        PaktName = paktName;
        PaktType = paktType;
        Order = order;
        IsIgnored = isIgnored;
    }

    /// <summary>The C# property name.</summary>
    public string ClrName { get; }

    /// <summary>The PAKT field name (from convention or <see cref="PaktPropertyAttribute"/>).</summary>
    public string PaktName { get; }

    /// <summary>The PAKT type for this property.</summary>
    public PaktType PaktType { get; }

    /// <summary>Serialization order (from declaration order or <see cref="PaktPropertyOrderAttribute"/>).</summary>
    public int Order { get; }

    /// <summary>Whether this property is excluded from serialization.</summary>
    public bool IsIgnored { get; }
}
