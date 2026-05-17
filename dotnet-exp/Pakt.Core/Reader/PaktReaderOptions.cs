using System.Buffers;

namespace Pakt;

public sealed class PaktReaderOptions
{
    public static readonly PaktReaderOptions Default = new();

    public int MaxTokenBytes
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Must be positive");
    } = 1_048_576;

    public int MaxNestingDepth
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Must be positive");
    } = 32;

    public int MaxStringLength
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Must be positive");
    } = 10_485_760;

    public int InitialBufferSize
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Must be positive");
    } = 4_096;

    public int MaxStatementCount { get; init; } = int.MaxValue;
    public ArrayPool<byte>? BufferPool { get; init; }
}