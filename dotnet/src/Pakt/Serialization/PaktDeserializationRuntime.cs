using System;
using System.Collections;
using System.Linq;

namespace Pakt.Serialization;

internal static class PaktDeserializationRuntime
{
    public static object? ReadObject(
        ref PaktReader reader,
        Type targetType,
        PaktConvertContext context,
        Type? converterOverrideType = null)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        var declaredType = reader.CurrentType
            ?? throw new InvalidOperationException("Reader is not positioned at a typed value.");

        if (reader.TokenType == PaktTokenType.Nil)
        {
            if (!declaredType.IsNullable)
                throw context.CreateError(
                    $"nil is not valid for non-nullable type {declaredType}",
                    reader.Position,
                    reader.CurrentName,
                    PaktErrorCode.NilNonNullable);

            if (CanBeNull(targetType))
                return null;

            throw context.CreateError(
                $"cannot assign nil to CLR type '{targetType.Name}'",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        var effectiveTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveTargetType == typeof(object) || effectiveTargetType == typeof(ValueType))
        {
            throw context.CreateError(
                "cannot deserialize into object; provide a concrete target type",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        if (effectiveTargetType.IsInterface
            && !TryGetListElementType(effectiveTargetType, out _)
            && !TryGetDictionaryTypes(effectiveTargetType, out _, out _))
        {
            throw context.CreateError(
                "cannot deserialize into interface type; provide a concrete target type",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        if (TryResolveConverter(targetType, converterOverrideType, context.Options, out var converter))
            return converter.ReadAsObject(ref reader, declaredType, context.ForCurrent(reader));

        if (TryReadScalar(ref reader, effectiveTargetType, context, out var scalarValue))
            return scalarValue;

        if (effectiveTargetType.IsEnum)
            return ReadEnum(ref reader, effectiveTargetType, context);

        if (TryGetTupleTypes(effectiveTargetType, out var tupleTypes))
            return ReadTuple(ref reader, effectiveTargetType, declaredType, context, tupleTypes);

        if (TryGetDictionaryTypes(effectiveTargetType, out var keyType, out var valueType))
            return ReadDictionary(ref reader, effectiveTargetType, declaredType, context, keyType, valueType);

        if (TryGetListElementType(effectiveTargetType, out var elementType))
            return ReadList(ref reader, effectiveTargetType, declaredType, context, elementType);

        var typeInfo = context.SerializerContext.GetTypeInfo(effectiveTargetType);
        if (typeInfo is not null)
        {
            if (reader.TokenType != PaktTokenType.StructStart)
            {
                throw context.CreateError(
                    $"expected struct value for CLR type '{effectiveTargetType.Name}', got {reader.TokenType}",
                    reader.Position,
                    reader.CurrentName,
                    PaktErrorCode.TypeMismatch);
            }

            return typeInfo.DeserializeObject(ref reader, context.ForCurrent(reader));
        }

        throw context.CreateError(
            $"Type '{effectiveTargetType}' is not supported for PAKT deserialization.",
            reader.Position,
            reader.CurrentName,
            PaktErrorCode.TypeMismatch);
    }

    private static bool TryReadScalar(
        ref PaktReader reader,
        Type targetType,
        PaktConvertContext context,
        out object? value)
    {
        value = null;

        if (targetType == typeof(string))
        {
            EnsureScalar(reader, context, PaktScalarType.Str, PaktScalarType.Atom);
            value = reader.ScalarType == PaktScalarType.Atom ? reader.GetAtom() : reader.GetString();
            return true;
        }

        if (targetType == typeof(long))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = reader.GetInt64();
            return true;
        }

        if (targetType == typeof(int))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((int)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(short))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((short)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(byte))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((byte)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(sbyte))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((sbyte)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(ulong))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((ulong)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(uint))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((uint)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(ushort))
        {
            EnsureScalar(reader, context, PaktScalarType.Int);
            value = checked((ushort)reader.GetInt64());
            return true;
        }

        if (targetType == typeof(decimal))
        {
            EnsureScalar(reader, context, PaktScalarType.Dec);
            value = reader.GetDecimal();
            return true;
        }

        if (targetType == typeof(double))
        {
            EnsureScalar(reader, context, PaktScalarType.Float);
            value = reader.GetDouble();
            return true;
        }

        if (targetType == typeof(float))
        {
            EnsureScalar(reader, context, PaktScalarType.Float);
            value = (float)reader.GetDouble();
            return true;
        }

        if (targetType == typeof(bool))
        {
            EnsureScalar(reader, context, PaktScalarType.Bool);
            value = reader.GetBoolean();
            return true;
        }

        if (targetType == typeof(Guid))
        {
            EnsureScalar(reader, context, PaktScalarType.Uuid);
            value = reader.GetGuid();
            return true;
        }

        if (targetType == typeof(DateOnly))
        {
            EnsureScalar(reader, context, PaktScalarType.Date);
            value = reader.GetDate();
            return true;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            EnsureScalar(reader, context, PaktScalarType.Ts);
            value = reader.GetTimestamp();
            return true;
        }

        if (targetType == typeof(byte[]))
        {
            EnsureScalar(reader, context, PaktScalarType.Bin);
            var bytes = new byte[reader.ValueSpan.Length / 2];
            var written = reader.GetBytesFromBin(bytes);
            if (written != bytes.Length)
                Array.Resize(ref bytes, written);
            value = bytes;
            return true;
        }

        return false;
    }

    private static object ReadEnum(
        ref PaktReader reader,
        Type targetType,
        PaktConvertContext context)
    {
        EnsureScalar(reader, context, PaktScalarType.Str, PaktScalarType.Atom);
        var raw = reader.ScalarType == PaktScalarType.Atom ? reader.GetAtom() : reader.GetString();
        try
        {
            return Enum.Parse(targetType, raw, ignoreCase: true);
        }
        catch (Exception ex)
        {
            throw context.CreateError(
                $"'{raw}' is not a valid {targetType.Name} value",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch,
                ex);
        }
    }

    private static object ReadList(
        ref PaktReader reader,
        Type targetType,
        PaktType declaredType,
        PaktConvertContext context,
        Type elementType)
    {
        if (!declaredType.IsList || reader.TokenType != PaktTokenType.ListStart)
        {
            throw context.CreateError(
                $"expected list value for CLR type '{targetType.Name}', got {reader.TokenType}",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;

        while (reader.Read())
        {
            if (reader.TokenType == PaktTokenType.ListEnd)
                break;

            list.Add(ReadObject(ref reader, elementType, context)!);
        }

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        return list;
    }

    private static object ReadDictionary(
        ref PaktReader reader,
        Type targetType,
        PaktType declaredType,
        PaktConvertContext context,
        Type keyType,
        Type valueType)
    {
        if (!declaredType.IsMap || reader.TokenType != PaktTokenType.MapStart)
        {
            throw context.CreateError(
                $"expected map value for CLR type '{targetType.Name}', got {reader.TokenType}",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;

        while (reader.Read())
        {
            if (reader.TokenType == PaktTokenType.MapEnd)
                break;

            var key = ReadObject(ref reader, keyType, context);
            if (!reader.Read())
            {
                throw context.CreateError(
                    "unexpected end of map while reading value",
                    reader.Position,
                    reader.CurrentName,
                    PaktErrorCode.UnexpectedEof);
            }

            var value = ReadObject(ref reader, valueType, context);
            ApplyDictionaryDuplicatePolicy(dictionary, key!, value, context, reader.Position, reader.CurrentName);
        }

        return dictionary;
    }

    private static object ReadTuple(
        ref PaktReader reader,
        Type targetType,
        PaktType declaredType,
        PaktConvertContext context,
        Type[] elementTypes)
    {
        if (!declaredType.IsTuple || reader.TokenType != PaktTokenType.TupleStart)
        {
            throw context.CreateError(
                $"expected tuple value for CLR type '{targetType.Name}', got {reader.TokenType}",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        if (declaredType.TupleElements.Length != elementTypes.Length)
        {
            throw context.CreateError(
                $"tuple arity mismatch: data declares {declaredType.TupleElements.Length}, target expects {elementTypes.Length}",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        var values = new object?[elementTypes.Length];
        var index = 0;
        while (reader.Read())
        {
            if (reader.TokenType == PaktTokenType.TupleEnd)
                break;

            if (index >= elementTypes.Length)
            {
                throw context.CreateError(
                    $"tuple arity mismatch: too many values for CLR type '{targetType.Name}'",
                    reader.Position,
                    reader.CurrentName,
                    PaktErrorCode.TypeMismatch);
            }

            values[index] = ReadObject(ref reader, elementTypes[index], context);
            index++;
        }

        if (index != elementTypes.Length)
        {
            throw context.CreateError(
                $"tuple arity mismatch: expected {elementTypes.Length} values, got {index}",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        return Activator.CreateInstance(targetType, values)!;
    }

    private static void ApplyDictionaryDuplicatePolicy(
        IDictionary dictionary,
        object key,
        object? value,
        PaktConvertContext context,
        PaktPosition position,
        string? fieldName)
    {
        if (!dictionary.Contains(key))
        {
            dictionary[key] = value;
            return;
        }

        switch (context.Options.Duplicates)
        {
            case DuplicatePolicy.LastWins:
                dictionary[key] = value;
                return;

            case DuplicatePolicy.FirstWins:
                return;

            case DuplicatePolicy.Accumulate:
                dictionary[key] = value;
                return;

            case DuplicatePolicy.Error:
                throw context.CreateError(
                    $"duplicate key '{key}'",
                    position,
                    fieldName,
                    PaktErrorCode.TypeMismatch);

            default:
                dictionary[key] = value;
                return;
        }
    }

    private static void EnsureScalar(
        PaktReader reader,
        PaktConvertContext context,
        params PaktScalarType[] allowedKinds)
    {
        if (reader.TokenType != PaktTokenType.ScalarValue)
        {
            throw context.CreateError(
                $"expected scalar value, got {reader.TokenType}",
                reader.Position,
                reader.CurrentName,
                PaktErrorCode.TypeMismatch);
        }

        foreach (var allowedKind in allowedKinds)
        {
            if (reader.ScalarType == allowedKind)
                return;
        }

        var allowed = string.Join(" or ", allowedKinds.Select(static kind => kind.ToString()));
        throw context.CreateError(
            $"expected {allowed}, got {reader.ScalarType}",
            reader.Position,
            reader.CurrentName,
            PaktErrorCode.TypeMismatch);
    }

    private static bool TryResolveConverter(
        Type targetType,
        Type? converterOverrideType,
        DeserializeOptions options,
        out PaktConverter converter)
    {
        if (converterOverrideType is not null)
        {
            converter = CreateConverterInstance(converterOverrideType, targetType);
            return true;
        }

        foreach (var candidate in options.Converters)
        {
            if (candidate is PaktConverter registered && registered.TargetType == targetType)
            {
                converter = registered;
                return true;
            }
        }

        converter = null!;
        return false;
    }

    private static PaktConverter CreateConverterInstance(Type converterType, Type targetType)
    {
        var instance = Activator.CreateInstance(converterType) as PaktConverter
            ?? throw new InvalidOperationException(
                $"Converter type '{converterType}' must derive from PaktConverter<T>.");

        if (instance.TargetType != targetType)
        {
            throw new InvalidOperationException(
                $"Converter type '{converterType}' targets '{instance.TargetType}', not '{targetType}'.");
        }

        return instance;
    }

    private static bool TryGetListElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>)
                || definition == typeof(IList<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(IEnumerable<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }

    private static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Dictionary<,>)
                || definition == typeof(IDictionary<,>)
                || definition == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
        }

        keyType = null!;
        valueType = null!;
        return false;
    }

    private static bool TryGetTupleTypes(Type type, out Type[] elementTypes)
    {
        if (!type.IsGenericType)
        {
            elementTypes = Array.Empty<Type>();
            return false;
        }

        var fullName = type.GetGenericTypeDefinition().FullName;
        if (fullName is null || !fullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            elementTypes = Array.Empty<Type>();
            return false;
        }

        elementTypes = type.GetGenericArguments();
        return true;
    }

    private static bool CanBeNull(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
}
