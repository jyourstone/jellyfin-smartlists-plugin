using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Marker interface for composite sort keys that embed tiebreaker values.
    /// In multi-sort scenarios, non-final sorts use only the primary value
    /// so that actual secondary sorts can determine sub-ordering.
    /// </summary>
    internal interface ICompositeSortKey
    {
        /// <summary>
        /// Gets the primary sort value without embedded tiebreakers.
        /// </summary>
        IComparable PrimaryValue { get; }
    }

    /// <summary>
    /// A comparable tuple for composite sort keys with 4 elements.
    /// Used for complex multi-level sorting.
    /// </summary>
    internal sealed class ComparableTuple4<T1, T2, T3, T4> : IComparable, ICompositeSortKey
        where T1 : IComparable
        where T2 : IComparable
        where T3 : IComparable
        where T4 : IComparable
    {
        private readonly T1 _item1;
        private readonly T2 _item2;
        private readonly T3 _item3;
        private readonly T4 _item4;
        private readonly IComparer<T1> _comparer1;
        private readonly IComparer<T2> _comparer2;
        private readonly IComparer<T3> _comparer3;
        private readonly IComparer<T4> _comparer4;

        public ComparableTuple4(T1 item1, T2 item2, T3 item3, T4 item4,
            IComparer<T1>? comparer1 = null,
            IComparer<T2>? comparer2 = null,
            IComparer<T3>? comparer3 = null,
            IComparer<T4>? comparer4 = null)
        {
            _item1 = item1;
            _item2 = item2;
            _item3 = item3;
            _item4 = item4;
            _comparer1 = comparer1 ?? Comparer<T1>.Default;
            _comparer2 = comparer2 ?? Comparer<T2>.Default;
            _comparer3 = comparer3 ?? Comparer<T3>.Default;
            _comparer4 = comparer4 ?? Comparer<T4>.Default;
        }

        /// <inheritdoc/>
        public IComparable PrimaryValue => _item1;

        public int CompareTo(object? obj)
        {
            if (obj is null) return 1;
            if (obj is not ComparableTuple4<T1, T2, T3, T4> other)
                throw new ArgumentException($"Object must be of type {typeof(ComparableTuple4<T1, T2, T3, T4>).Name}", nameof(obj));

            // Compare each level in order, returning if there's a difference
            var cmp1 = _comparer1.Compare(_item1, other._item1);
            if (cmp1 != 0) return cmp1;

            var cmp2 = _comparer2.Compare(_item2, other._item2);
            if (cmp2 != 0) return cmp2;

            var cmp3 = _comparer3.Compare(_item3, other._item3);
            if (cmp3 != 0) return cmp3;

            return _comparer4.Compare(_item4, other._item4);
        }
    }
}

