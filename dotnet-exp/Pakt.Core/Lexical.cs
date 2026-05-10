namespace Pakt;

internal static class Lexical
{
    // Delimiters
    public const byte LBrace = (byte)'{';
    public const byte RBrace = (byte)'}';
    public const byte LParen = (byte)'(';
    public const byte RParen = (byte)')';
    public const byte LBrack = (byte)'[';
    public const byte RBrack = (byte)']';
    public const byte LAngle = (byte)'<';
    public const byte RAngle = (byte)'>';
    public const byte Pipe = (byte)'|';

    // Operators and punctuation
    public const byte Assign = (byte)'=';
    public const byte Colon = (byte)':';
    public const byte TypeAscription = Colon;
    public const byte NullableSuffix = (byte)'?';
    public const byte CommentStart = (byte)'#';
    public const byte Quote = (byte)'\'';
    public const byte Escape = (byte)'\\';

    // Whitespace
    public const byte Space = (byte)' ';
    public const byte Tab = (byte)'\t';
    public const byte Newline = (byte)'\n';
    public const byte CarriageReturn = (byte)'\r';

    // Literal prefixes (before quote)
    public const byte RawPrefix = (byte)'r';
    public const byte HexPrefix = (byte)'x';
    public const byte Base64Prefix = (byte)'b';
    public const byte OctalMarker = (byte)'o';

    // Numeric literal chars
    public const byte Minus = (byte)'-';
    public const byte Plus = (byte)'+';
    public const byte DecimalPoint = (byte)'.';
    public const byte DigitSeparator = (byte)'_';
    public const byte ExponentLower = (byte)'e';
    public const byte ExponentUpper = (byte)'E';

    // ISO 8601 chars
    public const byte DateTimeSep = (byte)'T';
    public const byte UtcMarker = (byte)'Z';

    // Keyword detection
    public const byte NilStart = (byte)'n';

    public static bool IsIdentifierStart(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'A' && b <= (byte)'Z')
        || b == DigitSeparator;

    public static bool IsIdentifierPart(byte b) =>
        IsIdentifierStart(b)
        || IsDigit(b)
        || b == Minus;

    public static bool IsDigit(byte b) =>
        b >= (byte)'0' && b <= (byte)'9';

    public static bool IsHexDigit(byte b) =>
        IsDigit(b)
        || (b >= (byte)'a' && b <= (byte)'f')
        || (b >= (byte)'A' && b <= (byte)'F');

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
}