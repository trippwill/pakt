using System.Buffers;

namespace Pakt;

/// <summary>
/// UTF-8 byte constants and pre-compiled search sets for the PAKT lexer.
/// </summary>
internal static class PaktConstants
{
    // ── Layout ──
    public const byte Space = 0x20;
    public const byte Tab = 0x09;
    public const byte LF = 0x0A;
    public const byte CR = 0x0D;
    public const byte Comma = 0x2C;
    public const byte Hash = 0x23;

    // ── Paired delimiters ──
    public const byte LBrace = 0x7B;
    public const byte RBrace = 0x7D;
    public const byte LParen = 0x28;
    public const byte RParen = 0x29;
    public const byte LBrack = 0x5B;
    public const byte RBrack = 0x5D;
    public const byte LAngle = 0x3C;
    public const byte RAngle = 0x3E;

    // ── Operators and punctuation ──
    public const byte Colon = 0x3A;
    public const byte EqualsSign = 0x3D;
    public const byte Pipe = 0x7C;
    public const byte Question = 0x3F;
    public const byte Semicolon = 0x3B;
    public const byte SingleQuote = 0x27;
    public const byte Backslash = 0x5C;
    public const byte Nul = 0x00;

    // ── Letters used in literal syntax ──
    public const byte LowerR = (byte)'r';
    public const byte LowerX = (byte)'x';
    public const byte LowerB = (byte)'b';
    public const byte LowerE = (byte)'e';
    public const byte UpperE = (byte)'E';
    public const byte Minus = (byte)'-';
    public const byte Plus = (byte)'+';
    public const byte Dot = (byte)'.';
    public const byte Underscore = (byte)'_';

    // ── SIMD search sets (for string interior scanning — long runs) ──

    /// <summary>
    /// Stop bytes inside a single-quoted, non-raw string body.
    /// Includes quote, backslash, newline, CR, and NUL.
    /// </summary>
    public static readonly SearchValues<byte> StringStopBytes =
        SearchValues.Create("'\\\n\r\0"u8);

    /// <summary>
    /// Stop bytes inside a raw string body (no backslash escape).
    /// </summary>
    public static readonly SearchValues<byte> RawStringStopBytes =
        SearchValues.Create("'\0"u8);

    // ── Value terminators (for number/ident boundary detection) ──

    /// <summary>Bytes that end a number or identifier token.</summary>
    public static ReadOnlySpan<byte> Delimiters => " \t\r\n,{}()[]<>|:=?';#\0"u8;

    // ── Character classification (inline, branch-free) ──

    public static bool IsWhitespace(byte b) => b is Space or Tab or Comma;

    public static bool IsLayout(byte b) => b is Space or Tab or LF or CR or Comma;

    public static bool IsDigit(byte b) => (uint)(b - (byte)'0') <= 9;

    public static bool IsIdentStart(byte b) =>
        (uint)(b - (byte)'a') <= 25
        || (uint)(b - (byte)'A') <= 25
        || b == Underscore;

    public static bool IsIdentPart(byte b) =>
        IsIdentStart(b) || IsDigit(b) || b == Minus;
}