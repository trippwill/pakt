using System;
using System.Collections;
using System.Collections.Generic;

namespace Pakt.Generators.Model
{
    /// <summary>
    /// Wraps an array with value-based equality, required for incremental generator caching.
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly T[]? _array;

        public EquatableArray(T[]? array)
        {
            _array = array;
        }

        public int Length => _array?.Length ?? 0;

        public T this[int index] => _array![index];

        public bool IsDefault => _array is null;

        public bool Equals(EquatableArray<T> other)
        {
            int len = Length;
            int otherLen = other.Length;
            if (len != otherLen) return false;
            for (int i = 0; i < len; i++)
            {
                if (!_array![i].Equals(other._array![i])) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) =>
            obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            if (_array is null || _array.Length == 0) return 0;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < _array.Length; i++)
                {
                    hash = hash * 31 + _array[i].GetHashCode();
                }
                return hash;
            }
        }

        public IEnumerator<T> GetEnumerator() =>
            ((IEnumerable<T>)(_array ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static implicit operator EquatableArray<T>(T[]? array) => new EquatableArray<T>(array);
    }
}
