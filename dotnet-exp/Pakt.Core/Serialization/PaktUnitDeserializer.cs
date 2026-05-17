using System.Buffers;
using System.Text;

namespace Pakt;

/// <summary>
/// Deserializes a PAKT unit into a CLR object using the generated
/// <see cref="PaktSerializerContext"/> for type resolution and the
/// raw <see cref="PaktSequenceReader"/> for zero-overhead reading.
/// </summary>
public static class PaktUnitDeserializer
{
    /// <summary>
    /// Deserialize a complete PAKT unit from memory.
    /// </summary>
    public static T Deserialize<T>(
        ReadOnlyMemory<byte> data,
        PaktSerializerContext context,
        PaktSerializationOptions? options = null)
    {
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
        return DeserializeCore<T>(ref reader, context, options ?? context.Options);
    }

    /// <summary>
    /// Deserialize from a <see cref="ReadOnlySequence{T}"/>.
    /// </summary>
    public static T Deserialize<T>(
        ReadOnlySequence<byte> data,
        PaktSerializerContext context,
        bool isFinalBlock = true,
        PaktSerializationOptions? options = null)
    {
        var reader = new PaktSequenceReader(data, isFinalBlock);
        return DeserializeCore<T>(ref reader, context, options ?? context.Options);
    }

    private static T DeserializeCore<T>(
        ref PaktSequenceReader reader,
        PaktSerializerContext context,
        PaktSerializationOptions options)
    {
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>();
        if (typeInfo is null)
            throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        if (typeInfo.DeserializeUnit is { } unitDeserialize)
            return unitDeserialize(ref reader, options);

        throw new NotSupportedException(
            $"No unit deserializer generated for {typeof(T).Name}.");
    }

    /// <summary>
    /// Skip the current statement's value (everything after the operator token).
    /// </summary>
    public static void SkipStatementValue(ref PaktSequenceReader reader)
    {
        int depth = 0;
        while (reader.Read())
        {
            PaktTokenType token = reader.TokenType;

            if (token is PaktTokenType.StructStart or PaktTokenType.TupleStart
                or PaktTokenType.ListStart or PaktTokenType.MapStart)
            {
                depth++;
            }
            else if (token is PaktTokenType.StructEnd or PaktTokenType.TupleEnd
                or PaktTokenType.ListEnd or PaktTokenType.MapEnd)
            {
                depth--;
                if (depth <= 0) return;
            }
            else if (depth == 0)
            {
                return;
            }

            if (token is PaktTokenType.EndOfUnit or PaktTokenType.StatementName)
                return;
        }
    }
}
