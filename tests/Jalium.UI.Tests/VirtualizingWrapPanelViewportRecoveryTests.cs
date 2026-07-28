using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Covers measure running before anyone knows the viewport.
/// </summary>
/// <remarks>
/// A ScrollViewer measures its content with an unbounded scroll axis, so on the first
/// frame this panel has no viewport at all. That collapses the realization window —
/// and the cache band with it, since a Page-based cache length is a multiple of the
/// viewport — down to a single row. Both the forward and backward cache ranges then
/// come out empty, which the panel reads as "everything requested is realized", so it
/// never queues a catch-up pass. Arrange is the first moment a real size exists, and
/// it must be able to say so; otherwise the panel stays frozen at one row for good.
/// <para>
/// Layout here is driven element-first, the way LayoutManager drives it: re-measuring
/// through the parent short-circuits on an unchanged constraint and never reaches the
/// panel, which hides the whole effect.
/// </para>
/// </remarks>
[Collection("Application")]
public sealed class VirtualizingWrapPanelViewportRecoveryTests
{
    private const double CellWidth = 100;
    private const double CellHeight = 50;
    private const double PanelWidth = 300;   // three columns
    private const double PanelHeight = 300;  // six rows

    /// <summary>Hosts the panel with no ControlTemplate, so these tests do not depend
    /// on a theme being loaded.</summary>
    private sealed class ProbeItemsControl : ItemsControl
    {
        public ProbeItemsControl(int itemCount)
        {
            ItemsPanel = new ItemsPanelTemplate { PanelType = typeof(VirtualizingWrapPanel) };
            ItemsSource = Enumerable.Range(0, itemCount).Select(i => $"item {i}").ToList();
        }

        public VirtualizingWrapPanel Panel => (VirtualizingWrapPanel)ItemsHost!;

        // Pinned on both axes: with no theme an unpinned container collapses to zero
        // and virtualization stops meaning anything. Item size is left to be inferred
        // from this rather than set on the panel, which keeps ItemWidth/ItemHeight from
        // invalidating measure and muddying the IsMeasureValid assertions below.
        protected override FrameworkElement GetContainerForItem(object item)
            => new Border
            {
                Width = CellWidth,
                MinWidth = CellWidth,
                Height = CellHeight,
                MinHeight = CellHeight,
            };
    }

    [Fact]
    public void FirstFrameWithoutAViewport_RecoversOnceArrangeRevealsOne()
    {
        var owner = new ProbeItemsControl(itemCount: 60);

        // The shape a ScrollViewer produces: unbounded scroll axis, so no viewport.
        owner.Measure(new Size(PanelWidth, double.PositiveInfinity));
        var panel = owner.Panel;
        var blindRealized = panel.Children.Count;

        // One row only — three columns at 300px wide.
        Assert.Equal(3, blindRealized);

        // Arrange is the first time a real height exists.
        panel.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));

        Assert.False(
            panel.IsMeasureValid,
            "arrange saw a viewport that measure never had, and did not ask to be " +
            "re-measured — the panel is stuck at one row permanently");

        // LayoutManager re-measures the invalidated element with its own last constraint.
        panel.Measure(new Size(PanelWidth, double.PositiveInfinity));

        Assert.True(
            panel.Children.Count > blindRealized,
            $"still {panel.Children.Count} realized after the viewport was revealed");

        // Six visible rows of three.
        Assert.True(panel.Children.Count >= 18, $"only {panel.Children.Count} realized");
    }

    [Fact]
    public void ViewportRecovery_StopsRequestingOnceItHasBeenServed()
    {
        // Termination guard: the request is latched per viewport size, so a steady
        // layout must settle instead of ping-ponging measure against arrange.
        var owner = new ProbeItemsControl(itemCount: 12);
        var constraint = new Size(PanelWidth, double.PositiveInfinity);
        var bounds = new Rect(0, 0, PanelWidth, PanelHeight);

        owner.Measure(constraint);
        var panel = owner.Panel;
        panel.Arrange(bounds);
        panel.Measure(constraint);
        panel.Arrange(bounds);

        for (var i = 0; i < 5; i++)
        {
            panel.Measure(constraint);
            panel.Arrange(bounds);

            Assert.True(
                panel.IsMeasureValid,
                $"pass {i} left measure invalidated — recovery is looping");
        }
    }

    [Fact]
    public void ShrinkingViewport_DoesNotRequestRecovery()
    {
        // Recovery exists for a viewport that turned out larger than measure assumed.
        // A smaller one already has every container it needs.
        var owner = new ProbeItemsControl(itemCount: 12);
        var constraint = new Size(PanelWidth, double.PositiveInfinity);

        owner.Measure(constraint);
        var panel = owner.Panel;
        panel.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));
        panel.Measure(constraint);
        panel.Arrange(new Rect(0, 0, PanelWidth, PanelHeight));
        Assert.True(panel.IsMeasureValid);

        panel.Measure(constraint);
        panel.Arrange(new Rect(0, 0, PanelWidth, CellHeight * 2));

        Assert.True(panel.IsMeasureValid);
    }
}
