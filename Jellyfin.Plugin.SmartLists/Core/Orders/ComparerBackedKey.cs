using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// A sort key that carries the <see cref="IComparer{T}"/> its order sorts with.
    ///
    /// Single-sort goes through <c>Order.OrderBy</c>, which passes the order's overridden
    /// Comparer to LINQ. Multi-sort goes through <c>SmartList.ApplySortingCore</c>, which
    /// compares the raw <see cref="IComparable"/> keys with no comparer at all. Without this
    /// wrapper a name-ish order (Name, Name (Ignore Articles), SeriesName, Artist, AlbumName)
    /// silently changes semantics the moment a second sort is added, because the natural
    /// comparer stops running and ordinal string comparison takes over.
    ///
    /// Deliberately NOT an <see cref="ICompositeSortKey"/>: there are no embedded tiebreakers
    /// to strip, so the comparer must keep applying when the order sits in a non-final position.
    /// </summary>
    internal sealed class ComparerBackedKey<T> : IComparable
    {
        private readonly T _value;
        private readonly IComparer<T> _comparer;

        public ComparerBackedKey(T value, IComparer<T> comparer)
        {
            _value = value;
            _comparer = comparer ?? Comparer<T>.Default;
        }

        /// <summary>
        /// Gets the unwrapped sort value.
        /// </summary>
        public T Value => _value;

        public int CompareTo(object? obj)
        {
            // Null sorts first, matching Comparer<T>.Default and ComparableTuple4.
            if (obj is null) return 1;

            if (obj is ComparerBackedKey<T> other)
            {
                return _comparer.Compare(_value, other._value);
            }

            // Defensive: an unwrapped value of the same underlying type is still comparable,
            // so tolerate it rather than failing a whole refresh over a mixed key list.
            if (obj is T bare)
            {
                return _comparer.Compare(_value, bare);
            }

            throw new ArgumentException($"Object must be of type {typeof(ComparerBackedKey<T>).Name}", nameof(obj));
        }
    }
}
