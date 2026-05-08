namespace Pakt;

internal static class Lexical
{
    public enum Delimiter
    {
        Brace,
        Paren,
        Bracket,
        Angle,
        Pipe,
    }

    public const byte CommentStart = (byte)'#';
    public const byte TypeSeparator = (byte)':';
    public const byte Assign = (byte)'=';
    public const byte Newline = (byte)'\n';
    public const byte CarriageReturn = (byte)'\r';
    public const byte Space = (byte)' ';
    public const byte Tab = (byte)'\t';
    public const byte Pipe = (byte)'|';
    public const byte QuestionMark = (byte)'?';

    public static bool IsIdentifierStart(byte b) =>
        (b >= 'a' && b <= 'z')
        || (b >= 'A' && b <= 'Z')
        || b == '_';

    public static bool IsIdentifierPart(byte b) =>
        IsIdentifierStart(b)
        || (b >= '0' && b <= '9')
        || b == '-';

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