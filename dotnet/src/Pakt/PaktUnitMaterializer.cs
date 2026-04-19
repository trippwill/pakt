using System.Collections;
using System.Reflection;
using Pakt.Serialization;

namespace Pakt;

internal static class PaktUnitMaterializer
{
    public static T Materialize<T>(PaktMemoryReader reader, PaktSerializerContext context, DeserializeOptions options)
    {
        var typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' is not registered in the serializer context.");

        object target = Activator.CreateInstance(typeof(T))
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' must have a parameterless constructor or be a value type.");

        var bindings = CreateBindings(typeof(T), typeInfo);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (reader.ReadStatement())
        {
            var statementPosition = reader.StatementPosition;

            if (!bindings.TryGetValue(reader.StatementName, out var binding))
            {
                if (options.UnknownFields == UnknownFieldPolicy.Error)
                {
                    throw new PaktDeserializeException(
                        $"Unknown statement '{reader.StatementName}'",
                        statementPosition, reader.StatementName, reader.StatementName,
                        PaktErrorCode.TypeMismatch);
                }

                reader.Skip();
                continue;
            }

            var duplicate = !seen.Add(binding.Metadata.PaktName);
            if (duplicate)
            {
                switch (options.Duplicates)
                {
                    case DuplicatePolicy.FirstWins:
                        reader.Skip();
                        continue;

                    case DuplicatePolicy.Error:
                        throw new PaktDeserializeException(
                            $"Duplicate statement '{binding.Metadata.PaktName}'",
                            statementPosition, binding.Metadata.PaktName, binding.Metadata.PaktName,
                            PaktErrorCode.TypeMismatch);
                }
            }

            if (reader.IsPack)
            {
                if (binding.Metadata.ConverterType is not null)
                {
                    throw new PaktDeserializeException(
                        $"Pack statement '{binding.Metadata.PaktName}' cannot use a property-level converter.",
                        statementPosition, binding.Metadata.PaktName, binding.Metadata.PaktName,
                        PaktErrorCode.TypeMismatch);
                }

                switch (options.Duplicates)
                {
                    case DuplicatePolicy.LastWins when duplicate:
                        ReplaceFromPack(reader, target, binding, options, statementPosition);
                        break;

                    default:
                        AppendFromPack(reader, target, binding, options, statementPosition);
                        break;
                }

                continue;
            }

            if (!duplicate || options.Duplicates == DuplicatePolicy.LastWins)
            {
                binding.Property.SetValue(
                    target,
                    reader.ReadValue(binding.Property.PropertyType, binding.Metadata.ConverterType));
                continue;
            }

            if (options.Duplicates == DuplicatePolicy.Accumulate)
            {
                AppendSingleValue(reader, target, binding, statementPosition);
                continue;
            }

            reader.Skip();
        }

        CheckMissing(bindings, seen, options);
        return (T)target;
    }

    public static async ValueTask<T> MaterializeAsync<T>(
        PaktStreamReader reader, PaktSerializerContext context, DeserializeOptions options,
        CancellationToken ct = default)
    {
        // For whole-unit materialization over a stream, we use the statement-level
        // async API to read each statement. This is a simplified path that handles
        // the common case of non-pack assign statements. Full async pack materialization
        // with collection binding will be added when needed.
        var typeInfo = context.GetTypeInfo<T>()
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' is not registered in the serializer context.");

        object target = Activator.CreateInstance(typeof(T))
            ?? throw new InvalidOperationException(
                $"Type '{typeof(T).Name}' must have a parameterless constructor or be a value type.");

        var bindings = CreateBindings(typeof(T), typeInfo);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (await reader.ReadStatementAsync(ct).ConfigureAwait(false))
        {
            var statementPosition = reader.StatementPosition;

            if (!bindings.TryGetValue(reader.StatementName, out var binding))
            {
                if (options.UnknownFields == UnknownFieldPolicy.Error)
                {
                    throw new PaktDeserializeException(
                        $"Unknown statement '{reader.StatementName}'",
                        statementPosition, reader.StatementName, reader.StatementName,
                        PaktErrorCode.TypeMismatch);
                }

                await reader.SkipAsync(ct).ConfigureAwait(false);
                continue;
            }

            var duplicate = !seen.Add(binding.Metadata.PaktName);
            if (duplicate)
            {
                switch (options.Duplicates)
                {
                    case DuplicatePolicy.FirstWins:
                        await reader.SkipAsync(ct).ConfigureAwait(false);
                        continue;

                    case DuplicatePolicy.Error:
                        throw new PaktDeserializeException(
                            $"Duplicate statement '{binding.Metadata.PaktName}'",
                            statementPosition, binding.Metadata.PaktName, binding.Metadata.PaktName,
                            PaktErrorCode.TypeMismatch);
                }
            }

            if (reader.IsPack)
            {
                if (binding.Metadata.ConverterType is not null)
                {
                    throw new PaktDeserializeException(
                        $"Pack statement '{binding.Metadata.PaktName}' cannot use a property-level converter.",
                        statementPosition, binding.Metadata.PaktName, binding.Metadata.PaktName,
                        PaktErrorCode.TypeMismatch);
                }

                // Async pack materialization: collect elements into a list
                if (binding.IsDictionary)
                {
                    var dictionary = (duplicate && options.Duplicates == DuplicatePolicy.LastWins)
                        ? CreateDictionary(binding.DictionaryKeyType!, binding.DictionaryValueType!)
                        : (binding.Property.GetValue(target) as IDictionary ?? CreateDictionary(binding.DictionaryKeyType!, binding.DictionaryValueType!));

                    await foreach (var entry in reader.ReadMapPackEntriesAsync(binding.PackElementType!, ct).ConfigureAwait(false))
                        AddPackDictionaryEntry(dictionary, entry!, options, binding, statementPosition);

                    binding.Property.SetValue(target, dictionary);
                }
                else if (binding.IsArray || binding.IsListLike)
                {
                    var list = (duplicate && options.Duplicates == DuplicatePolicy.LastWins)
                        ? CreateList(binding.ElementType!)
                        : (binding.Property.GetValue(target) as IList ?? CreateList(binding.ElementType!));

                    await foreach (var item in reader.ReadPackValuesAsync(binding.ElementType!, ct).ConfigureAwait(false))
                        list.Add(item);

                    if (binding.IsArray)
                        binding.Property.SetValue(target, ToArray(binding.ElementType!, list.Cast<object?>().ToList()));
                    else
                        binding.Property.SetValue(target, list);
                }
                else
                {
                    throw new PaktDeserializeException(
                        $"Statement '{binding.Metadata.PaktName}' is a pack, but CLR property '{binding.Property.Name}' is not collection-like.",
                        statementPosition, binding.Metadata.PaktName, binding.Metadata.PaktName,
                        PaktErrorCode.TypeMismatch);
                }

                continue;
            }

            if (!duplicate || options.Duplicates == DuplicatePolicy.LastWins)
            {
                var value = await reader.ReadValueAsync(binding.Property.PropertyType, binding.Metadata.ConverterType, ct)
                    .ConfigureAwait(false);
                binding.Property.SetValue(target, value);
                continue;
            }

            await reader.SkipAsync(ct).ConfigureAwait(false);
        }

        CheckMissing(bindings, seen, options);
        return (T)target;
    }

    private static void CheckMissing(
        Dictionary<string, UnitBinding> bindings,
        HashSet<string> seen,
        DeserializeOptions options)
    {
        if (options.MissingFields != MissingFieldPolicy.Error)
            return;

        foreach (var binding in bindings.Values)
        {
            if (seen.Contains(binding.Metadata.PaktName))
                continue;

            throw new PaktDeserializeException(
                $"Missing statement '{binding.Metadata.PaktName}'",
                PaktPosition.None,
                binding.Metadata.PaktName,
                binding.Metadata.PaktName,
                PaktErrorCode.TypeMismatch);
        }
    }

    private static Dictionary<string, UnitBinding> CreateBindings<T>(
        Type clrType,
        PaktTypeInfo<T> typeInfo)
    {
        var properties = clrType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var byClrName = properties.ToDictionary(static property => property.Name, StringComparer.Ordinal);
        var bindings = new Dictionary<string, UnitBinding>(StringComparer.Ordinal);

        foreach (var metadata in typeInfo.Properties.Where(static property => !property.IsIgnored))
        {
            if (!byClrName.TryGetValue(metadata.ClrName, out var property))
                continue;

            bindings[metadata.PaktName] = new UnitBinding(metadata, property);
        }

        return bindings;
    }

    private static void ReplaceFromPack(
        PaktMemoryReader reader,
        object target,
        UnitBinding binding,
        DeserializeOptions options,
        PaktPosition statementPosition)
    {
        if (binding.IsDictionary)
        {
            var dictionary = CreateDictionary(binding.DictionaryKeyType!, binding.DictionaryValueType!);
            foreach (var entry in reader.ReadMapPackEntries(binding.PackElementType!))
                AddPackDictionaryEntry(dictionary, entry!, options, binding, statementPosition);

            binding.Property.SetValue(target, dictionary);
            return;
        }

        if (binding.IsArray)
        {
            var values = new List<object?>();
            foreach (var item in reader.ReadPackValues(binding.PackElementType!))
                values.Add(item);

            binding.Property.SetValue(target, ToArray(binding.ElementType!, values));
            return;
        }

        if (binding.IsListLike)
        {
            var list = CreateList(binding.ElementType!);
            foreach (var item in reader.ReadPackValues(binding.PackElementType!))
                list.Add(item);

            binding.Property.SetValue(target, list);
            return;
        }

        throw new PaktDeserializeException(
            $"Statement '{binding.Metadata.PaktName}' is a pack, but CLR property '{binding.Property.Name}' is not collection-like.",
            statementPosition,
            binding.Metadata.PaktName,
            binding.Metadata.PaktName,
            PaktErrorCode.TypeMismatch);
    }

    private static void AppendFromPack(
        PaktMemoryReader reader,
        object target,
        UnitBinding binding,
        DeserializeOptions options,
        PaktPosition statementPosition)
    {
        if (binding.IsDictionary)
        {
            var dictionary = binding.Property.GetValue(target) as IDictionary
                ?? CreateDictionary(binding.DictionaryKeyType!, binding.DictionaryValueType!);

            foreach (var entry in reader.ReadMapPackEntries(binding.PackElementType!))
                AddPackDictionaryEntry(dictionary, entry!, options, binding, statementPosition);

            binding.Property.SetValue(target, dictionary);
            return;
        }

        if (binding.IsArray)
        {
            var values = new List<object?>();
            if (binding.Property.GetValue(target) is Array existingArray)
            {
                foreach (var item in existingArray)
                    values.Add(item);
            }

            foreach (var item in reader.ReadPackValues(binding.PackElementType!))
                values.Add(item);

            binding.Property.SetValue(target, ToArray(binding.ElementType!, values));
            return;
        }

        if (binding.IsListLike)
        {
            var list = binding.Property.GetValue(target) as IList
                ?? CreateList(binding.ElementType!);

            foreach (var item in reader.ReadPackValues(binding.PackElementType!))
                list.Add(item);

            binding.Property.SetValue(target, list);
            return;
        }

        throw new PaktDeserializeException(
            $"Statement '{binding.Metadata.PaktName}' is a pack, but CLR property '{binding.Property.Name}' is not collection-like.",
            statementPosition,
            binding.Metadata.PaktName,
            binding.Metadata.PaktName,
            PaktErrorCode.TypeMismatch);
    }

    private static void AppendSingleValue(
        PaktMemoryReader reader,
        object target,
        UnitBinding binding,
        PaktPosition statementPosition)
    {
        if (binding.IsArray)
        {
            var values = new List<object?>();
            if (binding.Property.GetValue(target) is Array existingArray)
            {
                foreach (var item in existingArray)
                    values.Add(item);
            }

            values.Add(reader.ReadValue(binding.ElementType!));
            binding.Property.SetValue(target, ToArray(binding.ElementType!, values));
            return;
        }

        if (binding.IsListLike)
        {
            var list = binding.Property.GetValue(target) as IList
                ?? CreateList(binding.ElementType!);

            list.Add(reader.ReadValue(binding.ElementType!));
            binding.Property.SetValue(target, list);
            return;
        }

        throw new PaktDeserializeException(
            $"Duplicate statement '{binding.Metadata.PaktName}' cannot be accumulated into non-collection CLR property '{binding.Property.Name}'.",
            statementPosition,
            binding.Metadata.PaktName,
            binding.Metadata.PaktName,
            PaktErrorCode.TypeMismatch);
    }

    private static void AddPackDictionaryEntry(
        IDictionary dictionary,
        object entry,
        DeserializeOptions options,
        UnitBinding binding,
        PaktPosition statementPosition)
    {
        var key = entry.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry);
        var value = entry.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry);

        if (key is null || !dictionary.Contains(key))
        {
            dictionary[key!] = value;
            return;
        }

        switch (options.Duplicates)
        {
            case DuplicatePolicy.LastWins:
            case DuplicatePolicy.Accumulate:
                dictionary[key!] = value;
                return;

            case DuplicatePolicy.FirstWins:
                return;

            case DuplicatePolicy.Error:
                throw new PaktDeserializeException(
                    $"Duplicate key '{key}' in statement '{binding.Metadata.PaktName}'.",
                    statementPosition,
                    binding.Metadata.PaktName,
                    binding.Metadata.PaktName,
                    PaktErrorCode.TypeMismatch);
        }
    }

    private static IList CreateList(Type elementType)
        => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

    private static IDictionary CreateDictionary(Type keyType, Type valueType)
        => (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;

    private static Array ToArray(Type elementType, IReadOnlyList<object?> values)
    {
        var array = Array.CreateInstance(elementType, values.Count);
        for (var i = 0; i < values.Count; i++)
            array.SetValue(values[i], i);
        return array;
    }

    private sealed class UnitBinding
    {
        public UnitBinding(PaktPropertyInfo metadata, PropertyInfo property)
        {
            Metadata = metadata;
            Property = property;
            IsArray = property.PropertyType.IsArray;
            if (IsArray)
            {
                ElementType = property.PropertyType.GetElementType();
            }
            else if (TryGetListElementType(property.PropertyType, out var elementType))
            {
                ElementType = elementType;
                IsListLike = true;
            }
            else if (TryGetDictionaryTypes(property.PropertyType, out var keyType, out var valueType))
            {
                DictionaryKeyType = keyType;
                DictionaryValueType = valueType;
                PackElementType = typeof(PaktMapEntry<,>).MakeGenericType(keyType, valueType);
                IsDictionary = true;
            }

            PackElementType ??= ElementType;
        }

        public PaktPropertyInfo Metadata { get; }

        public PropertyInfo Property { get; }

        public Type? ElementType { get; }

        public Type? PackElementType { get; }

        public Type? DictionaryKeyType { get; }

        public Type? DictionaryValueType { get; }

        public bool IsArray { get; }

        public bool IsListLike { get; }

        public bool IsDictionary { get; }
    }

    private static bool TryGetListElementType(Type type, out Type elementType)
    {
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
