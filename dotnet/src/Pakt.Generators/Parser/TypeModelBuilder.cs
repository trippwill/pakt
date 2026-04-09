using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Pakt.Generators.Model;

namespace Pakt.Generators.Parser
{
    internal static class TypeModelBuilder
    {
        private static readonly SymbolDisplayFormat FullyQualifiedFormat =
            SymbolDisplayFormat.FullyQualifiedFormat;

        public static SerializableTypeModel? Build(
            INamedTypeSymbol typeSymbol,
            List<Diagnostic> diagnostics,
            CancellationToken ct)
        {
            var properties = new List<PropertyModel>();
            int order = 0;
            int explicitOrderCount = 0;
            int totalProps = 0;

            foreach (var member in typeSymbol.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (!(member is IPropertySymbol prop)) continue;
                if (prop.IsStatic || prop.IsIndexer) continue;
                if (prop.DeclaredAccessibility != Accessibility.Public) continue;
                if (prop.GetMethod is null) continue;

                totalProps++;

                bool isIgnored = HasAttribute(prop, "Pakt.Serialization.PaktIgnoreAttribute");
                bool hasConverter = HasAttribute(prop, "Pakt.Serialization.PaktConverterAttribute");

                if (hasConverter)
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticDescriptors.ConverterNotSupported,
                        prop.Locations.FirstOrDefault(),
                        prop.Name));
                    isIgnored = true;
                }

                bool hasSetter = prop.SetMethod is not null &&
                    prop.SetMethod.DeclaredAccessibility >= Accessibility.Internal;

                if (!hasSetter && !isIgnored)
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticDescriptors.NoSettableSetter,
                        prop.Locations.FirstOrDefault(),
                        prop.Name, typeSymbol.Name));
                    isIgnored = true;
                }

                string paktName = GetPaktName(prop);

                int? explicitOrder = GetExplicitOrder(prop);
                if (explicitOrder.HasValue) explicitOrderCount++;

                int propOrder = explicitOrder ?? order;

                var analyzed = AnalyzePropertyType(prop);

                properties.Add(new PropertyModel(
                    clrName: prop.Name,
                    paktName: paktName,
                    typeFullName: analyzed.TypeFullName,
                    paktKind: analyzed.Kind,
                    isNullable: analyzed.IsNullable,
                    isIgnored: isIgnored,
                    order: propOrder,
                    atomMembers: analyzed.AtomMembers,
                    elementTypeFullName: analyzed.ElementTypeFullName,
                    elementPaktKind: analyzed.ElementPaktKind,
                    keyTypeFullName: analyzed.KeyTypeFullName,
                    keyPaktKind: analyzed.KeyPaktKind,
                    valueTypeFullName: analyzed.ValueTypeFullName,
                    valuePaktKind: analyzed.ValuePaktKind,
                    nestedTypeFullName: analyzed.NestedTypeFullName,
                    isArray: analyzed.IsArray));

                order++;
            }

            // Validate property order: all-or-none
            if (explicitOrderCount > 0 && explicitOrderCount < totalProps)
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticDescriptors.MixedPropertyOrder,
                    typeSymbol.Locations.FirstOrDefault(),
                    typeSymbol.Name));
            }

            // Sort by order
            properties.Sort((a, b) => a.Order.CompareTo(b.Order));

            return new SerializableTypeModel(
                fullyQualifiedName: typeSymbol.ToDisplayString(FullyQualifiedFormat),
                name: typeSymbol.Name,
                ns: typeSymbol.ContainingNamespace?.IsGlobalNamespace == true
                    ? ""
                    : typeSymbol.ContainingNamespace?.ToDisplayString() ?? "",
                properties: properties.ToArray());
        }

        private static string GetPaktName(IPropertySymbol prop)
        {
            var attr = GetAttribute(prop, "Pakt.Serialization.PaktPropertyAttribute");
            if (attr is not null && attr.ConstructorArguments.Length > 0)
            {
                var val = attr.ConstructorArguments[0].Value;
                if (val is string s) return s;
            }

            // Default: lowercase first character
            var name = prop.Name;
            if (name.Length == 0) return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static int? GetExplicitOrder(IPropertySymbol prop)
        {
            var attr = GetAttribute(prop, "Pakt.Serialization.PaktPropertyOrderAttribute");
            if (attr is not null && attr.ConstructorArguments.Length > 0)
            {
                var val = attr.ConstructorArguments[0].Value;
                if (val is int i) return i;
            }
            return null;
        }

        private readonly struct AnalyzedProperty
        {
            public readonly PaktTypeKind Kind;
            public readonly bool IsNullable;
            public readonly string TypeFullName;
            public readonly EquatableArray<string> AtomMembers;
            public readonly string? ElementTypeFullName;
            public readonly PaktTypeKind? ElementPaktKind;
            public readonly string? KeyTypeFullName;
            public readonly PaktTypeKind? KeyPaktKind;
            public readonly string? ValueTypeFullName;
            public readonly PaktTypeKind? ValuePaktKind;
            public readonly string? NestedTypeFullName;
            public readonly bool IsArray;

            public AnalyzedProperty(
                PaktTypeKind kind, bool isNullable, string typeFullName,
                EquatableArray<string> atomMembers = default,
                string? elementTypeFullName = null, PaktTypeKind? elementPaktKind = null,
                string? keyTypeFullName = null, PaktTypeKind? keyPaktKind = null,
                string? valueTypeFullName = null, PaktTypeKind? valuePaktKind = null,
                string? nestedTypeFullName = null, bool isArray = false)
            {
                Kind = kind;
                IsNullable = isNullable;
                TypeFullName = typeFullName;
                AtomMembers = atomMembers;
                ElementTypeFullName = elementTypeFullName;
                ElementPaktKind = elementPaktKind;
                KeyTypeFullName = keyTypeFullName;
                KeyPaktKind = keyPaktKind;
                ValueTypeFullName = valueTypeFullName;
                ValuePaktKind = valuePaktKind;
                NestedTypeFullName = nestedTypeFullName;
                IsArray = isArray;
            }
        }

        private static AnalyzedProperty AnalyzePropertyType(IPropertySymbol prop)
        {
            var type = prop.Type;
            bool nullable = false;

            // Unwrap Nullable<T>
            if (type is INamedTypeSymbol nts &&
                nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                nullable = true;
                type = nts.TypeArguments[0];
            }
            else if (type.NullableAnnotation == NullableAnnotation.Annotated)
            {
                nullable = true;
                // For annotated reference types, the underlying type is the same
                if (type is INamedTypeSymbol annotated &&
                    annotated.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                {
                    type = annotated.TypeArguments[0];
                }
            }

            string typeFullName = type.ToDisplayString(FullyQualifiedFormat);

            // Check [PaktScalar] override
            var scalarAttr = GetAttribute(prop, "Pakt.Serialization.PaktScalarAttribute");
            if (scalarAttr is not null && scalarAttr.ConstructorArguments.Length > 0)
            {
                var val = scalarAttr.ConstructorArguments[0].Value;
                if (val is int scalarValue)
                {
                    var kind = MapScalarTypeValue(scalarValue);
                    return new AnalyzedProperty(kind, nullable, typeFullName);
                }
            }

            // Check [PaktAtom]
            var atomAttr = GetAttribute(prop, "Pakt.Serialization.PaktAtomAttribute");
            if (atomAttr is not null && atomAttr.ConstructorArguments.Length > 0)
            {
                var arg = atomAttr.ConstructorArguments[0];
                var members = arg.Values.Select(v => (string)v.Value!).ToArray();
                if (members.Length > 0)
                {
                    return new AnalyzedProperty(PaktTypeKind.Atom, nullable, typeFullName,
                        atomMembers: members);
                }
            }

            // byte[] → Bin
            if (type is IArrayTypeSymbol arrayType &&
                arrayType.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return new AnalyzedProperty(PaktTypeKind.Bin, nullable, typeFullName);
            }

            // Other arrays → List
            if (type is IArrayTypeSymbol generalArray)
            {
                var elemType = generalArray.ElementType;
                var elemKind = MapTypeToKind(elemType);
                string? nestedFqn = elemKind == PaktTypeKind.Struct
                    ? elemType.ToDisplayString(FullyQualifiedFormat) : null;
                return new AnalyzedProperty(PaktTypeKind.List, nullable, typeFullName,
                    elementTypeFullName: elemType.ToDisplayString(FullyQualifiedFormat),
                    elementPaktKind: elemKind,
                    nestedTypeFullName: nestedFqn,
                    isArray: true);
            }

            // Map by special type
            switch (type.SpecialType)
            {
                case SpecialType.System_String:
                    return new AnalyzedProperty(PaktTypeKind.Str, nullable, typeFullName);
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                case SpecialType.System_Int16:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_UInt16:
                case SpecialType.System_UInt32:
                case SpecialType.System_UInt64:
                    return new AnalyzedProperty(PaktTypeKind.Int, nullable, typeFullName);
                case SpecialType.System_Decimal:
                    return new AnalyzedProperty(PaktTypeKind.Dec, nullable, typeFullName);
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    return new AnalyzedProperty(PaktTypeKind.Float, nullable, typeFullName);
                case SpecialType.System_Boolean:
                    return new AnalyzedProperty(PaktTypeKind.Bool, nullable, typeFullName);
            }

            // Named types
            if (type is INamedTypeSymbol namedType)
            {
                var originalFqn = namedType.OriginalDefinition.ToDisplayString(FullyQualifiedFormat);

                if (originalFqn == "global::System.Guid")
                    return new AnalyzedProperty(PaktTypeKind.Uuid, nullable, typeFullName);

                if (originalFqn == "global::System.DateOnly")
                    return new AnalyzedProperty(PaktTypeKind.Date, nullable, typeFullName);

                if (originalFqn == "global::System.DateTimeOffset")
                    return new AnalyzedProperty(PaktTypeKind.Ts, nullable, typeFullName);

                // List<T>, IList<T>, IReadOnlyList<T>, ICollection<T>
                if (IsListLikeType(namedType))
                {
                    var elemType = namedType.TypeArguments[0];
                    var elemKind = MapTypeToKind(elemType);
                    string? nestedFqn = elemKind == PaktTypeKind.Struct
                        ? elemType.ToDisplayString(FullyQualifiedFormat) : null;
                    return new AnalyzedProperty(PaktTypeKind.List, nullable, typeFullName,
                        elementTypeFullName: elemType.ToDisplayString(FullyQualifiedFormat),
                        elementPaktKind: elemKind,
                        nestedTypeFullName: nestedFqn);
                }

                // Dictionary<K,V>
                if (IsDictionaryType(namedType))
                {
                    var keyType = namedType.TypeArguments[0];
                    var valType = namedType.TypeArguments[1];
                    var keyKind = MapTypeToKind(keyType);
                    var valKind = MapTypeToKind(valType);
                    string? nestedFqn = valKind == PaktTypeKind.Struct
                        ? valType.ToDisplayString(FullyQualifiedFormat) : null;
                    return new AnalyzedProperty(PaktTypeKind.Map, nullable, typeFullName,
                        keyTypeFullName: keyType.ToDisplayString(FullyQualifiedFormat),
                        keyPaktKind: keyKind,
                        valueTypeFullName: valType.ToDisplayString(FullyQualifiedFormat),
                        valuePaktKind: valKind,
                        nestedTypeFullName: nestedFqn);
                }

                // Default: nested struct
                return new AnalyzedProperty(PaktTypeKind.Struct, nullable, typeFullName,
                    nestedTypeFullName: typeFullName);
            }

            // Fallback
            return new AnalyzedProperty(PaktTypeKind.Struct, nullable, typeFullName,
                nestedTypeFullName: typeFullName);
        }

        private static PaktTypeKind MapTypeToKind(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol nts &&
                nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                type = nts.TypeArguments[0];
            }

            // byte[] → Bin
            if (type is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
                return PaktTypeKind.Bin;

            if (type is IArrayTypeSymbol)
                return PaktTypeKind.List;

            switch (type.SpecialType)
            {
                case SpecialType.System_String: return PaktTypeKind.Str;
                case SpecialType.System_Int32:
                case SpecialType.System_Int64:
                case SpecialType.System_Int16:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_UInt16:
                case SpecialType.System_UInt32:
                case SpecialType.System_UInt64: return PaktTypeKind.Int;
                case SpecialType.System_Decimal: return PaktTypeKind.Dec;
                case SpecialType.System_Single:
                case SpecialType.System_Double: return PaktTypeKind.Float;
                case SpecialType.System_Boolean: return PaktTypeKind.Bool;
            }

            if (type is INamedTypeSymbol named)
            {
                var fqn = named.OriginalDefinition.ToDisplayString(FullyQualifiedFormat);
                if (fqn == "global::System.Guid") return PaktTypeKind.Uuid;
                if (fqn == "global::System.DateOnly") return PaktTypeKind.Date;
                if (fqn == "global::System.DateTimeOffset") return PaktTypeKind.Ts;
                if (IsListLikeType(named)) return PaktTypeKind.List;
                if (IsDictionaryType(named)) return PaktTypeKind.Map;
            }

            return PaktTypeKind.Struct;
        }

        private static bool IsListLikeType(INamedTypeSymbol type)
        {
            var original = type.OriginalDefinition.ToDisplayString(FullyQualifiedFormat);
            return original == "global::System.Collections.Generic.List<T>"
                || original == "global::System.Collections.Generic.IList<T>"
                || original == "global::System.Collections.Generic.IReadOnlyList<T>"
                || original == "global::System.Collections.Generic.ICollection<T>"
                || original == "global::System.Collections.Generic.IEnumerable<T>";
        }

        private static bool IsDictionaryType(INamedTypeSymbol type)
        {
            var original = type.OriginalDefinition.ToDisplayString(FullyQualifiedFormat);
            return original == "global::System.Collections.Generic.Dictionary<TKey, TValue>"
                || original == "global::System.Collections.Generic.IDictionary<TKey, TValue>"
                || original == "global::System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
        }

        private static PaktTypeKind MapScalarTypeValue(int value)
        {
            switch (value)
            {
                case 1: return PaktTypeKind.Str;
                case 2: return PaktTypeKind.Int;
                case 3: return PaktTypeKind.Dec;
                case 4: return PaktTypeKind.Float;
                case 5: return PaktTypeKind.Bool;
                case 6: return PaktTypeKind.Uuid;
                case 7: return PaktTypeKind.Date;
                case 8: return PaktTypeKind.Ts;
                case 10: return PaktTypeKind.Bin;
                case 11: return PaktTypeKind.Atom;
                default: return PaktTypeKind.Str;
            }
        }

        private static bool HasAttribute(ISymbol symbol, string attributeFqn)
        {
            return symbol.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == attributeFqn);
        }

        private static AttributeData? GetAttribute(ISymbol symbol, string attributeFqn)
        {
            return symbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == attributeFqn);
        }
    }
}
