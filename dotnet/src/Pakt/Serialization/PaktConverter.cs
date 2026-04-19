using System;

namespace Pakt.Serialization;

/// <summary>
/// Non-generic base class for custom PAKT converters.
/// </summary>
public abstract class PaktConverter
{
    internal abstract Type TargetType { get; }

    internal abstract object? ReadAsObject(ref PaktReader reader, PaktType declaredType, PaktConvertContext context);

    internal abstract void WriteAsObject(PaktWriter writer, object? value);
}

/// <summary>
/// Base class for custom PAKT value converters.
/// Converters participate in the stream and read directly from the reader.
/// </summary>
/// <typeparam name="T">The CLR type handled by this converter.</typeparam>
public abstract class PaktConverter<T> : PaktConverter
{
    /// <summary>
    /// Reads a PAKT value from the reader and returns a CLR value.
    /// </summary>
    public abstract T Read(ref PaktReader reader, PaktType declaredType, PaktConvertContext context);

    /// <summary>
    /// Writes a CLR value to the writer.
    /// </summary>
    public abstract void Write(PaktWriter writer, T value);

    internal override Type TargetType => typeof(T);

    internal override object? ReadAsObject(ref PaktReader reader, PaktType declaredType, PaktConvertContext context)
        => Read(ref reader, declaredType, context);

    internal override void WriteAsObject(PaktWriter writer, object? value)
        => Write(writer, (T)value!);
}
