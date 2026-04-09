using System.Buffers;
using Pakt.Serialization;

namespace Pakt;

/// <summary>
/// Convenience API for serializing and deserializing single PAKT assign values.
/// For multi-statement units, use <see cref="PaktStreamReader"/>.
/// </summary>
public static class PaktSerializer
{
    /// <summary>
    /// Deserialize a PAKT unit containing a single assign statement.
    /// </summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="data">UTF-8 encoded PAKT unit bytes.</param>
    /// <param name="context">Serializer context with registered types.</param>
    /// <returns>The deserialized value.</returns>
    public static T Deserialize<T>(ReadOnlySpan<byte> data, PaktSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered in the serializer context.");

        var reader = new PaktReader(data);
        try
        {
            if (!reader.Read())
                throw new PaktException("Empty unit", PaktPosition.None, PaktErrorCode.UnexpectedEof);

            if (reader.TokenType == PaktTokenType.AssignStart)
            {
                reader.Read(); // Value start (StructStart, etc.)
                var result = typeInfo.Deserialize!(ref reader);

                while (reader.Read())
                {
                    if (reader.TokenType == PaktTokenType.AssignEnd)
                        break;
                }

                return result;
            }

            throw new PaktException($"Expected AssignStart, got {reader.TokenType}", reader.Position, PaktErrorCode.Syntax);
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// Serialize a value to PAKT bytes as a single assign statement.
    /// </summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="context">Serializer context with registered types.</param>
    /// <param name="statementName">The name for the top-level statement.</param>
    /// <returns>UTF-8 encoded PAKT unit bytes.</returns>
    public static byte[] Serialize<T>(T value, PaktSerializerContext context, string statementName = "value")
    {
        ArgumentNullException.ThrowIfNull(context);
        var typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException($"Type '{typeof(T).Name}' is not registered in the serializer context.");

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart(statementName, typeInfo.PaktType);
        typeInfo.Serialize!(writer, value);
        writer.WriteAssignmentEnd();
        return buffer.WrittenSpan.ToArray();
    }
}
