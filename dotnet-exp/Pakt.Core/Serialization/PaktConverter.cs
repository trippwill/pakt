namespace Pakt;

/// <summary>
/// Base class for custom PAKT type converters.
/// Converters participate in the reader/writer stream directly — no intermediate DOM.
/// </summary>
public abstract class PaktConverter
{
    /// <summary>The CLR type this converter handles.</summary>
    public abstract Type TargetType { get; }

    internal abstract object? ReadObject(ref PaktValidatingReader reader, PaktConvertContext context);
}

/// <summary>
/// Strongly-typed converter for <typeparamref name="T"/>.
/// Implement this to provide custom deserialization for a specific type.
/// </summary>
/// <typeparam name="T">The CLR type to convert.</typeparam>
public abstract class PaktConverter<T> : PaktConverter
{
    public sealed override Type TargetType => typeof(T);

    /// <summary>
    /// Read a value of type <typeparamref name="T"/> from the reader.
    /// The reader is positioned at the first value token (after statement header).
    /// </summary>
    public abstract T Read(ref PaktValidatingReader reader, PaktConvertContext context);

    internal sealed override object? ReadObject(ref PaktValidatingReader reader, PaktConvertContext context)
        => Read(ref reader, context);
}