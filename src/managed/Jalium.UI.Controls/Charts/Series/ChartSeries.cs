using System.Collections;
using System.Collections.Specialized;
using Jalium.UI.Media;

namespace Jalium.UI.Controls.Charts;

/// <summary>
/// Abstract base class for all chart series.
/// </summary>
public abstract class ChartSeries : DependencyObject
{
    private Dictionary<INotifyCollectionChanged, int>? _observedCollections;

    #region Dependency Properties

    /// <summary>
    /// Identifies the Title dependency property.
    /// </summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ChartSeries),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the Brush dependency property.
    /// </summary>
    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(ChartSeries),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the StrokeBrush dependency property.
    /// </summary>
    public static readonly DependencyProperty StrokeBrushProperty =
        DependencyProperty.Register(nameof(StrokeBrush), typeof(Brush), typeof(ChartSeries),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the StrokeThickness dependency property.
    /// </summary>
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(ChartSeries),
            new PropertyMetadata(2.0));

    /// <summary>
    /// Identifies the IsVisible dependency property.
    /// </summary>
    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.Register(nameof(IsVisible), typeof(bool), typeof(ChartSeries),
            new PropertyMetadata(true));

    /// <summary>
    /// Identifies the ItemsSource dependency property.
    /// </summary>
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ChartSeries),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the XBindingPath dependency property.
    /// </summary>
    public static readonly DependencyProperty XBindingPathProperty =
        DependencyProperty.Register(nameof(XBindingPath), typeof(string), typeof(ChartSeries),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the YBindingPath dependency property.
    /// </summary>
    public static readonly DependencyProperty YBindingPathProperty =
        DependencyProperty.Register(nameof(YBindingPath), typeof(string), typeof(ChartSeries),
            new PropertyMetadata(null));

    #endregion

    #region CLR Properties

    /// <summary>
    /// Gets or sets the title of this series, used in legends.
    /// </summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Gets or sets the fill brush for this series.
    /// </summary>
    public Brush? Brush
    {
        get => (Brush?)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>
    /// Gets or sets the stroke brush for this series.
    /// </summary>
    public Brush? StrokeBrush
    {
        get => (Brush?)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    /// <summary>
    /// Gets or sets the stroke thickness for this series.
    /// </summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty)!;
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <summary>
    /// Gets or sets whether this series is visible.
    /// </summary>
    public bool IsVisible
    {
        get => (bool)GetValue(IsVisibleProperty)!;
        set => SetValue(IsVisibleProperty, value);
    }

    /// <summary>
    /// Gets or sets the data source for this series.
    /// </summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Gets or sets the property path for X values in the data source.
    /// </summary>
    public string? XBindingPath
    {
        get => (string?)GetValue(XBindingPathProperty);
        set => SetValue(XBindingPathProperty, value);
    }

    /// <summary>
    /// Gets or sets the property path for Y values in the data source.
    /// </summary>
    public string? YBindingPath
    {
        get => (string?)GetValue(YBindingPathProperty);
        set => SetValue(YBindingPathProperty, value);
    }

    #endregion

    /// <inheritdoc />
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldCollection)
        {
            StopObservingCollection(oldCollection);
        }

        base.OnPropertyChanged(e);

        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            ObserveCollection(newCollection);
        }

        if (BindingMentor is ChartBase chart)
        {
            chart.InvalidateVisual();
        }
    }

    private void ObserveCollection(INotifyCollectionChanged collection)
    {
        _observedCollections ??= new Dictionary<INotifyCollectionChanged, int>(
            ReferenceEqualityComparer.Instance);

        if (_observedCollections.TryGetValue(collection, out var referenceCount))
        {
            _observedCollections[collection] = referenceCount + 1;
            return;
        }

        _observedCollections[collection] = 1;
        CollectionChangedEventManager.AddHandler(
            collection,
            OnObservedCollectionChanged);
    }

    private void StopObservingCollection(INotifyCollectionChanged collection)
    {
        if (_observedCollections == null ||
            !_observedCollections.TryGetValue(collection, out var referenceCount))
        {
            return;
        }

        if (referenceCount > 1)
        {
            _observedCollections[collection] = referenceCount - 1;
            return;
        }

        CollectionChangedEventManager.RemoveHandler(
            collection,
            OnObservedCollectionChanged);
        _observedCollections.Remove(collection);
        if (_observedCollections.Count == 0)
        {
            _observedCollections = null;
        }
    }

    private void OnObservedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (BindingMentor is ChartBase chart)
        {
            chart.InvalidateVisual();
        }
    }
}
