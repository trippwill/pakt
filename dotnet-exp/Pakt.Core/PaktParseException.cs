namespace Pakt;

public sealed class PaktParseException : Exception
{
    public PaktParseException(string message, int code, SourcePosition position)
        : base(message)
    {
        Code = code;
        Identifier = GetErrorIdentifier((PaktErrorCode)code);
        Position = position;
    }

    public int Code { get; internal init; }

    public string Identifier { get; internal init; }

    public SourcePosition Position { get; internal init; }

    public static string GetErrorIdentifier(PaktErrorCode code) => code switch
    {
        PaktErrorCode.UnexpectedEof => "unexpected_eof",
        PaktErrorCode.TypeMismatch => "type_mismatch",
        PaktErrorCode.NilNonNullable => "nil_non_nullable",
        PaktErrorCode.Syntax => "syntax_error",
        PaktErrorCode.MissingLayout => "missing_layout",
        PaktErrorCode.ReservedToken => "reserved_token",
        PaktErrorCode.InvalidHeader => "invalid_header",
        PaktErrorCode.ArityMismatch => "arity_mismatch",
        PaktErrorCode.NestingDepthExceeded => "nesting_depth_exceeded",
        PaktErrorCode.TokenLengthExceeded => "token_length_exceeded",
        PaktErrorCode.BufferedBytesExceeded => "buffered_bytes_exceeded",
        _ => "unknown_error",
    };
}

internal readonly record struct PaktParseError(
    PaktErrorCode Code,
    SourcePosition Position,
    string? Message = null
)
{
    public PaktParseException ToException() =>
        new(Message ?? Code.ToString(), (int)Code, Position);

    public static PaktParseError UnexpectedEndOfInput(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.UnexpectedEof, position, msg);

    public static PaktParseError TypeMismatch(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.TypeMismatch, position, msg);

    public static PaktParseError NilNonNullable(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.NilNonNullable, position, msg);

    public static PaktParseError Syntax(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.Syntax, position, msg);

    public static PaktParseError MissingLayout(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.MissingLayout, position, msg);

    public static PaktParseError ReservedToken(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.ReservedToken, position, msg);

    public static PaktParseError InvalidHeader(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.InvalidHeader, position, msg);

    public static PaktParseError ArityMismatch(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.ArityMismatch, position, msg);

    public static PaktParseError NestingDepthExceeded(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.NestingDepthExceeded, position, msg);

    public static PaktParseError TokenLengthExceeded(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.TokenLengthExceeded, position, msg);

    public static PaktParseError BufferedBytesExceeded(SourcePosition position, string? msg = null) =>
        new(PaktErrorCode.BufferedBytesExceeded, position, msg);
}