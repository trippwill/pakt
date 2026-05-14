using Microsoft.CodeAnalysis;
using Pakt.Generators.Models;

namespace Pakt.Generators.Parser;

internal static class TypeModelBuilder
{
    public static SerializableTypeModel? Build(
        INamedTypeSymbol typeSymbol,
        Compilation compilation)
    {
        var properties = new List<PropertyModel>();
        int autoOrder = 0;

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.IsStatic || prop.IsIndexer) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;

            // Check for [PaktIgnore]
            if (HasAttribute(prop, "Pakt.PaktIgnoreAttribute"))
                continue;

            // Must have an accessible setter
            if (prop.SetMethod is null ||
                prop.SetMethod.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                continue;

            string paktName = GetPaktName(prop);
            int order = GetExplicitOrder(prop) ?? autoOrder;
            autoOrder++;

            var kind = ClassifyType(prop.Type, compilation);
            if (kind is null) continue; // unsupported type, skip

            var model = new PropertyModel
            {
                ClrName = prop.Name,
                PaktName = paktName,
                ClrTypeFqn = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Kind = kind.Value,
                IsNullable = prop.Type.NullableAnnotation == NullableAnnotation.Annotated
                    || (prop.Type is INamedTypeSymbol { IsGenericType: true } nts
                        && nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T),
                Order = order,
                IsIgnored = false,
                ElementTypeFqn = GetElementType(prop.Type, compilation),
                KeyTypeFqn = GetMapKeyType(prop.Type),
                ValueTypeFqn = GetMapValueType(prop.Type),
                NestedTypeFqn = kind == PaktTypeKind.Struct
                    ? prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null,
                ConverterTypeFqn = GetConverterType(prop),
            };

            properties.Add(model);
        }

        if (properties.Count == 0) return null;

        properties.Sort((a, b) => a.Order.CompareTo(b.Order));

        return new SerializableTypeModel
        {
            FullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Name = typeSymbol.Name,
            Namespace = typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null
                : typeSymbol.ContainingNamespace?.ToDisplayString(),
            Properties = properties,
            HasParameterlessConstructor = typeSymbol.InstanceConstructors
                .Any(c => c.Parameters.Length == 0
                    && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal),
        };
    }

    private static PaktTypeKind? ClassifyType(ITypeSymbol type, Compilation compilation)
    {
        // Unwrap Nullable<T>
        if (type is INamedTypeSymbol { IsGenericType: true } nts
            && nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            type = nts.TypeArguments[0];
        }

        // Unwrap nullable reference annotation
        if (type.NullableAnnotation == NullableAnnotation.Annotated && type.OriginalDefinition is { } orig)
        {
            type = orig;
        }

        return type.SpecialType switch
        {
            SpecialType.System_String => PaktTypeKind.String,
            SpecialType.System_Int32 => PaktTypeKind.Int,
            SpecialType.System_Int64 => PaktTypeKind.Long,
            SpecialType.System_Decimal => PaktTypeKind.Decimal,
            SpecialType.System_Double => PaktTypeKind.Double,
            SpecialType.System_Single => PaktTypeKind.Float,
            SpecialType.System_Boolean => PaktTypeKind.Bool,
            _ => ClassifyNonSpecialType(type, compilation),
        };
    }

    private static PaktTypeKind? ClassifyNonSpecialType(ITypeSymbol type, Compilation compilation)
    {
        string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (string.Equals(fqn, "global::System.Guid", System.StringComparison.Ordinal)) return PaktTypeKind.Guid;
        if (string.Equals(fqn, "global::System.DateOnly", System.StringComparison.Ordinal)) return PaktTypeKind.DateOnly;
        if (string.Equals(fqn, "global::System.DateTimeOffset", System.StringComparison.Ordinal)) return PaktTypeKind.DateTimeOffset;

        // byte[]
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
            return PaktTypeKind.ByteArray;

        // List<T>, IList<T>, IReadOnlyList<T>, ICollection<T>, IEnumerable<T>, arrays
        if (type is IArrayTypeSymbol) return PaktTypeKind.List;
        if (IsGenericInterface(type, "System.Collections.Generic.IList`1")) return PaktTypeKind.List;
        if (IsGenericInterface(type, "System.Collections.Generic.IReadOnlyList`1")) return PaktTypeKind.List;
        if (IsGenericInterface(type, "System.Collections.Generic.ICollection`1")) return PaktTypeKind.List;
        if (type is INamedTypeSymbol { IsGenericType: true } listNts
            && string.Equals(listNts.OriginalDefinition.ToDisplayString(), "System.Collections.Generic.List<T>", System.StringComparison.Ordinal))
            return PaktTypeKind.List;

        // Dictionary<K,V>, IDictionary<K,V>, IReadOnlyDictionary<K,V>
        if (IsGenericInterface(type, "System.Collections.Generic.IDictionary`2")) return PaktTypeKind.Map;
        if (IsGenericInterface(type, "System.Collections.Generic.IReadOnlyDictionary`2")) return PaktTypeKind.Map;
        if (type is INamedTypeSymbol { IsGenericType: true } dictNts
            && string.Equals(dictNts.OriginalDefinition.ToDisplayString(), "System.Collections.Generic.Dictionary<TKey, TValue>", System.StringComparison.Ordinal))
            return PaktTypeKind.Map;

        // Named type with public properties → nested struct
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named
            && named.GetMembers().OfType<IPropertySymbol>().Any(p => p is { IsStatic: false, DeclaredAccessibility: Accessibility.Public }))
            return PaktTypeKind.Struct;

        return null; // unsupported
    }

    private static bool IsGenericInterface(ITypeSymbol type, string interfaceFqn)
    {
        return type.AllInterfaces.Any(i =>
            i.IsGenericType && string.Equals(i.OriginalDefinition.ToDisplayString(), interfaceFqn, System.StringComparison.Ordinal));
    }

    private static string GetPaktName(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (string.Equals(attr.AttributeClass?.ToDisplayString(), "Pakt.PaktPropertyAttribute", System.StringComparison.Ordinal)
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is string name)
            {
                return name;
            }
        }

        // Default: kebab-case from PascalCase
        return ToKebabCase(prop.Name);
    }

    private static int? GetExplicitOrder(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (string.Equals(attr.AttributeClass?.ToDisplayString(), "Pakt.PaktPropertyOrderAttribute", System.StringComparison.Ordinal)
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is int order)
            {
                return order;
            }
        }

        return null;
    }

    private static string? GetConverterType(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (string.Equals(attr.AttributeClass?.ToDisplayString(), "Pakt.PaktConverterAttribute", System.StringComparison.Ordinal)
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol converterType)
            {
                return converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeFqn)
        => symbol.GetAttributes().Any(a => string.Equals(a.AttributeClass?.ToDisplayString(), attributeFqn, System.StringComparison.Ordinal));

    private static string? GetElementType(ITypeSymbol type, Compilation compilation)
    {
        if (type is IArrayTypeSymbol arr)
            return arr.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } nts)
            return nts.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return null;
    }

    private static string? GetMapKeyType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 2 } nts)
            return nts.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return null;
    }

    private static string? GetMapValueType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 2 } nts)
            return nts.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return null;
    }

    private static string ToKebabCase(string pascal)
    {
        if (string.IsNullOrEmpty(pascal)) return pascal;

        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
