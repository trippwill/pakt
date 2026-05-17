using System.Buffers;

namespace Pakt;

/// <summary>
/// High-level API for PAKT serialization and deserialization.
/// <para>
/// <see cref="Deserialize{T}(ReadOnlyMemory{byte}, PaktSerializerContext, PaktSerializationOptions?)"/>
/// is the synchronous in-memory fast path using the zero-overhead <c>ref PaktReader</c> delegate.
/// </para>
/// <para>
/// <see cref="DeserializeAsync{T}(Stream, PaktSerializerContext, PaktSerializationOptions?, int, CancellationToken)"/>
/// is the asynchronous stream-backed path using <see cref="PaktPipeSource"/> with automatic
/// buffer management via <see cref="System.IO.Pipelines.PipeReader"/>.
/// </para>
/// </summary>
public static class PaktSerializer
{
    /// <summary>
    /// Deserialize a complete PAKT unit from in-memory data (synchronous fast path).
    /// Uses the zero-overhead <c>ref PaktReader</c> delegate directly.
    /// </summary>
    public static T Deserialize<T>(
        ReadOnlyMemory<byte> data,
        PaktSerializerContext context,
        PaktSerializationOptions? options = null)
    {
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        if (typeInfo.DeserializeUnit is not { } unitDeserialize)
            throw new NotSupportedException(
                $"No unit deserializer generated for {typeof(T).Name}.");

        var opts = options ?? context.Options;
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new PaktReader(seq, isFinalBlock: true);
        return unitDeserialize(ref reader, opts);
    }

    /// <summary>
    /// Deserialize a PAKT unit from a <see cref="Stream"/> asynchronously.
    /// Uses <see cref="PaktPipeSource"/> with <see cref="System.IO.Pipelines.PipeReader"/>
    /// for automatic buffer management, compaction, and memory pooling.
    /// </summary>
    public static async ValueTask<T> DeserializeAsync<T>(
        Stream stream,
        PaktSerializerContext context,
        PaktSerializationOptions? options = null,
        int bufferSize = 16384,
        CancellationToken ct = default)
    {
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        if (typeInfo.DeserializeUnitAsync is not { } unitDeserializeAsync)
            throw new NotSupportedException(
                $"No async unit deserializer generated for {typeof(T).Name}.");

        var opts = options ?? context.Options;
        var source = PaktPipeSource.Create(stream, bufferSize: bufferSize);
        try
        {
            await source.InitialReadAsync(ct).ConfigureAwait(false);
            return await unitDeserializeAsync(source, opts, ct).ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Skip the current statement's value (everything after the operator token).
    /// Used by generated sync deserializers for unknown/duplicate statement handling.
    /// </summary>
    public static void SkipStatementValue(ref PaktReader reader)
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