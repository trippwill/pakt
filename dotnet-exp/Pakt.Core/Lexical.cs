using System.Buffers;

namespace Pakt;

/// <summary>
/// UTF-8 byte constants and character classification methods.
/// Names describe what the byte IS, not what it means in PAKT grammar.
/// For grammatical meaning, see <see cref="Syntax"/>.
/// </summary>
internal static class Lexical
{
    // ── Pre-compiled SIMD-optimized byte search sets (.NET 8+) ──

    internal static readonly SearchValues<byte> LayoutBytes =
        SearchValues.Create(" \t\r\n"u8);

    internal static readonly SearchValues<byte> NonNewlineLayoutBytes =
        SearchValues.Create(" \t"u8);

    internal static readonly SearchValues<byte> NewlineBytes =
        SearchValues.Create("\r\n"u8);

    internal static readonly SearchValues<byte> IdentStartBytes =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_"u8);

    internal static readonly SearchValues<byte> IdentPartBytes =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789-"u8);

    // Paired delimiters
    public const byte LBrace = (byte)'{';
    public const byte RBrace = (byte)'}';
    public const byte LParen = (byte)'(';
    public const byte RParen = (byte)')';
    public const byte LBrack = (byte)'[';
    public const byte RBrack = (byte)']';
    public const byte LAngle = (byte)'<';
    public const byte RAngle = (byte)'>';
    public const byte Pipe = (byte)'|';
    public const byte SingleQuote = (byte)'\'';

    // Punctuation
    public const byte EqualsSign = (byte)'=';
    public const byte Colon = (byte)':';
    public const byte Hash = (byte)'#';
    public const byte Question = (byte)'?';
    public const byte Backslash = (byte)'\\';
    public const byte Nul = 0x00;

    // Whitespace
    public const byte Space = (byte)' ';
    public const byte Tab = (byte)'\t';
    public const byte Newline = (byte)'\n';
    public const byte CarriageReturn = (byte)'\r';

    // Letters used in literal syntax
    public const byte LowerR = (byte)'r';
    public const byte LowerX = (byte)'x';
    public const byte LowerB = (byte)'b';
    public const byte LowerO = (byte)'o';
    public const byte LowerE = (byte)'e';
    public const byte UpperE = (byte)'E';
    public const byte LowerN = (byte)'n';
    public const byte UpperT = (byte)'T';
    public const byte UpperZ = (byte)'Z';

    // Arithmetic / numeric
    public const byte Minus = (byte)'-';
    public const byte Plus = (byte)'+';
    public const byte Dot = (byte)'.';
    public const byte Underscore = (byte)'_';

    // Range check optimization: (uint)(b - lo) <= (hi - lo)
    // Lowers to a single sub + unsigned compare (no branch).
    // If b < lo, the subtraction underflows to a large uint that fails the <= check.
    //
    // Case-fold optimization: (b | 0x20) maps 'A'-'Z' to 'a'-'z',
    // collapsing two range checks into one.

    /// <summary>
    /// IDENT_START = 'a'-'z' | 'A'-'Z' | '_'
    /// </summary>
    public static bool IsIdentifierStart(byte b) =>
        (uint)(b - (byte)'a') <= (byte)'z' - (byte)'a'     // a-z: one sub + compare
        || (uint)(b - (byte)'A') <= (byte)'Z' - (byte)'A'  // A-Z: one sub + compare
        || b == Underscore;

    /// <summary>
    /// IDENT_PART = IDENT_START | DIGIT | '-'
    /// </summary>
    public static bool IsIdentifierPart(byte b) =>
        IsIdentifierStart(b)
        || IsDigit(b)
        || b == Minus;

    /// <summary>
    /// DIGIT = '0'-'9'
    /// </summary>
    public static bool IsDigit(byte b) =>
        (uint)(b - (byte)'0') <= 9;  // one sub + compare

    /// <summary>
    /// HEX_DIGIT = DIGIT | 'a'-'f' | 'A'-'F'
    /// </summary>
    public static bool IsHexDigit(byte b) =>
        IsDigit(b)
        || (uint)((b | 0x20) - (byte)'a') <= 5;  // case-fold: one OR + sub + compare

    /// <summary>
    /// WS = ' ' | '\t'
    /// </summary>
    public static bool IsWhitespace(byte b) =>
        b == Space || b == Tab;

    /// <summary>
    /// LAYOUT_CHAR = WS | NL
    /// </summary>
    public static bool IsLayoutChar(byte b) =>
        IsWhitespace(b) || b == Newline || b == CarriageReturn;

    // §3.3 scalar literal character classes

    /// <summary>
    /// INT = [-] DIGIT_SEP | [-] '0x' HEX+ | [-] '0b' BIN+ | [-] '0o' OCT+
    /// </summary>
    public static bool IsIntChar(byte b) =>
        IsHexDigit(b)
        || b == Minus || b == Underscore
        || b == LowerX || b == LowerO || b == LowerB;

    /// <summary>
    /// DEC = [-] DIGIT_SEP? '.' DIGIT_SEP
    /// </summary>
    public static bool IsDecChar(byte b) =>
        IsDigit(b)
        || b == Minus || b == Underscore || b == Dot;

    /// <summary>
    /// FLOAT = [-] DIGIT_SEP? ('.' DIGIT_SEP)? ('e'|'E') [+-]? DIGIT+
    /// </summary>
    public static bool IsFloatChar(byte b) =>
        IsDigit(b)
        || b == Minus || b == Plus || b == Underscore
        || b == Dot || b == LowerE || b == UpperE;

    /// <summary>
    /// DATE = DIGIT{4}-DIGIT{2}-DIGIT{2}
    /// </summary>
    public static bool IsDateChar(byte b) =>
        IsDigit(b) || b == Minus;

    /// <summary>
    /// TS = DATE 'T' time TZ — digits, -, T, :, ., Z, +
    /// </summary>
    public static bool IsTsChar(byte b) =>
        IsDigit(b)
        || b == Minus || b == Plus || b == Colon
        || b == UpperT || b == UpperZ || b == Dot;

    /// <summary>
    /// UUID = HEX{8}-HEX{4}-HEX{4}-HEX{4}-HEX{12}
    /// </summary>
    public static bool IsUuidChar(byte b) =>
        IsHexDigit(b) || b == Minus;
}