using System;

namespace Pakt.Serialization;

/// <summary>
/// Exception thrown when PAKT deserialization fails with additional statement and field context.
/// </summary>
public sealed class PaktDeserializeException : PaktException
{
    private readonly string _message;

    /// <summary>
    /// Initializes a new <see cref="PaktDeserializeException"/>.
    /// </summary>
    public PaktDeserializeException(
        string message,
        PaktPosition position,
        string? statementName = null,
        string? fieldName = null,
        PaktErrorCode code = PaktErrorCode.None)
        : base(message, position, code)
    {
        StatementName = statementName;
        FieldName = fieldName;
        _message = FormatMessage(message, position, statementName, fieldName);
    }

    /// <summary>
    /// Initializes a new <see cref="PaktDeserializeException"/> with an inner exception.
    /// </summary>
    public PaktDeserializeException(
        string message,
        PaktPosition position,
        string? statementName,
        string? fieldName,
        PaktErrorCode code,
        Exception innerException)
        : base(message, position, code, innerException)
    {
        StatementName = statementName;
        FieldName = fieldName;
        _message = FormatMessage(message, position, statementName, fieldName);
    }

    /// <summary>The top-level statement name, when known.</summary>
    public string? StatementName { get; }

    /// <summary>The field path within the current statement, when known.</summary>
    public string? FieldName { get; }

    /// <inheritdoc />
    public override string Message => _message;

    private static string FormatMessage(
        string message,
        PaktPosition position,
        string? statementName,
        string? fieldName)
    {
        string? path = null;
        if (!string.IsNullOrEmpty(statementName) && !string.IsNullOrEmpty(fieldName))
            path = $"{statementName}.{fieldName}";
        else if (!string.IsNullOrEmpty(statementName))
            path = statementName;
        else if (!string.IsNullOrEmpty(fieldName))
            path = fieldName;

        var prefix = string.IsNullOrEmpty(path) ? "" : $"{path} ";
        return position == PaktPosition.None
            ? $"{prefix}{message}".Trim()
            : $"{prefix}({position}): {message}".Trim();
    }
}
