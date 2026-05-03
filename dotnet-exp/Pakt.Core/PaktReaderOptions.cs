namespace Pakt;

public class PaktReaderOptions
{
    public const int DefaultMaxTokenBytes = 1024 * 1024;

    public const int DefaultMaxNestingDepth = 128;

    public static readonly PaktReaderOptions Default = new();

    public int MaxTokenBytes { get; init; } = DefaultMaxTokenBytes;

    public int MaxNestingDepth { get; init; } = DefaultMaxNestingDepth;
}