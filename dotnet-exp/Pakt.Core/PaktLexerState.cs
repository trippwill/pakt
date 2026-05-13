using System.Runtime.InteropServices;

namespace Pakt;

/// <summary>
/// Persistent state for <see cref="PaktLexer"/> across buffer refills.
/// Passed by reference to the lexer constructor; the lexer updates it on every
/// <see cref="PaktLexer.Read"/> return.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct PaktLexerState
{
    internal long TotalConsumed;
    internal int Line;
    internal int Column;
    internal LexerMode Mode;
    internal byte EscapeSubstate;
    internal byte PendingQuoteCount;
    internal int PartialTokenStart;
}

/// <summary>
/// Tracks what the lexer is in the middle of when a buffer boundary is hit.
/// </summary>
internal enum LexerMode : byte
{
    Normal,
    InString,
    InRawString,
    InMultiLine,
    InRawMultiLine,
    InBinary,
}