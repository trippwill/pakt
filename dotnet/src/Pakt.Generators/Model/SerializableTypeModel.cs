using System;

namespace Pakt.Generators.Model
{
    internal sealed class SerializableTypeModel : IEquatable<SerializableTypeModel>
    {
        public SerializableTypeModel(
            string fullyQualifiedName,
            string name,
            string ns,
            EquatableArray<PropertyModel> properties)
        {
            FullyQualifiedName = fullyQualifiedName;
            Name = name;
            Namespace = ns;
            Properties = properties;
        }

        /// <summary>e.g., "global::MyApp.Server"</summary>
        public string FullyQualifiedName { get; }

        /// <summary>e.g., "Server"</summary>
        public string Name { get; }

        /// <summary>e.g., "MyApp"</summary>
        public string Namespace { get; }

        public EquatableArray<PropertyModel> Properties { get; }

        public bool Equals(SerializableTypeModel? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return FullyQualifiedName == other.FullyQualifiedName
                && Name == other.Name
                && Namespace == other.Namespace
                && Properties.Equals(other.Properties);
        }

        public override bool Equals(object? obj) => Equals(obj as SerializableTypeModel);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + FullyQualifiedName.GetHashCode();
                hash = hash * 31 + Properties.GetHashCode();
                return hash;
            }
        }
    }
}
