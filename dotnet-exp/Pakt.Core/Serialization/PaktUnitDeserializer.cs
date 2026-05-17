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
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>();
        if (typeInfo is null)
            throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        var opts = options ?? context.Options;

        // Prefer raw path — no annotation parsing overhead
        if (typeInfo.RawDeserializeUnit is { } rawDeserialize)
        {
            var seq = new ReadOnlySequence<byte>(data);
            var rawReader = new PaktSequenceReader(seq, isFinalBlock: true);
            return rawDeserialize(ref rawReader, opts);
        }

        // Fallback to validating reader
        var reader = new PaktValidatingReader(data);
        return DeserializeCoreValidating<T>(ref reader, typeInfo, opts);
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
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>();
        if (typeInfo is null)
            throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        var opts = options ?? context.Options;

        if (typeInfo.RawDeserializeUnit is { } rawDeserialize)
        {
            var rawReader = new PaktSequenceReader(data, isFinalBlock);
            return rawDeserialize(ref rawReader, opts);
        }

        var reader = new PaktValidatingReader(data, isFinalBlock);
        return DeserializeCoreValidating<T>(ref reader, typeInfo, opts);
    }

    /// <summary>
    /// Deserialize from an already-constructed validating reader.
    /// </summary>
    public static T Deserialize<T>(
        ref PaktValidatingReader reader,
        PaktSerializerContext context,
        PaktSerializationOptions? options = null)
    {
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>();
        if (typeInfo is null)
            throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        return DeserializeCoreValidating<T>(ref reader, typeInfo, options ?? context.Options);
    }

    private static T DeserializeCoreValidating<T>(
        ref PaktValidatingReader reader,
        PaktTypeInfo<T> typeInfo,
        PaktSerializationOptions options)
    {
        if (typeInfo.DeserializeUnit is { } unitDeserialize)
            return unitDeserialize(ref reader, options);

        // Fallback: use properties metadata for runtime statement matching
        return DeserializeWithProperties(ref reader, typeInfo,
            null! /* context not available in this path */, options);
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
                return;
            }

            if (token is PaktTokenType.EndOfUnit or PaktTokenType.StatementName)
                return;
        }
    }

    /// <summary>
    /// Skip the current statement's value using the raw sequence reader.
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