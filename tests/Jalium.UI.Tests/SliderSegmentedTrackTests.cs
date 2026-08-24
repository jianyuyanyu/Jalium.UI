using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Markup;
using Jalium.UI.Media;
using MediaDoubleCollection = Jalium.UI.Media.DoubleCollection;
using TypeConverterRegistry = Jalium.UI.Markup.TypeConverterRegistry;

namespace Jalium.UI.Tests;

[Collection("Application")]
public class SliderSegmentedTrackTests
{
    private const double Tolerance = 1e-6;

    #region Boundary math

    [Fact]
    public void BoundaryRatios_FromTickFrequency_SplitTheTrackEvenly()
    {
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 20, isDirectionReversed: false);

        Assert.Equal(new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 }, boundaries.Select(r => Math.Round(r, 6)));
    }

    [Fact]
    public void BoundaryRatios_WithoutTicks_ReportContinuous()
    {
        Assert.Same(SliderSegmentGeometry.Continuous,
            SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 0, isDirectionReversed: false));

        // A degenerate range cannot be segmented either.
        Assert.Same(SliderSegmentGeometry.Continuous,
            SliderSegmentGeometry.GetBoundaryRatios(5, 5, null, 1, isDirectionReversed: false));
    }

    [Fact]
    public void BoundaryRatios_FromExplicitTicks_AreSortedDedupedAndClampedToTheInterior()
    {
        var ticks = new MediaDoubleCollection { 75, 25, 25, 0, 100, 250, double.NaN };

        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, ticks, 10, isDirectionReversed: false);

        // Explicit ticks win over TickFrequency; out-of-range and duplicate entries drop out.
        Assert.Equal(new[] { 0.0, 0.25, 0.75, 1.0 }, boundaries.Select(r => Math.Round(r, 6)));
    }

    [Fact]
    public void BoundaryRatios_Reversed_MirrorTheInteriorBreaks()
    {
        var ticks = new MediaDoubleCollection { 20 };

        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, ticks, 0, isDirectionReversed: true);

        Assert.Equal(new[] { 0.0, 0.8, 1.0 }, boundaries.Select(r => Math.Round(r, 6)));
    }

    [Fact]
    public void BoundaryRatios_PathologicalTickFrequency_FallsBackToContinuous()
    {
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(
            0, 100_000, null, 1, isDirectionReversed: false);

        Assert.Same(SliderSegmentGeometry.Continuous, boundaries);
    }

    #endregion

    #region Painting

    [Fact]
    public void Draw_CarvesTheGapOutOfTheSegments_SoTheTrackKeepsItsLength()
    {
        var track = new Rect(8, 12, 200, 4);
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 25, isDirectionReversed: false);

        var rects = Record(dc => SliderSegmentGeometry.Draw(
            dc, track, Orientation.Horizontal, boundaries, 0, 0, gap: 4,
            trackBrush: Brushes.Gray, fillBrush: null));

        Assert.Equal(4, rects.Count);

        // Outer edges stay put; only the interior joints lose half a gap each.
        Assert.Equal(track.X, rects[0].X, Tolerance);
        Assert.Equal(track.X + track.Width, rects[^1].X + rects[^1].Width, Tolerance);

        Assert.Equal(48, rects[0].Width, Tolerance);   // 50 - gap/2
        Assert.Equal(46, rects[1].Width, Tolerance);   // 50 - gap
        Assert.Equal(46, rects[2].Width, Tolerance);
        Assert.Equal(48, rects[3].Width, Tolerance);

        for (var i = 1; i < rects.Count; i++)
        {
            var previousRight = rects[i - 1].X + rects[i - 1].Width;
            Assert.Equal(4, rects[i].X - previousRight, Tolerance);
        }
    }

    [Fact]
    public void Draw_PaintsCoveredSegmentsWithTheFillBrush()
    {
        var track = new Rect(0, 0, 200, 4);
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 25, isDirectionReversed: false);

        var drawings = RecordDrawings(dc => SliderSegmentGeometry.Draw(
            dc, track, Orientation.Horizontal, boundaries, 0, 0.5, gap: 4,
            trackBrush: Brushes.Gray, fillBrush: Brushes.Red));

        // 4 track segments + a fill for each of the two segments below the value.
        Assert.Equal(6, drawings.Count);
        Assert.Equal(2, drawings.Count(d => ReferenceEquals(d.Brush, Brushes.Red)));

        // The fill of a fully covered segment is exactly the segment.
        var firstSegment = ((RectangleGeometry)drawings[0].Geometry!).Rect;
        var firstFill = ((RectangleGeometry)drawings[1].Geometry!).Rect;
        Assert.Equal(firstSegment.X, firstFill.X, Tolerance);
        Assert.Equal(firstSegment.Width, firstFill.Width, Tolerance);
    }

    [Fact]
    public void Draw_PartlyCoveredSegment_IsFilledProportionally()
    {
        var track = new Rect(0, 0, 200, 4);
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 50, isDirectionReversed: false);

        var drawings = RecordDrawings(dc => SliderSegmentGeometry.Draw(
            dc, track, Orientation.Horizontal, boundaries, 0, 0.25, gap: 0,
            trackBrush: Brushes.Gray, fillBrush: Brushes.Red));

        var fill = drawings.Single(d => ReferenceEquals(d.Brush, Brushes.Red));
        var rect = ((RectangleGeometry)fill.Geometry!).Rect;

        Assert.Equal(0, rect.X, Tolerance);
        Assert.Equal(50, rect.Width, Tolerance);
    }

    [Fact]
    public void Draw_SegmentsTooShortForTheTrack_DegradeToOneContinuousBar()
    {
        var track = new Rect(0, 0, 40, 4);
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 5, isDirectionReversed: false);

        var rects = Record(dc => SliderSegmentGeometry.Draw(
            dc, track, Orientation.Horizontal, boundaries, 0, 1, gap: 3,
            trackBrush: Brushes.Gray, fillBrush: null));

        var single = Assert.Single(rects);
        Assert.Equal(track.X, single.X, Tolerance);
        Assert.Equal(track.Width, single.Width, Tolerance);
    }

    [Fact]
    public void Draw_Vertical_PutsTheLowRatioAtTheBottom()
    {
        var track = new Rect(10, 0, 4, 200);
        var boundaries = SliderSegmentGeometry.GetBoundaryRatios(0, 100, null, 50, isDirectionReversed: false);

        var drawings = RecordDrawings(dc => SliderSegmentGeometry.Draw(
            dc, track, Orientation.Vertical, boundaries, 0, 0.5, gap: 0,
            trackBrush: Brushes.Gray, fillBrush: Brushes.Red));

        var fill = drawings.Single(d => ReferenceEquals(d.Brush, Brushes.Red));
        var rect = ((RectangleGeometry)fill.Geometry!).Rect;

        Assert.Equal(100, rect.Y, Tolerance);
        Assert.Equal(100, rect.Height, Tolerance);
    }

    [Fact]
    public void SegmentedTrackBar_RendersPillSegmentsAlignedWithTheThumbTravel()
    {
        var bar = new SegmentedTrackBar
        {
            Orientation = Orientation.Horizontal,
            Minimum = 0,
            Maximum = 100,
            RangeStart = 0,
            RangeEnd = 50,
            TickFrequency = 25,
            SegmentGap = 4,
            TrackThickness = 4,
            ReservedSpace = 16,
            TrackBrush = Brushes.Gray,
            Fill = Brushes.Red,
        };

        bar.Measure(new Size(240, 32));
        bar.Arrange(new Rect(0, 0, 240, 32));

        var drawings = RecordDrawings(dc => InvokeOnRender(bar, dc));
        var geometries = drawings.Select(d => (RectangleGeometry)d.Geometry!).ToList();

        // Thumb travel is 240 - 16, so the track spans [8, 232] and sits vertically centred.
        Assert.All(geometries, g => Assert.Equal(14, g.Rect.Y, Tolerance));
        Assert.All(geometries, g => Assert.Equal(4, g.Rect.Height, Tolerance));
        Assert.All(geometries, g => Assert.Equal(2, g.RadiusX, Tolerance));

        var track = drawings.Where(d => ReferenceEquals(d.Brush, Brushes.Gray)).ToList();
        Assert.Equal(4, track.Count);
        Assert.Equal(8, ((RectangleGeometry)track[0].Geometry!).Rect.X, Tolerance);
        Assert.Equal(54, ((RectangleGeometry)track[0].Geometry!).Rect.Width, Tolerance);
        Assert.Equal(52, ((RectangleGeometry)track[1].Geometry!).Rect.Width, Tolerance);

        var lastTrack = ((RectangleGeometry)track[^1].Geometry!).Rect;
        Assert.Equal(232, lastTrack.X + lastTrack.Width, Tolerance);

        // RangeEnd = 50 lands on a boundary, so exactly the first two segments fill.
        Assert.Equal(2, drawings.Count(d => ReferenceEquals(d.Brush, Brushes.Red)));
    }

    #endregion

    #region Control wiring

    [Fact]
    public void Slider_SegmentedTemplatePart_ReplacesTheContinuousTrack()
    {
        ResetApplicationState();
        ThemeLoader.Initialize();
        var app = new Application();

        try
        {
            var slider = CreateThemedSlider(app);
            var host = Host(slider, 240, 32);

            var segments = FindNamedDescendant<SegmentedTrackBar>(slider, "PART_Segments");
            var trackRectangle = FindNamedDescendant<FrameworkElement>(slider, "PART_Track");
            var selectionRange = FindNamedDescendant<FrameworkElement>(slider, "PART_SelectionRange");

            Assert.NotNull(segments);
            Assert.NotNull(trackRectangle);
            Assert.NotNull(selectionRange);

            // Continuous is the default: the rectangles paint, the segment bar stays out.
            Assert.Equal(Visibility.Collapsed, segments.Visibility);
            Assert.Equal(Visibility.Visible, trackRectangle.Visibility);

            slider.TrackMode = SliderTrackMode.Segmented;
            host.Measure(new Size(240, 32));
            host.Arrange(new Rect(0, 0, 240, 32));

            Assert.Equal(Visibility.Visible, segments.Visibility);
            Assert.Equal(Visibility.Collapsed, trackRectangle.Visibility);
            Assert.Equal(Visibility.Collapsed, selectionRange.Visibility);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Slider_MirrorsItsRangeStateOntoTheSegmentPart()
    {
        ResetApplicationState();
        ThemeLoader.Initialize();
        var app = new Application();

        try
        {
            var slider = CreateThemedSlider(app);
            slider.TrackMode = SliderTrackMode.Segmented;
            slider.Minimum = 0;
            slider.Maximum = 100;
            slider.TickFrequency = 20;
            slider.SegmentGap = 5;
            slider.Value = 65;

            var host = Host(slider, 240, 32);

            var segments = FindNamedDescendant<SegmentedTrackBar>(slider, "PART_Segments");
            Assert.NotNull(segments);

            Assert.Equal(0, segments.Minimum, Tolerance);
            Assert.Equal(100, segments.Maximum, Tolerance);
            Assert.Equal(20, segments.TickFrequency, Tolerance);
            Assert.Equal(5, segments.SegmentGap, Tolerance);
            Assert.Equal(0, segments.RangeStart, Tolerance);
            Assert.Equal(65, segments.RangeEnd, Tolerance);
            Assert.Equal(16, segments.ReservedSpace, Tolerance);

            slider.Value = 30;
            host.Measure(new Size(240, 32));
            Assert.Equal(30, segments.RangeEnd, Tolerance);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void RangeSlider_SegmentedTemplatePart_TracksBothThumbs()
    {
        ResetApplicationState();
        ThemeLoader.Initialize();
        var app = new Application();

        try
        {
            var slider = new RangeSlider
            {
                Minimum = 0,
                Maximum = 100,
                RangeStart = 20,
                RangeEnd = 80,
                TickFrequency = 20,
                TrackMode = SliderTrackMode.Segmented,
                Width = 240,
                Height = 32,
            };
            slider.Style = Assert.IsType<Style>(app.Resources[typeof(RangeSlider)]);

            Host(slider, 240, 32);

            var segments = FindNamedDescendant<SegmentedTrackBar>(slider, "PART_Segments");
            var trackRectangle = FindNamedDescendant<FrameworkElement>(slider, "PART_Track");

            Assert.NotNull(segments);
            Assert.NotNull(trackRectangle);
            Assert.Equal(Visibility.Visible, segments.Visibility);
            Assert.Equal(Visibility.Collapsed, trackRectangle.Visibility);
            Assert.Equal(20, segments.RangeStart, Tolerance);
            Assert.Equal(80, segments.RangeEnd, Tolerance);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void DoubleCollectionAttributes_ConvertFromMarkup()
    {
        // The registry - not the [TypeConverter] attribute - is what ConvertValue consults,
        // so an unregistered DoubleCollection silently converted to null.
        var converted = Assert.IsType<MediaDoubleCollection>(
            TypeConverterRegistry.ConvertValue("10, 30, 60", typeof(MediaDoubleCollection)));
        Assert.Equal(new[] { 10.0, 30.0, 60.0 }, converted);

        const string xaml = """
            <Slider xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Minimum="0" Maximum="100" Ticks="10, 30, 60" />
            """;

        var slider = Assert.IsType<Slider>(XamlReader.Parse(xaml));
        Assert.Equal(new[] { 10.0, 30.0, 60.0 }, slider.Ticks);

        Assert.Equal(
            new[] { 0.0, 0.1, 0.3, 0.6, 1.0 },
            SliderSegmentGeometry
                .GetBoundaryRatios(0, 100, slider.Ticks, 0, isDirectionReversed: false)
                .Select(r => Math.Round(r, 6)));
    }

    [Fact]
    public void Slider_RejectsAnUndefinedTrackModeAndANegativeGap()
    {
        var slider = new Slider();

        Assert.Throws<ArgumentException>(() => slider.SetValue(Slider.TrackModeProperty, (SliderTrackMode)42));
        Assert.Throws<ArgumentException>(() => slider.SegmentGap = -1);
    }

    #endregion

    #region Helpers

    private static Slider CreateThemedSlider(Application app)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 40,
            TickFrequency = 20,
            Width = 240,
            Height = 32,
        };
        slider.Style = Assert.IsType<Style>(app.Resources[typeof(Slider)]);
        return slider;
    }

    private static StackPanel Host(FrameworkElement element, double width, double height)
    {
        var host = new StackPanel { Width = width, Height = height };
        host.Children.Add(element);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        element.ApplyTemplate();
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        return host;
    }

    private static void InvokeOnRender(FrameworkElement element, DrawingContext dc)
    {
        var onRender = element.GetType().GetMethod(
            "OnRender",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(DrawingContext)]);
        Assert.NotNull(onRender);
        onRender.Invoke(element, [dc]);
    }

    private static List<GeometryDrawing> RecordDrawings(Action<DrawingContext> draw)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            draw(dc);
        }

        return group.Children.OfType<GeometryDrawing>().ToList();
    }

    private static List<Rect> Record(Action<DrawingContext> draw) =>
        RecordDrawings(draw)
            .Select(d => ((RectangleGeometry)d.Geometry!).Rect)
            .ToList();

    private static void ResetApplicationState()
    {
        var currentField = typeof(Application).GetField("_current",
            BindingFlags.NonPublic | BindingFlags.Static);
        currentField?.SetValue(null, null);

        var resetMethod = typeof(ThemeManager).GetMethod("Reset",
            BindingFlags.NonPublic | BindingFlags.Static);
        resetMethod?.Invoke(null, null);
    }

    private static T? FindNamedDescendant<T>(Visual root, string name) where T : FrameworkElement
    {
        if (root is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
        {
            return match;
        }

        for (int i = 0; i < root.VisualChildrenCount; i++)
        {
            if (root.GetVisualChild(i) is Visual child)
            {
                var result = FindNamedDescendant<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    #endregion
}
