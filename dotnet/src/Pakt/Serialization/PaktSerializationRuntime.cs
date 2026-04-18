using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Pakt.Serialization;

internal static class PaktSerializationRuntime
{
    public static void WriteObject(
        PaktWriter writer,
        object? value,
        Type clrType,
        PaktType declaredType,
        PaktSerializerContext context,
        DeserializeOptions options,
        Type? converterOverrideType = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(declaredType);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (value is null)
        {
            if (!declaredType.IsNullable)
            {
                throw new InvalidOperationException(
                    $"Cannot serialize null for non-nullable PAKT type '{declaredType}'.");
            }

            writer.WriteNilValue();
            return;
        }

        var targetType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (TryResolveConverter(targetType, converterOverrideType, options, out var converter))
        {
            converter.WriteAsObject(writer, value);
            return;
        }

        if (declaredType.IsAtomSet)
        {
            writer.WriteAtomValue(value.ToString().AsSpan());
            return;
        }

        if (declaredType.IsScalar)
        {
            WriteScalar(writer, value, targetType, declaredType.ScalarKind);
            return;
        }

        if (targetType.IsEnum)
        {
            writer.WriteAtomValue(value.ToString().AsSpan());
            return;
        }

        if (declaredType.IsStruct)
        {
            var typeInfo = context.GetTypeInfo(targetType)
                ?? throw new InvalidOperationException(
                    $"Type '{targetType.Name}' is not registered in the serializer context.");
            typeInfo.SerializeObject(writer, value);
            return;
        }

        if (declaredType.IsTuple)
        {
            WriteTuple(writer, value, targetType, declaredType, context, options);
            return;
        }

        if (declaredType.IsList)
        {
            WriteList(writer, value, targetType, declaredType, context, options);
            return;
        }

        if (declaredType.IsMap)
        {
            WriteMap(writer, value, targetType, declaredType, context, options);
            return;
        }

        throw new InvalidOperationException(
            $"Type '{targetType.Name}' is not supported for PAKT serialization.");
    }

    private static void WriteScalar(
        PaktWriter writer,
        object value,
        Type targetType,
        PaktScalarType scalarKind)
    {
        switch (scalarKind)
        {
            case PaktScalarType.Str:
                writer.WriteStringValue(((string)value).AsSpan());
                return;

            case PaktScalarType.Int:
                writer.WriteIntValue(ToInt64(value, targetType));
                return;

            case PaktScalarType.Dec:
                writer.WriteDecimalValue((decimal)value);
                return;

            case PaktScalarType.Float:
                writer.WriteFloatValue(targetType == typeof(float) ? (float)value : (double)value);
                return;

            case PaktScalarType.Bool:
                writer.WriteBoolValue((bool)value);
                return;

            case PaktScalarType.Uuid:
                writer.WriteUuidValue((Guid)value);
                return;

            case PaktScalarType.Date:
                writer.WriteDateValue((DateOnly)value);
                return;

            case PaktScalarType.Ts:
                writer.WriteTimestampValue((DateTimeOffset)value);
                return;

            case PaktScalarType.Bin:
                writer.WriteBinValue((byte[])value);
                return;

            default:
                throw new InvalidOperationException($"Unsupported scalar kind '{scalarKind}'.");
        }
    }

    private static void WriteTuple(
        PaktWriter writer,
        object value,
        Type clrType,
        PaktType declaredType,
        PaktSerializerContext context,
        DeserializeOptions options)
    {
        if (value is not ITuple tuple)
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' does not implement ITuple.");
        }

        writer.WriteTupleStart();
        var tupleTypes = declaredType.TupleElements;
        var clrTypes = clrType.GetGenericArguments();
        for (var i = 0; i < tuple.Length; i++)
        {
            WriteObject(writer, tuple[i], clrTypes[i], tupleTypes[i], context, options);
        }
        writer.WriteTupleEnd();
    }

    private static void WriteList(
        PaktWriter writer,
        object value,
        Type clrType,
        PaktType declaredType,
        PaktSerializerContext context,
        DeserializeOptions options)
    {
        if (value is not IEnumerable enumerable)
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' is not enumerable.");
        }

        if (!TryGetListElementType(clrType, out var elementType))
        {
            throw new InvalidOperationException(
                $"Could not determine element type for CLR type '{clrType.Name}'.");
        }

        writer.WriteListStart();
        foreach (var item in enumerable)
        {
            WriteObject(writer, item, elementType, declaredType.ListElement!, context, options);
        }
        writer.WriteListEnd();
    }

    private static void WriteMap(
        PaktWriter writer,
        object value,
        Type clrType,
        PaktType declaredType,
        PaktSerializerContext context,
        DeserializeOptions options)
    {
        if (!TryGetDictionaryTypes(clrType, out var keyType, out var valueType))
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' is not a supported dictionary type.");
        }

        writer.WriteMapStart();
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                WriteObject(writer, entry.Key, keyType, declaredType.MapKey!, context, options);
                writer.WriteMapKeySeparator();
                WriteObject(writer, entry.Value, valueType, declaredType.MapValue!, context, options);
            }
        }
        else if (value is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                var entryType = entry!.GetType();
                var key = entryType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry);
                var entryValue = entryType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry);
                WriteObject(writer, key, keyType, declaredType.MapKey!, context, options);
                writer.WriteMapKeySeparator();
                WriteObject(writer, entryValue, valueType, declaredType.MapValue!, context, options);
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' is not enumerable.");
        }

        writer.WriteMapEnd();
    }

    private static long ToInt64(object value, Type targetType)
        => value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            sbyte sbyteValue => sbyteValue,
            ushort ushortValue => ushortValue,
            uint uintValue => uintValue,
            ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
            _ => throw new InvalidOperationException(
                $"CLR type '{targetType.Name}' cannot be serialized as PAKT int."),
        };

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
}
