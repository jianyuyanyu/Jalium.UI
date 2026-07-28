using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Guards the layout behaviour a window resize depends on: an Auto scroll bar must
/// not flip on and off while the viewport is being dragged, and settling on a size
/// must not cost extra measure passes.
/// </summary>
/// <remarks>
/// The failure these pin down: content sized to exactly fill its viewport sits on
/// the Auto threshold. When the decision compared an extent produced by one measure
/// against a height supplied by a later arrange, shrinking the window read as
/// "overflowing" and showed the bar; growing it hid the bar again. Because a
/// reserved bar takes a gutter out of the width, every flip re-flowed the content
/// sideways, so dragging the bottom edge made the whole page shudder. Each flip also
/// wrote ScrollBar.Visibility during arrange, which invalidated the whole ancestor
/// chain and forced the layout manager to run another full measure+arrange round.
/// </remarks>
public sealed class ScrollViewerResizeStabilityTests
{
    /// <summary>Content that fills whatever height it is offered, but never less
    /// than its natural size — the shape that parks a viewer on the Auto threshold
    /// (a page whose star row stretches to the viewport).</summary>
    private sealed class ViewportFillingContent : FrameworkElement
    {
        public int MeasureCount { get; private set; }
        public Size LastAvailableSize { get; private set; }
        public double NaturalHeight { get; set; } = 400;

        protected override Size MeasureOverride(Size availableSize)
        {
            MeasureCount++;
            LastAvailableSize = availableSize;

            var height = double.IsInfinity(availableSize.Height)
                ? NaturalHeight
                : Math.Max(NaturalHeight, availableSize.Height);
            return new Size(120, height);
        }
    }

    /// <summary>
    /// Content that sizes itself to the viewer's reported viewport — the shape a
    /// page uses when it wants to fill the shell and scroll internally, and the one
    /// that actually broke. ViewportHeight is produced by arrange, so what this
    /// reports during measure is always one pass behind; the viewer must stay stable
    /// anyway.
    /// </summary>
    private sealed class ViewportTrackingContent : FrameworkElement
    {
        private readonly Func<double> _viewportHeight;

        public ViewportTrackingContent(Func<double> viewportHeight)
            => _viewportHeight = viewportHeight;

        public int MeasureCount { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            MeasureCount++;
            var viewport = _viewportHeight();
            var height = double.IsFinite(viewport) && viewport > 0
                ? viewport
                : (double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
            return new Size(120, height);
        }
    }

    private static ScrollViewer CreateViewer(FrameworkElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    // Detached trees have no LayoutManager, so nothing propagates a child's
    // invalidation up to the viewer. Invalidating explicitly reproduces what a real
    // layout pass does; without it Measure short-circuits on the unchanged
    // constraint and the viewer never re-reads its content at all.
    private static void LayoutAt(ScrollViewer viewer, double width, double height)
    {
        viewer.InvalidateMeasure();
        viewer.Measure(new Size(width, height));
        viewer.Arrange(new Rect(0, 0, width, height));
    }

    /// <summary>
    /// Lays out the way the real shell does: the viewer is measured with an
    /// unbounded height (it sits in a Grid cell whose row height depends on its
    /// content) and only learns its real height when it is arranged.
    /// </summary>
    private static void LayoutUnbounded(ScrollViewer viewer, double width, double height)
    {
        viewer.InvalidateMeasure();
        viewer.Measure(new Size(width, double.PositiveInfinity));
        viewer.Arrange(new Rect(0, 0, width, height));
    }

    [Fact]
    public void ShrinkingTheViewportOnePixel_DoesNotToggleTheAutoScrollBar()
    {
        // A page exactly as tall as its viewport: the boundary case.
        var content = new ViewportFillingContent { NaturalHeight = 0 };
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);
        var settled = viewer.ComputedVerticalScrollBarVisibility;

        // Dragging the bottom edge is a stream of ±1px viewport changes. None of
        // them may flip the bar: each flip moves a gutter and re-flows the content.
        foreach (var height in new double[] { 499, 500, 499, 498, 499, 500, 498, 500 })
        {
            LayoutAt(viewer, 300, height);
            Assert.Equal(settled, viewer.ComputedVerticalScrollBarVisibility);
        }
    }

    [Fact]
    public void ShrinkingTheViewport_DoesNotShowAScrollBarForContentThatFits()
    {
        var content = new ViewportFillingContent { NaturalHeight = 0 };
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 600);
        Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);

        // Monotonic shrink: the content keeps fitting, so the bar must stay away.
        foreach (var height in new double[] { 560, 520, 480, 440, 400, 360 })
        {
            LayoutAt(viewer, 300, height);
            Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
        }
    }

    [Fact]
    public void ContentTallerThanViewport_StillGetsItsScrollBarAndExtent()
    {
        var content = new ViewportFillingContent { NaturalHeight = 2000 };
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);

        Assert.Equal(Visibility.Visible, viewer.ComputedVerticalScrollBarVisibility);
        Assert.Equal(2000, viewer.ExtentHeight, precision: 3);
        Assert.True(viewer.ScrollableHeight > 0);
    }

    [Fact]
    public void SteadyStateRelayout_CostsNoExtraContentMeasures()
    {
        var content = new ViewportFillingContent { NaturalHeight = 0 };
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);
        var afterFirst = content.MeasureCount;

        // Re-laying out at the SAME size must not re-measure the content at all:
        // the constraint is unchanged, so UIElement.Measure short-circuits.
        LayoutAt(viewer, 300, 500);
        Assert.Equal(afterFirst, content.MeasureCount);
    }

    [Fact]
    public void EachResizeStep_MeasuresContentAtMostTwice()
    {
        var content = new ViewportFillingContent { NaturalHeight = 0 };
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);

        // One resize step may cost at most one settle pass plus one correction —
        // never an unbounded re-measure loop driven by the bar flipping.
        foreach (var height in new double[] { 480, 460, 440, 420 })
        {
            var before = content.MeasureCount;
            LayoutAt(viewer, 300, height);
            var spent = content.MeasureCount - before;
            Assert.True(
                spent <= 2,
                $"resize to {height} measured the content {spent} times");
        }
    }

    [Fact]
    public void ViewportTrackingPage_UnboundedMeasure_DoesNotToggleTheBarWhileResizing()
    {
        // The real shell shape: unbounded measure, and a page whose height follows
        // the viewport it was last arranged into.
        ScrollViewer? viewer = null;
        var content = new ViewportTrackingContent(() => viewer?.ViewportHeight ?? 0);
        viewer = CreateViewer(content);

        LayoutUnbounded(viewer, 300, 500);
        LayoutUnbounded(viewer, 300, 500);
        var settled = viewer.ComputedVerticalScrollBarVisibility;
        Assert.Equal(Visibility.Collapsed, settled);

        // Dragging the bottom edge. The page always ends up exactly viewport-sized,
        // so it never truly overflows and the bar must never appear.
        foreach (var height in new double[] { 499, 498, 496, 493, 489, 484, 489, 493, 496, 498, 500 })
        {
            LayoutUnbounded(viewer, 300, height);
            Assert.Equal(
                Visibility.Collapsed,
                viewer.ComputedVerticalScrollBarVisibility);
        }
    }

    [Fact]
    public void ViewportTrackingPage_UnboundedMeasure_ShrinkingDoesNotStickTheBarOn()
    {
        ScrollViewer? viewer = null;
        var content = new ViewportTrackingContent(() => viewer?.ViewportHeight ?? 0);
        viewer = CreateViewer(content);

        LayoutUnbounded(viewer, 300, 600);
        LayoutUnbounded(viewer, 300, 600);

        // A single large shrink, then settle. Even if a transient pass mis-reads the
        // stale height, the viewer must converge back to "no bar" — the reported bug
        // was that it latched on and only cleared after another nudge.
        LayoutUnbounded(viewer, 300, 420);
        LayoutUnbounded(viewer, 300, 420);

        Assert.Equal(
            Visibility.Collapsed,
            viewer.ComputedVerticalScrollBarVisibility);
    }

    /// <summary>
    /// The exact shape the shell page uses: during arrange it pins its own Height to
    /// the viewport it was just given. Writing Height invalidates measure, so the
    /// next measure sees a height produced by the *previous* arrange.
    /// </summary>
    private sealed class SelfPinningPage : FrameworkElement
    {
        private readonly Func<double> _viewportHeight;

        public SelfPinningPage(Func<double> viewportHeight)
            => _viewportHeight = viewportHeight;

        public int MeasureCount { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            MeasureCount++;
            var pinned = Height;
            var height = double.IsNaN(pinned)
                ? (double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height)
                : pinned;
            return new Size(120, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var viewport = _viewportHeight();
            if (double.IsFinite(viewport) && viewport > 0 &&
                (double.IsNaN(Height) || Math.Abs(Height - viewport) > 0.5))
            {
                Height = viewport;
            }

            return base.ArrangeOverride(finalSize);
        }
    }

    [Fact]
    public void SelfPinningPage_ResizeSequence_ConvergesWithoutShowingABar()
    {
        ScrollViewer? viewer = null;
        var content = new SelfPinningPage(() => viewer?.ViewportHeight ?? 0);
        viewer = CreateViewer(content);

        // Settle.
        for (var i = 0; i < 3; i++)
        {
            LayoutUnbounded(viewer, 300, 500);
        }

        Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);

        // Drag the bottom edge: one layout per step, because a drag never pauses to
        // let layout settle. The page only ever wants to be viewport-sized, so it
        // never genuinely overflows — but its pinned height still describes the
        // PREVIOUS viewport, so a viewer that trusts that stale extent will show a
        // bar on every shrinking step. That is the reported bug.
        foreach (var height in new double[] { 490, 470, 450, 430, 410, 390, 370 })
        {
            LayoutUnbounded(viewer, 300, height);

            Assert.Equal(
                Visibility.Collapsed,
                viewer.ComputedVerticalScrollBarVisibility);
        }
    }

    /// <summary>
    /// Content that measures itself against whatever height it is given — the shape
    /// a "fill the shell and scroll internally" page has once it is handed the
    /// viewport as a constraint instead of reading it back afterwards.
    /// </summary>
    private sealed class ConstraintSizedPage : FrameworkElement
    {
        public Size LastConstraint { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            LastConstraint = availableSize;
            var height = double.IsInfinity(availableSize.Height) ? 5000 : availableSize.Height;
            return new Size(120, height);
        }
    }

    [Fact]
    public void FillViewport_MeasuresTheContentWithTheViewportInsteadOfInfinity()
    {
        var content = new ConstraintSizedPage();
        ScrollViewer.SetFillViewport(content, FillViewportMode.Vertical);
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);

        // The whole point: a real height arrives as a constraint, in this pass.
        Assert.False(double.IsInfinity(content.LastConstraint.Height));
        Assert.Equal(500, content.LastConstraint.Height, precision: 3);
        Assert.Equal(500, viewer.ExtentHeight, precision: 3);
        Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
    }

    [Fact]
    public void FillViewport_TracksResizesExactlyWithNoLag()
    {
        var content = new ConstraintSizedPage();
        ScrollViewer.SetFillViewport(content, FillViewportMode.Vertical);
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);

        // One layout per step, as during a drag. Each step must land on the new
        // height immediately — the read-back approach was always one pass behind,
        // which left the page smaller than its box and visually centred in it.
        foreach (var height in new double[] { 470, 430, 380, 640, 500 })
        {
            LayoutAt(viewer, 300, height);

            Assert.Equal(height, content.LastConstraint.Height, precision: 3);
            Assert.Equal(height, viewer.ExtentHeight, precision: 3);
            Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);
        }
    }

    [Fact]
    public void FillViewport_LeavesTheOtherAxisAlone()
    {
        var content = new ConstraintSizedPage();
        ScrollViewer.SetFillViewport(content, FillViewportMode.Vertical);
        var viewer = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        LayoutAt(viewer, 300, 500);

        // Horizontal stays scrollable, so it keeps its unbounded measure.
        Assert.True(double.IsInfinity(content.LastConstraint.Width));
        Assert.False(double.IsInfinity(content.LastConstraint.Height));
    }

    [Fact]
    public void WithoutFillViewport_TheScrollableAxisStaysUnbounded()
    {
        // Guards the default contract: opting in must be required.
        var content = new ConstraintSizedPage();
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 500);

        Assert.True(double.IsInfinity(content.LastConstraint.Height));
    }

    [Fact]
    public void GrowingPastTheContent_HidesTheBarAndShrinkingBackShowsIt()
    {
        // A real overflow/fit transition must still work — the stability guards
        // above must not have been bought by making the bar sticky.
        var content = new ViewportFillingContent { NaturalHeight = 800 };
        var viewer = CreateViewer(content);

        LayoutAt(viewer, 300, 400);
        Assert.Equal(Visibility.Visible, viewer.ComputedVerticalScrollBarVisibility);

        LayoutAt(viewer, 300, 900);
        Assert.Equal(Visibility.Collapsed, viewer.ComputedVerticalScrollBarVisibility);

        LayoutAt(viewer, 300, 400);
        Assert.Equal(Visibility.Visible, viewer.ComputedVerticalScrollBarVisibility);
    }
}
