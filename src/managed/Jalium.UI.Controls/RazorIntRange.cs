using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Jalium.UI.Controls;

/// <summary>
/// Virtual integer sequence backing the numeric form of the <c>@virtualize</c> directive
/// (<c>@virtualize(var i = 0; i &lt; Count; i++)</c>). Stores no elements: the value at an
/// index is computed as <c>Start + index * Step</c>, so a million-item range costs nothing
/// beyond this object.
/// </summary>
/// <remarks>
/// <para>
/// Implementing the non-generic <see cref="IList"/> is mandatory, not incidental.
/// <c>CollectionViewSource.CreateDefaultView</c> only routes an <see cref="IList"/> to
/// <c>ListCollectionView</c>, and <c>CollectionView.RebuildEffectiveItems</c> then keeps an
/// unshaped list <em>by reference</em> to preserve O(1) indexed access for virtual/lazy
/// lists. A source that is merely <see cref="IEnumerable"/> lands on
/// <c>new CollectionView(enumerable)</c>, which eagerly enumerates into a
/// <c>List&lt;object&gt;</c> snapshot — one box per element, defeating the entire point.
/// </para>
/// <para>
/// Mutations raise <see cref="NotifyCollectionChangedAction.Reset"/> rather than a precise
/// Add/Remove. A precise notification has to materialise the changed-item list, so growing
/// a range from 100 to 1,000,000 would allocate 999,900 boxes to describe a change the
/// panel handles in O(realized) anyway.
/// </para>
/// </remarks>
public sealed class RazorIntRange
    : IReadOnlyList<int>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private int _start;
    private int _count;
    private int _step = 1;

    /// <summary>Initializes an empty range with a step of 1.</summary>
    public RazorIntRange()
    {
    }

    /// <summary>Initializes a range of <paramref name="count"/> values.</summary>
    public RazorIntRange(int start, int count, int step = 1)
    {
        ValidateStep(step);
        _start = start;
        _count = Math.Max(0, count);
        _step = step;
    }

    /// <summary>Gets the first value in the sequence.</summary>
    public int Start => _start;

    /// <summary>Gets the number of values in the sequence.</summary>
    public int Count => _count;

    /// <summary>Gets the increment between consecutive values. Never zero.</summary>
    public int Step => _step;

    /// <summary>
    /// Replaces every range parameter at once and raises a single reset. Updating the
    /// three properties separately would raise up to three resets and let the panel
    /// observe inconsistent intermediate ranges.
    /// </summary>
    public void Update(int start, int count, int step)
    {
        ValidateStep(step);
        count = Math.Max(0, count);
        if (_start == start && _count == count && _step == step)
        {
            return;
        }

        var countChanged = _count != count;
        _start = start;
        _count = count;
        _step = step;

        if (countChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc />
    public int this[int index] => (uint)index < (uint)_count
        ? _start + (index * _step)
        : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>
    /// Returns the index of <paramref name="value"/> without scanning: the sequence is
    /// arithmetic, so membership is a division.
    /// </summary>
    public int IndexOf(int value)
    {
        var delta = (long)value - _start;
        if (delta % _step != 0)
        {
            return -1;
        }

        var index = delta / _step;
        return index >= 0 && index < _count ? (int)index : -1;
    }

    /// <summary>Returns a non-allocating enumerator over the sequence.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private static void ValidateStep(int step)
    {
        // A zero step makes every element equal, which collapses the generator's
        // item-to-container map and makes IndexOf ambiguous.
        if (step == 0)
        {
            throw new ArgumentException("@virtualize numeric range step cannot be zero.", nameof(step));
        }
    }

    private static int Unbox(object? value) => value is int i
        ? i
        : throw new ArgumentException("RazorIntRange only contains Int32 values.", nameof(value));

    #region IList

    bool IList.IsReadOnly => true;

    bool IList.IsFixedSize => false;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException("RazorIntRange is read-only.");
    }

    bool IList.Contains(object? value) => value is int i && IndexOf(i) >= 0;

    int IList.IndexOf(object? value) => value is int i ? IndexOf(i) : -1;

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < _count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException("RazorIntRange is read-only.");

    void IList.Insert(int index, object? value) => throw new NotSupportedException("RazorIntRange is read-only.");

    void IList.Remove(object? value) => throw new NotSupportedException("RazorIntRange is read-only.");

    void IList.RemoveAt(int index) => throw new NotSupportedException("RazorIntRange is read-only.");

    void IList.Clear() => throw new NotSupportedException("RazorIntRange is read-only.");

    #endregion

    /// <summary>Struct enumerator so <c>foreach</c> over a range allocates nothing.</summary>
    public struct Enumerator : IEnumerator<int>
    {
        private readonly RazorIntRange _range;
        private int _index;

        internal Enumerator(RazorIntRange range)
        {
            _range = range;
            _index = -1;
        }

        /// <inheritdoc />
        public int Current => _range[_index];

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext() => ++_index < _range.Count;

        /// <inheritdoc />
        public void Reset() => _index = -1;

        /// <inheritdoc />
        public readonly void Dispose()
        {
        }
    }
}
