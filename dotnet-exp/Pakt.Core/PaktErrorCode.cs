namespace Pakt;

public enum PaktErrorCode : byte
{
    None = 0,
    UnexpectedEof = 1,
    TypeMismatch = 2,
    NilNonNullable = 3,
    Syntax = 4,
    ArityMismatch = 8,

    // Implementation-defined.
    NestingDepthExceeded = 100,
    TokenLengthExceeded = 101,
    BufferedBytesExceeded = 102,
}