using System.Buffers;

namespace Pakt;

public sealed class PaktReaderOptions
{
    public static readonly PaktReaderOptions Default = new();

    public int MaxTokenBytes { get; init; } = 1_048_576;
    public int MaxNestingDepth { get; init; } = 128;
    public int MaxStringLength { get; init; } = 10_485_760;
    public int InitialBufferSize { get; init; } = 4_096;
    public int MaxStatementCount { get; init; } = int.MaxValue;
    public ArrayPool<byte>? BufferPool { get; init; }
}