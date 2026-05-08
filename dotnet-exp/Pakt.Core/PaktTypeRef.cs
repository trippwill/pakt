namespace Pakt;

/// <summary>
/// Reference to a parsed Pakt type.
/// </summary>
/// <param name="Id"> Gets the Id of the type. </param>
public readonly record struct PaktTypeRef
{
    internal PaktTypeRef(int id)
    {
        Id = id;
    }

    public readonly int Id;

    public bool IsDefault => Id == 0;
}