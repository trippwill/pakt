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
    public const byte StringDelimiter = (byte)'"';
    public const byte TypeSeparator = (byte)':';

    public static bool IsIdentifierStart(byte b) =>
        (b >= 'a' && b <= 'z')
        || (b >= 'A' && b <= 'Z')
        || b == '_';

    public static bool IsIdentifierPart(byte b) =>
        IsIdentifierStart(b)
        || (b >= '0' && b <= '9')
        || b == '-';
}