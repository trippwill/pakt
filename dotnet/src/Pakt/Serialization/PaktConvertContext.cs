using System;

namespace Pakt.Serialization;

/// <summary>
/// Provides deserialization context to custom converters.
/// Enables converters to delegate child value deserialization back to the framework.
/// </summary>
public readonly ref struct PaktConvertContext
{
    private readonly PaktSerializerContext _serializerContext;
    private readonly DeserializeOptions _options;
    private readonly string? _statementName;
    private readonly string? _fieldPath;

    internal PaktConvertContext(
        PaktSerializerContext serializerContext,
        DeserializeOptions options,
        string? statementName,
        string? fieldPath)
    {
        _serializerContext = serializerContext;
        _options = options;
        _statementName = statementName;
        _fieldPath = fieldPath;
    }

    /// <summary>Gets the serializer context used for metadata lookup.</summary>
    public PaktSerializerContext SerializerContext => _serializerContext;

    /// <summary>Gets the effective deserialize options.</summary>
    public DeserializeOptions Options => _options;

    /// <summary>Gets the current top-level statement name, when known.</summary>
    public string? StatementName => _statementName;

    /// <summary>Gets the current field path, when known.</summary>
    public string? FieldPath => _fieldPath;

    /// <summary>
    /// Deserializes the current value as <typeparamref name="T"/> using the shared runtime.
    /// </summary>
    public T ReadAs<T>(ref PaktReader reader)
        => (T)PaktDeserializationRuntime.ReadObject(ref reader, typeof(T), ForCurrent(reader))!;

    /// <summary>
    /// Deserializes the current value as <typeparamref name="T"/> using a specific converter type.
    /// </summary>
    public T ReadAs<T, TConverter>(ref PaktReader reader)
        where TConverter : PaktConverter<T>, new()
        => (T)PaktDeserializationRuntime.ReadObject(ref reader, typeof(T), ForCurrent(reader), typeof(TConverter))!;

    /// <summary>
    /// Skips the current value entirely.
    /// </summary>
    public void Skip(ref PaktReader reader) => reader.SkipValue();

    /// <summary>
    /// Creates a contextual deserialization error.
    /// </summary>
    public PaktDeserializeException CreateError(
        string message,
        PaktPosition position,
        string? fieldName = null,
        PaktErrorCode code = PaktErrorCode.TypeMismatch,
        Exception? inner = null)
    {
        var effectiveField = fieldName ?? _fieldPath;
        return inner is null
            ? new PaktDeserializeException(message, position, _statementName, effectiveField, code)
            : new PaktDeserializeException(message, position, _statementName, effectiveField, code, inner);
    }

    internal PaktConvertContext ForCurrent(PaktReader reader) => ForSegment(reader.CurrentName);

    internal PaktConvertContext ForSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
            return this;

        var fieldPath = string.IsNullOrEmpty(_fieldPath)
            ? segment
            : $"{_fieldPath}.{segment}";

        return new PaktConvertContext(_serializerContext, _options, _statementName, fieldPath);
    }
}
