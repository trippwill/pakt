namespace Pakt.Serialization;

/// <summary>
/// Abstract base class for custom PAKT type converters.
/// Implement this to handle types not covered by built-in conventions.
/// </summary>
/// <typeparam name="T">The type to convert.</typeparam>
public abstract class PaktConverter<T>
{
    // TODO: Uncomment when PaktReader and PaktWriter are implemented.
    // /// <summary>Reads a value of type <typeparamref name="T"/> from the reader.</summary>
    // public abstract T Read(ref PaktReader reader);

    // /// <summary>Writes a value of type <typeparamref name="T"/> to the writer.</summary>
    // public abstract void Write(PaktWriter writer, T value);
}
