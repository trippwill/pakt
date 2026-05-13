using System.Runtime.InteropServices;

namespace Pakt;

/// <summary>
/// A lexical token produced by <see cref="PaktLexer"/>.
/// The span covers the full lexeme including delimiters (quotes, prefixes).
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct PaktLexicalToken
{
    internal PaktLexicalToken(PaktLexicalTokenKind kind, int offset, int length)
    {
        Kind = kind;
        Offset = offset;
        Length = length;
    }

    /// <summary>Gets the token kind.</summary>
    public PaktLexicalTokenKind Kind { get; }

    /// <summary>Gets the byte offset of the token start within the buffer.</summary>
    public int Offset { get; }

    /// <summary>Gets the byte length of the token.</summary>
    public int Length { get; }
}

/// <summary>
/// Lexical token classification. The lexer does NOT interpret keywords
/// (<c>true</c>, <c>false</c>, <c>nil</c>) — they are <see cref="Ident"/> tokens.
/// </summary>
public enum PaktLexicalTokenKind : byte
{
    Ident,
    String,
    Number,
    Binary,
    AtomPrefix,
    Colon,
    Assign,
    Pack,
    Bind,
    Nullable,
    LBrace,
    RBrace,
    LParen,
    RParen,
    LBrack,
    RBrack,
    LAngle,
    RAngle,
    Pipe,
    Semicolon,
    Eof,
    Nul,
}

/// <summary>
/// Result of a <see cref="PaktLexer.Read"/> call.
/// </summary>
public enum PaktReadResult : byte
{
    /// <summary>A complete token was produced.</summary>
    Token,

    /// <summary>Buffer exhausted mid-token; caller should refill and retry.</summary>
    NeedMoreData,

    /// <summary>No more tokens; end of the final block.</summary>
    EndOfInput,
}