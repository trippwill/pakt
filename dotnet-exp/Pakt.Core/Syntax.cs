using System.Runtime.InteropServices;

namespace Pakt;

/// <summary>
/// Grammatical tokens mapped to their byte representation.
/// Single-byte tokens are <c>const byte</c>. Multi-byte tokens
/// (digraphs) are represented as <see cref="Digraph"/>.
/// </summary>
internal static class Syntax
{
    // Statement structure
    public const byte TypeAscription = Lexical.Colon;
    public const byte AssignOp = Lexical.Assign;
    public const byte NullableModifier = Lexical.NullableSuffix;

    // Value prefixes
    public const byte AtomValuePrefix = Lexical.Pipe;
    public const byte RawStringPrefix = Lexical.RawPrefix;
    public const byte HexBinaryPrefix = Lexical.HexPrefix;
    public const byte Base64BinaryPrefix = Lexical.Base64Prefix;
    public const byte NilKeywordStart = Lexical.NilStart;
    public const byte StringOpen = Lexical.Quote;

    // Composite type/value delimiters
    public const byte StructOpen = Lexical.LBrace;
    public const byte StructClose = Lexical.RBrace;
    public const byte TupleOpen = Lexical.LParen;
    public const byte TupleClose = Lexical.RParen;
    public const byte ListOpen = Lexical.LBrack;
    public const byte ListClose = Lexical.RBrack;
    public const byte MapOpen = Lexical.LAngle;
    public const byte MapClose = Lexical.RAngle;
    public const byte AtomSetOpen = Lexical.Pipe;
    public const byte AtomSetClose = Lexical.Pipe;

    // Digraphs
    public static readonly Digraph PackOp = new(Lexical.LAngle, Lexical.LAngle);
    public static readonly Digraph MapBind = new(Lexical.Assign, Lexical.RAngle);
}

/// <summary>
/// A two-byte token. Value type, no allocation.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct Digraph(byte first, byte second)
{
    public readonly byte First = first;
    public readonly byte Second = second;
}
