using System.Runtime.InteropServices;

namespace Pakt;

/// <summary>
///  Position within a source file.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct SourcePosition
{
    internal SourcePosition(long offset, int line, long column)
    {
        Offset = offset;
        Line = line;
        Column = column;
    }

    /// <summary>
    /// Gets the byte offset from the start of the source.
    /// </summary>
    public readonly long Offset;

    /// <summary>
    /// Gets the line number (1-based).
    /// </summary>
    public readonly int Line;

    /// <summary>
    /// Gets the column number (1-based).
    /// </summary>
    public readonly long Column;
}

[StructLayout(LayoutKind.Auto)]
internal struct SourceCursor
{
    public long Offset;
    public int Line;
    public long Column;

    public static SourceCursor Start => new()
    {
        Offset = 0,
        Line = 1,
        Column = 1,
    };

    public void Advance(byte b)
    {
        Offset++;
        if (b == '\n')
        {
            Line++;
            Column = 1;
        }
        else
        {
            Column++;
        }
    }

    public readonly SourcePosition ToPosition() => new(Offset, Line, Column);
}