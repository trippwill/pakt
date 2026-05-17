namespace Pakt;

/// <summary>
/// High-level API for PAKT deserialization supporting both synchronous in-memory
/// and asynchronous stream-backed reads via <see cref="PaktPipeSource"/>.
/// </summary>
public static class PaktSerializer
{
    /// <summary>
    /// Deserialize a complete PAKT unit from in-memory data (synchronous fast path).
    /// Uses <see cref="PaktPipeSource.CreateFromMemory"/> internally — the pipe returns
    /// all data on the first read with <c>IsCompleted = true</c>, so the refill loop never runs.
    /// </summary>
    public static T Deserialize<T>(
        ReadOnlyMemory<byte> data,
        PaktSerializerContext context,
        PaktSerializationOptions? options = null)
    {
        PaktTypeInfo<T>? typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"Type {typeof(T).Name} is not registered in the serializer context.");

        if (typeInfo.DeserializeUnitAsync is not { } unitDeserializeAsync)
            throw new NotSupportedException(
                $"No async unit deserializer generated for {typeof(T).Name}.");

        var opts = options ?? context.Options;
        var source = PaktPipeSource.CreateFromMemory(data);
        try
        {
            source.InitialReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            var task = unitDeserializeAsync(source, opts, CancellationToken.None);
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            source.DisposeAsync().GetAwaiter().GetResult();
        }
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
}