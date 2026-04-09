namespace Pakt;

/// <summary>
/// Error codes matching the PAKT specification §11.
/// </summary>
public enum PaktErrorCode
{
    /// <summary>No specific error code.</summary>
    None = 0,

    /// <summary>Input ends before a construct is complete.</summary>
    UnexpectedEof = 1,

    /// <summary>Reserved (formerly duplicate_name; see spec §6.1).</summary>
    DuplicateName = 2,

    /// <summary>A value does not conform to its declared type.</summary>
    TypeMismatch = 3,

    /// <summary><c>nil</c> assigned to a non-nullable type.</summary>
    NilNonNullable = 4,

    /// <summary>Lexical or grammatical error (catch-all).</summary>
    Syntax = 5,
}

/// <summary>
/// Exception thrown when PAKT parsing or validation fails.
/// </summary>
public class PaktException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="PaktException"/>.
    /// </summary>
    public PaktException(string message, PaktPosition position, PaktErrorCode code = PaktErrorCode.None)
        : base(FormatMessage(message, position))
    {
        Position = position;
        ErrorCode = code;
    }

    /// <summary>
    /// Initializes a new <see cref="PaktException"/> with an inner exception.
    /// </summary>
    public PaktException(string message, PaktPosition position, PaktErrorCode code, Exception innerException)
        : base(FormatMessage(message, position), innerException)
    {
        Position = position;
        ErrorCode = code;
    }

    /// <summary>The source position where the error occurred.</summary>
    public PaktPosition Position { get; }

    /// <summary>The spec-defined error code, if applicable.</summary>
    public PaktErrorCode ErrorCode { get; }

    private static string FormatMessage(string message, PaktPosition position)
    {
        if (position == PaktPosition.None)
            return message;
        return $"{position}: {message}";
    }
}
