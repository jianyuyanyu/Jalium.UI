namespace Jalium.UI;

/// <summary>
/// Sparse dependency-property value storage optimized for the overwhelmingly common
/// case where a property has exactly one non-default value source.
/// </summary>
/// <remarks>
/// The previous representation kept one dictionary per precedence layer. Reading an
/// effective value consequently performed as many as six independent hash lookups,
/// while an element using styles and templates could allocate several dictionaries.
/// This store indexes a property once, keeps its effective value beside the key, and
/// allocates a <see cref="LayerValues"/> only when two value sources coexist.
/// </remarks>
internal sealed class DependencyValueStore
{
    [Flags]
    internal enum LayerMask : byte
    {
        None = 0,
        Local = 1 << 0,
        ParentTemplate = 1 << 1,
        StyleTrigger = 1 << 2,
        TemplateTrigger = 1 << 3,
        StyleSetter = 1 << 4,
        Current = 1 << 5,
    }

    internal enum Layer : byte
    {
        Local,
        ParentTemplate,
        StyleTrigger,
        TemplateTrigger,
        StyleSetter,
        Current,
    }

    private sealed class LayerValues
    {
        public object? Local;
        public object? ParentTemplate;
        public object? StyleTrigger;
        public object? TemplateTrigger;
        public object? StyleSetter;
        public object? Current;
        public BaseValueSource CurrentSource;
    }

    private struct Entry
    {
        public DependencyProperty Property;
        public LayerMask Mask;
        public object? EffectiveValue;
        public BaseValueSource EffectiveSource;
        public LayerValues? Values;
    }

    // Linear lookup is cheaper than allocating and hashing for the small stores used by
    // Geometry/Freezable objects. Controls normally cross this threshold and get one
    // GlobalIndex lookup instead of a scan or six layer dictionaries.
    private const int IndexedThreshold = 4;
    private Entry[] _entries = new Entry[IndexedThreshold];
    private Dictionary<int, int>? _indices;
    private int _count;

    internal int Count => _count;

    internal bool TryGetEffective(
        DependencyProperty property,
        out object? value,
        out BaseValueSource source)
    {
        var index = FindIndex(property);
        if (index < 0)
        {
            value = null;
            source = BaseValueSource.Unknown;
            return false;
        }

        ref readonly var entry = ref _entries[index];
        value = entry.EffectiveValue;
        source = entry.EffectiveSource;
        return true;
    }

    internal bool ContainsLayer(DependencyProperty property, Layer layer)
    {
        var index = FindIndex(property);
        return index >= 0 && (_entries[index].Mask & ToMask(layer)) != 0;
    }

    internal bool TryGetLayer(
        DependencyProperty property,
        Layer layer,
        out object? value,
        out BaseValueSource source)
    {
        var index = FindIndex(property);
        if (index < 0)
        {
            value = null;
            source = BaseValueSource.Unknown;
            return false;
        }

        ref readonly var entry = ref _entries[index];
        var mask = ToMask(layer);
        if ((entry.Mask & mask) == 0)
        {
            value = null;
            source = BaseValueSource.Unknown;
            return false;
        }

        if (entry.Values is null)
        {
            value = entry.EffectiveValue;
            source = entry.EffectiveSource;
            return true;
        }

        value = GetLayerValue(entry.Values, layer);
        source = layer == Layer.Current
            ? entry.Values.CurrentSource
            : GetFixedSource(layer);
        return true;
    }

    internal void SetLayer(
        DependencyProperty property,
        Layer layer,
        object? value,
        BaseValueSource currentSource = BaseValueSource.Unknown)
    {
        var mask = ToMask(layer);
        var index = FindIndex(property);
        if (index < 0)
        {
            EnsureCapacity(_count + 1);
            index = _count++;
            _entries[index] = new Entry
            {
                Property = property,
                Mask = mask,
                EffectiveValue = value,
                EffectiveSource = layer == Layer.Current ? currentSource : GetFixedSource(layer),
            };

            EnsureIndexIfNeeded();
            if (_indices is not null)
            {
                _indices[property.GlobalIndex] = index;
            }
            return;
        }

        ref var entry = ref _entries[index];
        if (entry.Values is null && entry.Mask == mask)
        {
            entry.EffectiveValue = value;
            entry.EffectiveSource = layer == Layer.Current ? currentSource : GetFixedSource(layer);
            return;
        }

        var values = entry.Values;
        if (values is null)
        {
            values = new LayerValues();
            var existingLayer = GetSingleLayer(entry.Mask);
            SetLayerValue(values, existingLayer, entry.EffectiveValue, entry.EffectiveSource);
            entry.Values = values;
        }

        SetLayerValue(values, layer, value, currentSource);
        entry.Mask |= mask;
        RecomputeEffective(ref entry);
    }

    internal bool RemoveLayer(DependencyProperty property, Layer layer)
    {
        var index = FindIndex(property);
        if (index < 0)
            return false;

        ref var entry = ref _entries[index];
        var mask = ToMask(layer);
        if ((entry.Mask & mask) == 0)
            return false;

        if (entry.Mask == mask)
        {
            RemoveEntry(index);
            return true;
        }

        entry.Mask &= ~mask;
        ClearLayerValue(entry.Values!, layer);
        RecomputeEffective(ref entry);

        if (IsSingleLayer(entry.Mask))
        {
            entry.Values = null;
        }

        return true;
    }

    internal KeyValuePair<DependencyProperty, object?>[] SnapshotLayer(Layer layer)
    {
        var mask = ToMask(layer);
        var result = new List<KeyValuePair<DependencyProperty, object?>>();
        for (var i = 0; i < _count; i++)
        {
            ref readonly var entry = ref _entries[i];
            if ((entry.Mask & mask) == 0)
                continue;

            object? value = entry.Values is null
                ? entry.EffectiveValue
                : GetLayerValue(entry.Values, layer);
            result.Add(new KeyValuePair<DependencyProperty, object?>(entry.Property, value));
        }

        return result.ToArray();
    }

    internal DependencyProperty[] SnapshotEffectiveProperties()
    {
        var result = new List<DependencyProperty>(_count);
        for (var i = 0; i < _count; i++)
        {
            ref readonly var entry = ref _entries[i];
            var nonCurrentMask = entry.Mask & ~LayerMask.Current;
            if (nonCurrentMask != LayerMask.None)
            {
                result.Add(entry.Property);
                continue;
            }

            if (TryGetLayer(entry.Property, Layer.Current, out _, out var currentSource) &&
                currentSource != BaseValueSource.Inherited)
            {
                result.Add(entry.Property);
            }
        }

        return result.ToArray();
    }

    internal bool HasAnyLayer(DependencyProperty property)
        => FindIndex(property) >= 0;

    private int FindIndex(DependencyProperty property)
    {
        if (_indices is not null)
        {
            return _indices.TryGetValue(property.GlobalIndex, out var index) ? index : -1;
        }

        for (var i = 0; i < _count; i++)
        {
            if (ReferenceEquals(_entries[i].Property, property))
                return i;
        }

        return -1;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _entries.Length)
            return;

        Array.Resize(ref _entries, Math.Max(required, _entries.Length * 2));
    }

    private void EnsureIndexIfNeeded()
    {
        if (_indices is not null || _count <= IndexedThreshold)
            return;

        var indices = new Dictionary<int, int>(_count);
        for (var i = 0; i < _count; i++)
        {
            indices[_entries[i].Property.GlobalIndex] = i;
        }
        _indices = indices;
    }

    private void RemoveEntry(int index)
    {
        var removedGlobalIndex = _entries[index].Property.GlobalIndex;
        var lastIndex = --_count;
        if (index != lastIndex)
        {
            _entries[index] = _entries[lastIndex];
            if (_indices is not null)
            {
                _indices[_entries[index].Property.GlobalIndex] = index;
            }
        }

        _entries[lastIndex] = default;
        _indices?.Remove(removedGlobalIndex);
        if (_count <= IndexedThreshold)
        {
            _indices = null;
        }
    }

    private static void RecomputeEffective(ref Entry entry)
    {
        var values = entry.Values!;
        if ((entry.Mask & LayerMask.Local) != 0)
        {
            entry.EffectiveValue = values.Local;
            entry.EffectiveSource = BaseValueSource.Local;
        }
        else if ((entry.Mask & LayerMask.TemplateTrigger) != 0)
        {
            entry.EffectiveValue = values.TemplateTrigger;
            entry.EffectiveSource = BaseValueSource.TemplateTrigger;
        }
        else if ((entry.Mask & LayerMask.StyleTrigger) != 0)
        {
            entry.EffectiveValue = values.StyleTrigger;
            entry.EffectiveSource = BaseValueSource.StyleTrigger;
        }
        else if ((entry.Mask & LayerMask.ParentTemplate) != 0)
        {
            entry.EffectiveValue = values.ParentTemplate;
            entry.EffectiveSource = BaseValueSource.ParentTemplate;
        }
        else if ((entry.Mask & LayerMask.StyleSetter) != 0)
        {
            entry.EffectiveValue = values.StyleSetter;
            entry.EffectiveSource = BaseValueSource.Style;
        }
        else
        {
            entry.EffectiveValue = values.Current;
            entry.EffectiveSource = values.CurrentSource;
        }
    }

    private static void SetLayerValue(
        LayerValues values,
        Layer layer,
        object? value,
        BaseValueSource source = BaseValueSource.Unknown)
    {
        switch (layer)
        {
            case Layer.Local: values.Local = value; break;
            case Layer.ParentTemplate: values.ParentTemplate = value; break;
            case Layer.StyleTrigger: values.StyleTrigger = value; break;
            case Layer.TemplateTrigger: values.TemplateTrigger = value; break;
            case Layer.StyleSetter: values.StyleSetter = value; break;
            case Layer.Current:
                values.Current = value;
                values.CurrentSource = source;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(layer), layer, null);
        }
    }

    private static object? GetLayerValue(LayerValues values, Layer layer) => layer switch
    {
        Layer.Local => values.Local,
        Layer.ParentTemplate => values.ParentTemplate,
        Layer.StyleTrigger => values.StyleTrigger,
        Layer.TemplateTrigger => values.TemplateTrigger,
        Layer.StyleSetter => values.StyleSetter,
        Layer.Current => values.Current,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null),
    };

    private static void ClearLayerValue(LayerValues values, Layer layer) =>
        SetLayerValue(values, layer, null);

    private static LayerMask ToMask(Layer layer) => layer switch
    {
        Layer.Local => LayerMask.Local,
        Layer.ParentTemplate => LayerMask.ParentTemplate,
        Layer.StyleTrigger => LayerMask.StyleTrigger,
        Layer.TemplateTrigger => LayerMask.TemplateTrigger,
        Layer.StyleSetter => LayerMask.StyleSetter,
        Layer.Current => LayerMask.Current,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null),
    };

    private static BaseValueSource GetFixedSource(Layer layer) => layer switch
    {
        Layer.Local => BaseValueSource.Local,
        Layer.ParentTemplate => BaseValueSource.ParentTemplate,
        Layer.StyleTrigger => BaseValueSource.StyleTrigger,
        Layer.TemplateTrigger => BaseValueSource.TemplateTrigger,
        Layer.StyleSetter => BaseValueSource.Style,
        _ => BaseValueSource.Unknown,
    };

    private static bool IsSingleLayer(LayerMask mask)
    {
        var bits = (byte)mask;
        return bits != 0 && (bits & (bits - 1)) == 0;
    }

    private static Layer GetSingleLayer(LayerMask mask)
    {
        if (!IsSingleLayer(mask))
            throw new InvalidOperationException("Expected exactly one dependency-property value layer.");

        return mask switch
        {
            LayerMask.Local => Layer.Local,
            LayerMask.ParentTemplate => Layer.ParentTemplate,
            LayerMask.StyleTrigger => Layer.StyleTrigger,
            LayerMask.TemplateTrigger => Layer.TemplateTrigger,
            LayerMask.StyleSetter => Layer.StyleSetter,
            LayerMask.Current => Layer.Current,
            _ => throw new InvalidOperationException("Unknown dependency-property value layer."),
        };
    }
}
