using System.Buffers;
using System.Text;

namespace Pakt;

/// <summary>
/// Deserializes a PAKT unit into a CLR object using the generated
/// <see cref="PaktSerializerContext"/> for type resolution and the
/// <see cref="PaktValidatingReader"/> for type-enforced reading.
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
        var reader = new PaktValidatingReader(data);
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
        var reader = new PaktValidatingReader(data, isFinalBlock);
        return DeserializeCore<T>(ref reader, context, options ?? context.Options);
    }

    /// <summary>
    /// Deserialize from an already-constructed reader.
    /// </summary>
    public static T Deserialize<T>(
        ref PaktValidatingReader reader,
        PaktSerializerContext context,
        PaktSerializationOptions? options = null)
    {
        return DeserializeCore<T>(ref reader, context, options ?? context.Options);
    }

    private static T DeserializeCore<T>(
        ref PaktValidatingReader reader,
        PaktSerializerContext context,
        PaktSerializationOptions options)
    {
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>();
        if (typeInfo is null)
            throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        // If there's a generated unit-level deserializer, use it directly
        if (typeInfo.DeserializeUnit is { } unitDeserialize)
            return unitDeserialize(ref reader, options);

        // Fallback: use properties metadata for runtime statement matching
        return DeserializeWithProperties(ref reader, typeInfo, context, options);
    }

    /// <summary>
    /// Runtime statement-matching deserializer. Reads statement-by-statement,
    /// matches names to <see cref="PaktPropertyInfo"/>, and uses the generated
    /// <see cref="PaktDeserializeFunc{T}"/> for value reading.
    /// </summary>
    private static T DeserializeWithProperties<T>(
        ref PaktValidatingReader reader,
        PaktTypeInfo<T> typeInfo,
        PaktSerializerContext context,
        PaktSerializationOptions options)
    {
        // This is a fallback path. The preferred path is having the source
        // generator emit a DeserializeUnit delegate that handles everything.
        // For now, throw — the source generator should always provide DeserializeUnit.
        throw new NotSupportedException(
            $"Runtime statement matching is not yet implemented for {typeof(T).Name}. " +
            $"Ensure the source generator emits a DeserializeUnit delegate.");
    }

    /// <summary>
    /// Skip the current statement's value (everything after the operator token).
    /// </summary>
    public static void SkipStatementValue(ref PaktValidatingReader reader)
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
                // Scalar value at top level — statement value consumed
                return;
            }

            if (token is PaktTokenType.EndOfUnit or PaktTokenType.StatementName)
                return; // shouldn't happen in well-formed input, but be safe
        }
    }
}