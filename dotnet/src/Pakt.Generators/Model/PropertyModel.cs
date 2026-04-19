using System;

namespace Pakt.Generators.Model
{
    internal sealed class PropertyModel : IEquatable<PropertyModel>
    {
        public PropertyModel(
            string clrName,
            string paktName,
            string typeFullName,
            string? converterTypeFullName,
            PaktTypeKind paktKind,
            bool isNullable,
            bool isIgnored,
            int order,
            EquatableArray<string> atomMembers,
            string? elementTypeFullName,
            PaktTypeKind? elementPaktKind,
            string? keyTypeFullName,
            PaktTypeKind? keyPaktKind,
            string? valueTypeFullName,
            PaktTypeKind? valuePaktKind,
            string? nestedTypeFullName,
            bool isArray)
        {
            ClrName = clrName;
            PaktName = paktName;
            TypeFullName = typeFullName;
            ConverterTypeFullName = converterTypeFullName;
            PaktKind = paktKind;
            IsNullable = isNullable;
            IsIgnored = isIgnored;
            Order = order;
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

        public string ClrName { get; }
        public string PaktName { get; }
        public string TypeFullName { get; }
        public string? ConverterTypeFullName { get; }
        public PaktTypeKind PaktKind { get; }
        public bool IsNullable { get; }
        public bool IsIgnored { get; }
        public int Order { get; }
        public EquatableArray<string> AtomMembers { get; }
        public string? ElementTypeFullName { get; }
        public PaktTypeKind? ElementPaktKind { get; }
        public string? KeyTypeFullName { get; }
        public PaktTypeKind? KeyPaktKind { get; }
        public string? ValueTypeFullName { get; }
        public PaktTypeKind? ValuePaktKind { get; }
        public string? NestedTypeFullName { get; }
        public bool IsArray { get; }

        public bool Equals(PropertyModel? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return ClrName == other.ClrName
                && PaktName == other.PaktName
                && TypeFullName == other.TypeFullName
                && ConverterTypeFullName == other.ConverterTypeFullName
                && PaktKind == other.PaktKind
                && IsNullable == other.IsNullable
                && IsIgnored == other.IsIgnored
                && Order == other.Order
                && AtomMembers.Equals(other.AtomMembers)
                && ElementTypeFullName == other.ElementTypeFullName
                && ElementPaktKind == other.ElementPaktKind
                && KeyTypeFullName == other.KeyTypeFullName
                && KeyPaktKind == other.KeyPaktKind
                && ValueTypeFullName == other.ValueTypeFullName
                && ValuePaktKind == other.ValuePaktKind
                && NestedTypeFullName == other.NestedTypeFullName
                && IsArray == other.IsArray;
        }

        public override bool Equals(object? obj) => Equals(obj as PropertyModel);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + ClrName.GetHashCode();
                hash = hash * 31 + PaktName.GetHashCode();
                hash = hash * 31 + (ConverterTypeFullName?.GetHashCode() ?? 0);
                hash = hash * 31 + (int)PaktKind;
                hash = hash * 31 + IsNullable.GetHashCode();
                hash = hash * 31 + Order;
                return hash;
            }
        }
    }
}
