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
    /// The statement's value is deserialized using the generated type info.
    /// </summary>
    /// <typeparam name="T">The type to deserialize into.</typeparam>
    /// <param name="data">UTF-8 encoded PAKT unit bytes.</param>
    /// <param name="typeInfo">Source-generated type info with Deserialize delegate.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="InvalidOperationException">If typeInfo lacks a Deserialize delegate.</exception>
    /// <exception cref="PaktException">If the unit is malformed.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> data, PaktTypeInfo<T> typeInfo)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Deserialize is null)
            throw new InvalidOperationException($"PaktTypeInfo<{typeof(T).Name}> does not have a Deserialize delegate. Use source-generated type info.");

        var reader = new PaktReader(data);
        try
        {
            if (!reader.Read())
                throw new PaktException("Empty unit", PaktPosition.None, PaktErrorCode.UnexpectedEof);

            if (reader.TokenType == PaktTokenType.AssignStart)
            {
                reader.Read(); // Value start (StructStart, etc.)
                return typeInfo.Deserialize(ref reader);
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
    /// <param name="typeInfo">Source-generated type info with Serialize delegate.</param>
    /// <param name="statementName">The name for the top-level statement.</param>
    /// <returns>UTF-8 encoded PAKT unit bytes.</returns>
    /// <exception cref="InvalidOperationException">If typeInfo lacks a Serialize delegate.</exception>
    public static byte[] Serialize<T>(T value, PaktTypeInfo<T> typeInfo, string statementName = "value")
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Serialize is null)
            throw new InvalidOperationException($"PaktTypeInfo<{typeof(T).Name}> does not have a Serialize delegate. Use source-generated type info.");

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart(statementName, typeInfo.PaktType);
        typeInfo.Serialize(writer, value);
        writer.WriteAssignmentEnd();
        return buffer.WrittenSpan.ToArray();
    }
}
