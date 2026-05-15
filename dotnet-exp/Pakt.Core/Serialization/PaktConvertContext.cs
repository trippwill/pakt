using System.Runtime.InteropServices;

namespace Pakt;

/// <summary>
/// Context provided to <see cref="PaktConverter{T}"/> during deserialization.
/// Stack-only to avoid heap allocation in the converter pipeline.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly ref struct PaktConvertContext
{
    private readonly PaktSerializerContext _serializerContext;
    private readonly PaktSerializationOptions _options;
    private readonly string _statementName;
    private readonly string _fieldPath;

    internal PaktConvertContext(
        PaktSerializerContext serializerContext,
        PaktSerializationOptions options,
        string statementName,
        string fieldPath)
    {
        _serializerContext = serializerContext;
        _options = options;
        _statementName = statementName;
        _fieldPath = fieldPath;
    }

    /// <summary>The serializer context containing type registrations.</summary>
    public PaktSerializerContext SerializerContext => _serializerContext;

    /// <summary>Active serialization options (policies).</summary>
    public PaktSerializationOptions Options => _options;

    /// <summary>Name of the current statement being deserialized.</summary>
    public string StatementName => _statementName;

    /// <summary>
    /// Dot-separated path to the current field for error reporting.
    /// Example: "config.server.host"
    /// </summary>
    public string FieldPath => _fieldPath;

    /// <summary>
    /// Create a child context for a nested field.
    /// </summary>
    internal PaktConvertContext ForField(string fieldName) =>
        new(_serializerContext, _options, _statementName,
            _fieldPath.Length > 0 ? $"{_fieldPath}.{fieldName}" : fieldName);

    /// <summary>
    /// Skip the current value (composite or scalar) in the reader.
    /// Useful for converters that want to ignore certain fields.
    /// </summary>
    public static void Skip(ref PaktValidatingReader reader)
    {
        PaktTokenType token = reader.TokenType;

        // If it's a scalar, it's already consumed
        if (!IsCompositeStart(token))
            return;

        // For composites: read until matching close at same depth
        int startDepth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.CurrentDepth < startDepth)
                return;
        }
    }

    private static bool IsCompositeStart(PaktTokenType token) =>
        token is PaktTokenType.StructStart or PaktTokenType.TupleStart
            or PaktTokenType.ListStart or PaktTokenType.MapStart;
}