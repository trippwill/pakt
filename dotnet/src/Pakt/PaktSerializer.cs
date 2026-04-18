using System.Reflection;
using System.Buffers;
using Pakt.Serialization;

namespace Pakt;

/// <summary>
/// Convenience API for whole-unit materialization and whole-unit serialization.
/// </summary>
public static class PaktSerializer
{
    /// <summary>
    /// Deserializes a complete PAKT unit from borrowed memory into <typeparamref name="T"/>.
    /// </summary>
    public static T Deserialize<T>(
        ReadOnlyMemory<byte> data,
        PaktSerializerContext context,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var reader = PaktMemoryReader.Create(data, context, options);
        return PaktUnitMaterializer.Materialize<T>(reader, context, options ?? context.Options);
    }

    /// <summary>
    /// Deserializes a complete PAKT unit from owned memory into <typeparamref name="T"/>.
    /// </summary>
    public static T Deserialize<T>(
        IMemoryOwner<byte> owner,
        int length,
        PaktSerializerContext context,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var reader = PaktMemoryReader.Create(owner, length, context, options);
        return PaktUnitMaterializer.Materialize<T>(reader, context, options ?? context.Options);
    }

    /// <summary>
     /// Serialize a CLR object to a PAKT unit using its registered property metadata.
      /// </summary>
      /// <typeparam name="T">The type to serialize.</typeparam>
      /// <param name="value">The value to serialize.</param>
      /// <param name="context">Serializer context with registered types.</param>
     /// <param name="statementName">Unused legacy parameter.</param>
      /// <returns>UTF-8 encoded PAKT unit bytes.</returns>
    public static byte[] Serialize<T>(T value, PaktSerializerContext context, string statementName = "value")
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(context);
        _ = statementName;

        var typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered in the serializer context.");

        var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(static property => property.Name, StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);

        foreach (var property in typeInfo.Properties.Where(static property => !property.IsIgnored))
        {
            if (!properties.TryGetValue(property.ClrName, out var clrProperty))
                continue;

            writer.WriteAssignmentStart(property.PaktName, property.PaktType);
            PaktSerializationRuntime.WriteObject(
                writer,
                clrProperty.GetValue(value),
                clrProperty.PropertyType,
                property.PaktType,
                context,
                context.Options,
                property.ConverterType);
            writer.WriteAssignmentEnd();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
