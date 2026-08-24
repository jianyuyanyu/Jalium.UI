using Jalium.UI;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Media;

namespace Jalium.UI.Controls.Primitives;

/// <summary>
/// Draws a slider track split into discrete segments — one per tick interval — separated
/// by a small gap. Segments covered by the active range are painted with
/// <see cref="Fill"/>, the rest with <see cref="TrackBrush"/>.
///
/// This is the template counterpart of <see cref="TickBar"/>: the theme drops it into the
/// <see cref="Jalium.UI.Controls.Slider"/> / <see cref="Jalium.UI.Controls.RangeSlider"/>
/// template as <c>PART_Segments</c> and the owning control pushes its range state here,
/// which keeps the segmented look themeable (hover/pressed/disabled brushes stay ordinary
/// template triggers).
/// </summary>
public class SegmentedTrackBar : FrameworkElement
{
    #region Static Brushes

    private static readonly SolidColorBrush s_defaultTrackBrush = new(Color.FromRgb(60, 60, 60));
    private static readonly SolidColorBrush s_defaultFillBrush = new(ThemeColors.SliderThumb);
    private const string TrackBrushKey = "SliderTrack";
    private const string FillBrushKey = "AccentBrush";

    #endregion

    #region Dependency Properties

    /// <summary>
    /// Identifies the Orientation dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(SegmentedTrackBar),
            new PropertyMetadata(Orientation.Horizontal, OnLayoutPropertyChanged),
            value => value is Orientation orientation && Enum.IsDefined(orientation));

    /// <summary>
    /// Identifies the Minimum dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the Maximum dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(100.0, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the RangeStart dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public static readonly DependencyProperty RangeStartProperty =
        DependencyProperty.Register(nameof(RangeStart), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the RangeEnd dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public static readonly DependencyProperty RangeEndProperty =
        DependencyProperty.Register(nameof(RangeEnd), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the Ticks dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public static readonly DependencyProperty TicksProperty =
        DependencyProperty.Register(nameof(Ticks), typeof(DoubleCollection), typeof(SegmentedTrackBar),
            new PropertyMetadata(null, OnTicksChanged));

    /// <summary>
    /// Identifies the TickFrequency dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public static readonly DependencyProperty TickFrequencyProperty =
        DependencyProperty.Register(nameof(TickFrequency), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the SegmentGap dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public static readonly DependencyProperty SegmentGapProperty =
        DependencyProperty.Register(nameof(SegmentGap), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(SliderSegmentGeometry.DefaultSegmentGap, OnVisualPropertyChanged),
            IsNonNegativeDouble);

    /// <summary>
    /// Identifies the TrackThickness dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public static readonly DependencyProperty TrackThicknessProperty =
        DependencyProperty.Register(nameof(TrackThickness), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(4.0, OnLayoutPropertyChanged), IsNonNegativeDouble);

    /// <summary>
    /// Identifies the ReservedSpace dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public static readonly DependencyProperty ReservedSpaceProperty =
        DependencyProperty.Register(nameof(ReservedSpace), typeof(double), typeof(SegmentedTrackBar),
            new PropertyMetadata(0.0, OnVisualPropertyChanged), IsNonNegativeDouble);

    /// <summary>
    /// Identifies the Fill dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SegmentedTrackBar),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the TrackBrush dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(SegmentedTrackBar),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the IsDirectionReversed dependency property.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.State)]
    public static readonly DependencyProperty IsDirectionReversedProperty =
        DependencyProperty.Register(nameof(IsDirectionReversed), typeof(bool), typeof(SegmentedTrackBar),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    #endregion

    #region CLR Properties

    /// <summary>
    /// Gets or sets the axis the track runs along.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty)!;
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>
    /// Gets or sets the value mapped to the start of the track.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty)!;
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>
    /// Gets or sets the value mapped to the end of the track.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty)!;
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// Gets or sets the value where the filled run starts.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public double RangeStart
    {
        get => (double)GetValue(RangeStartProperty)!;
        set => SetValue(RangeStartProperty, value);
    }

    /// <summary>
    /// Gets or sets the value where the filled run ends.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public double RangeEnd
    {
        get => (double)GetValue(RangeEndProperty)!;
        set => SetValue(RangeEndProperty, value);
    }

    /// <summary>
    /// Gets or sets explicit segment boundaries. When non-empty this wins over
    /// <see cref="TickFrequency"/>.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public DoubleCollection? Ticks
    {
        get => (DoubleCollection?)GetValue(TicksProperty);
        set => SetValue(TicksProperty, value);
    }

    /// <summary>
    /// Gets or sets the interval that splits the track into segments.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Other)]
    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty)!;
        set => SetValue(TickFrequencyProperty, value);
    }

    /// <summary>
    /// Gets or sets the gap, in DIPs, carved out between neighbouring segments. The gap is
    /// taken from the segments, so the overall track keeps its length.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public double SegmentGap
    {
        get => (double)GetValue(SegmentGapProperty)!;
        set => SetValue(SegmentGapProperty, value);
    }

    /// <summary>
    /// Gets or sets the thickness of the track across its axis.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public double TrackThickness
    {
        get => (double)GetValue(TrackThicknessProperty)!;
        set => SetValue(TrackThicknessProperty, value);
    }

    /// <summary>
    /// Gets or sets the space reserved for the thumb; the track is inset by half of it on
    /// both ends so segments line up with the thumb travel.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Layout)]
    public double ReservedSpace
    {
        get => (double)GetValue(ReservedSpaceProperty)!;
        set => SetValue(ReservedSpaceProperty, value);
    }

    /// <summary>
    /// Gets or sets the brush for segments inside the active range.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>
    /// Gets or sets the brush for segments outside the active range.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.Appearance)]
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the direction of increasing value is reversed.
    /// </summary>
    [DevToolsPropertyCategory(DevToolsPropertyCategory.State)]
    public bool IsDirectionReversed
    {
        get => (bool)GetValue(IsDirectionReversedProperty)!;
        set => SetValue(IsDirectionReversedProperty, value);
    }

    #endregion

    #region Layout

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var thickness = Math.Max(0, TrackThickness);

        if (Orientation == Orientation.Horizontal)
        {
            var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 0;
            return new Size(width, thickness);
        }

        var height = double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 0;
        return new Size(thickness, height);
    }

    #endregion

    #region Rendering

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, RenderSize.Width, RenderSize.Height);
        var trackRect = ControlRenderGeometry.GetCenteredTrackRect(
            bounds, Orientation, ReservedSpace, TrackThickness);

        if (trackRect.Width <= 0 || trackRect.Height <= 0) return;

        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(
            Minimum, Maximum, (DoubleCollection?)GetValue(TicksProperty), TickFrequency, IsDirectionReversed);

        var low = GetVisualRatio(RangeStart);
        var high = GetVisualRatio(RangeEnd);

        SliderSegmentGeometry.Draw(
            drawingContext,
            trackRect,
            Orientation,
            boundaries,
            Math.Min(low, high),
            Math.Max(low, high),
            SegmentGap,
            ResolveBrush(TrackBrush, TrackBrushKey, s_defaultTrackBrush),
            ResolveBrush(Fill, FillBrushKey, s_defaultFillBrush));
    }

    private double GetVisualRatio(double value)
    {
        var range = Maximum - Minimum;
        var ratio = double.IsFinite(range) && range > 0 ? Math.Clamp((value - Minimum) / range, 0, 1) : 0;
        return IsDirectionReversed ? 1 - ratio : ratio;
    }

    private Brush ResolveBrush(Brush? local, string resourceKey, Brush fallback)
    {
        if (local is not null) return local;

        if (TryFindResource(resourceKey) is Brush themeBrush) return themeBrush;

        if (Application.Current?.Resources.TryGetValue(resourceKey, out var appResource) == true &&
            appResource is Brush appBrush)
        {
            return appBrush;
        }

        return fallback;
    }

    #endregion

    #region Property Changed Callbacks

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedTrackBar bar)
        {
            bar.InvalidateVisual();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentedTrackBar bar)
        {
            bar.InvalidateMeasure();
            bar.InvalidateVisual();
        }
    }

    private static void OnTicksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SegmentedTrackBar bar) return;

        // The collection is mutable and shared with the owning slider; track it directly so
        // an in-place edit repaints the segments too.
        if (e.OldValue is DoubleCollection oldTicks) oldTicks.Changed -= bar.OnTicksCollectionChanged;
        if (e.NewValue is DoubleCollection newTicks) newTicks.Changed += bar.OnTicksCollectionChanged;
        bar.InvalidateVisual();
    }

    private void OnTicksCollectionChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static bool IsNonNegativeDouble(object? value) =>
        value is double number && double.IsFinite(number) && number >= 0;

    #endregion
}
