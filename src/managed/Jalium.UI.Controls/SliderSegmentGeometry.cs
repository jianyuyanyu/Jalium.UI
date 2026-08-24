using Jalium.UI.Media;

namespace Jalium.UI.Controls;

/// <summary>
/// Shared geometry and painting for the segmented ("stepped") slider track.
///
/// Both rendering paths go through here — the templated
/// <see cref="Jalium.UI.Controls.Primitives.SegmentedTrackBar"/> part used by the theme,
/// and the template-less fallback drawing inside <see cref="Slider"/> /
/// <see cref="RangeSlider"/> — so a code-only app and a themed app cannot drift apart.
///
/// Ratios handed to this class are always <em>visual</em> ratios: 0 is the start of the
/// track in the direction the thumb travels and 1 is its end, i.e. the caller has already
/// folded <c>IsDirectionReversed</c> in.
/// </summary>
internal static class SliderSegmentGeometry
{
    /// <summary>Gap, in DIPs, carved out between two neighbouring segments.</summary>
    internal const double DefaultSegmentGap = 3.0;

    /// <summary>
    /// Upper bound on generated segments. A pathological tick frequency must not turn a
    /// 200px track into thousands of draw calls.
    /// </summary>
    internal const int MaxSegmentCount = 256;

    /// <summary>
    /// A segment shorter than this cannot carry a gap and a fill at the same time, so the
    /// track silently degrades to the continuous look instead of turning into mush.
    /// </summary>
    internal const double MinSegmentLength = 3.0;

    private const double Epsilon = 1e-9;

    private static readonly double[] s_continuous = [0.0, 1.0];

    /// <summary>The boundary set of a track that has no interior segment breaks.</summary>
    public static double[] Continuous => s_continuous;

    /// <summary>
    /// Projects the tick definition onto sorted, de-duplicated visual boundary ratios.
    /// The result always starts at 0 and ends at 1, so <c>Length - 1</c> is the segment
    /// count and a return of <see cref="Continuous"/> means "no segmentation".
    /// </summary>
    public static double[] GetBoundaryRatios(
        double minimum,
        double maximum,
        IList<double>? ticks,
        double tickFrequency,
        bool isDirectionReversed)
    {
        var range = maximum - minimum;
        if (!double.IsFinite(range) || range <= 0)
        {
            return s_continuous;
        }

        List<double>? interior = null;

        if (ticks is { Count: > 0 })
        {
            foreach (var tick in ticks)
            {
                if (!double.IsFinite(tick)) continue;
                var ratio = (tick - minimum) / range;
                if (ratio <= Epsilon || ratio >= 1 - Epsilon) continue;
                (interior ??= new List<double>()).Add(isDirectionReversed ? 1 - ratio : ratio);
                if (interior.Count >= MaxSegmentCount) break;
            }
        }
        else if (double.IsFinite(tickFrequency) && tickFrequency > 0)
        {
            var count = (int)Math.Floor(range / tickFrequency + Epsilon);
            if (count > MaxSegmentCount)
            {
                return s_continuous;
            }

            for (var i = 1; i <= count; i++)
            {
                var ratio = i * tickFrequency / range;
                if (ratio <= Epsilon || ratio >= 1 - Epsilon) continue;
                (interior ??= new List<double>()).Add(isDirectionReversed ? 1 - ratio : ratio);
            }
        }

        if (interior is null || interior.Count == 0)
        {
            return s_continuous;
        }

        interior.Sort();

        var boundaries = new List<double>(interior.Count + 2) { 0.0 };
        foreach (var ratio in interior)
        {
            if (ratio - boundaries[^1] > Epsilon) boundaries.Add(ratio);
        }
        boundaries.Add(1.0);
        return boundaries.ToArray();
    }

    /// <summary>
    /// Paints the segmented track. <paramref name="activeLow"/>/<paramref name="activeHigh"/>
    /// are visual ratios describing the filled run; a segment they only partly cover is
    /// filled proportionally, which is what an unsnapped slider needs.
    /// </summary>
    public static void Draw(
        DrawingContext dc,
        Rect trackRect,
        Orientation orientation,
        double[] boundaries,
        double activeLow,
        double activeHigh,
        double gap,
        Brush? trackBrush,
        Brush? fillBrush)
    {
        if (dc is null || boundaries.Length < 2) return;
        if (trackRect.Width <= 0 || trackRect.Height <= 0) return;

        var axisLength = orientation == Orientation.Horizontal ? trackRect.Width : trackRect.Height;
        var thickness = orientation == Orientation.Horizontal ? trackRect.Height : trackRect.Width;
        if (axisLength <= 0 || thickness <= 0) return;

        gap = double.IsFinite(gap) && gap > 0 ? gap : 0;
        var segmentCount = boundaries.Length - 1;

        // The gaps are carved out of the segments, so the overall track keeps its length.
        // When that leaves nothing drawable, fall back to one continuous bar.
        if (segmentCount > 1)
        {
            var shortest = double.MaxValue;
            for (var i = 0; i < segmentCount; i++)
            {
                shortest = Math.Min(shortest, (boundaries[i + 1] - boundaries[i]) * axisLength - gap);
            }

            if (shortest < MinSegmentLength)
            {
                boundaries = s_continuous;
                segmentCount = 1;
                gap = 0;
            }
        }

        var radius = thickness / 2;
        activeLow = Math.Clamp(activeLow, 0, 1);
        activeHigh = Math.Clamp(activeHigh, activeLow, 1);

        for (var i = 0; i < segmentCount; i++)
        {
            var low = boundaries[i];
            var high = boundaries[i + 1];

            var segment = Inset(
                GetSpanRect(trackRect, orientation, low, high),
                orientation,
                i == 0 ? 0 : gap / 2,
                i == segmentCount - 1 ? 0 : gap / 2);

            if (segment.Width <= 0 || segment.Height <= 0) continue;

            if (trackBrush is not null)
            {
                dc.DrawRoundedRectangle(trackBrush, null, segment, radius, radius);
            }

            if (fillBrush is null) continue;

            var fillLow = Math.Max(low, activeLow);
            var fillHigh = Math.Min(high, activeHigh);
            if (fillHigh - fillLow <= Epsilon) continue;

            var fill = fillLow <= low + Epsilon && fillHigh >= high - Epsilon
                ? segment
                : ClampToSegment(GetSpanRect(trackRect, orientation, fillLow, fillHigh), segment, orientation);

            if (fill.Width <= 0 || fill.Height <= 0) continue;

            dc.DrawRoundedRectangle(fillBrush, null, fill, radius, radius);
        }
    }

    /// <summary>
    /// Maps a visual ratio span onto the track. For a vertical track the low ratio sits at
    /// the bottom, matching the slider convention that value increases upwards.
    /// </summary>
    public static Rect GetSpanRect(Rect trackRect, Orientation orientation, double lowRatio, double highRatio)
    {
        if (orientation == Orientation.Horizontal)
        {
            return new Rect(
                trackRect.X + trackRect.Width * lowRatio,
                trackRect.Y,
                Math.Max(0, trackRect.Width * (highRatio - lowRatio)),
                trackRect.Height);
        }

        return new Rect(
            trackRect.X,
            trackRect.Y + trackRect.Height * (1 - highRatio),
            trackRect.Width,
            Math.Max(0, trackRect.Height * (highRatio - lowRatio)));
    }

    private static Rect Inset(Rect rect, Orientation orientation, double lowInset, double highInset)
    {
        if (orientation == Orientation.Horizontal)
        {
            var width = rect.Width - lowInset - highInset;
            return width <= 0 ? new Rect(rect.X, rect.Y, 0, 0) : new Rect(rect.X + lowInset, rect.Y, width, rect.Height);
        }

        var height = rect.Height - lowInset - highInset;
        return height <= 0 ? new Rect(rect.X, rect.Y, 0, 0) : new Rect(rect.X, rect.Y + highInset, rect.Width, height);
    }

    private static Rect ClampToSegment(Rect span, Rect segment, Orientation orientation)
    {
        if (orientation == Orientation.Horizontal)
        {
            var x = Math.Max(span.X, segment.X);
            var right = Math.Min(span.X + span.Width, segment.X + segment.Width);
            return new Rect(x, segment.Y, Math.Max(0, right - x), segment.Height);
        }

        var y = Math.Max(span.Y, segment.Y);
        var bottom = Math.Min(span.Y + span.Height, segment.Y + segment.Height);
        return new Rect(segment.X, y, segment.Width, Math.Max(0, bottom - y));
    }
}
