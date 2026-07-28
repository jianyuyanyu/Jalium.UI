using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;

namespace Jalium.UI.Tests;

/// <summary>
/// Locks the first-frame viewport-recovery contract across every built-in
/// virtualizing layout engine.
/// </summary>
public sealed class VirtualizingViewportRecoveryCoverageTests
{
    private const double ItemWidth = 100;
    private const double ItemHeight = 50;
    private const double ViewportWidth = 300;
    private const double ViewportHeight = 300;
    private const int ItemCount = 60;

    [Fact]
    public void EveryViewportOwningVirtualizer_UsesAnAuditedEngine()
    {
        var controlsAssembly = typeof(VirtualizingPanel).Assembly;
        var unaudited = controlsAssembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                typeof(VirtualizingPanel).IsAssignableFrom(type) &&
                typeof(IScrollInfo).IsAssignableFrom(type) &&
                !typeof(VirtualizingStackPanel).IsAssignableFrom(type) &&
                !typeof(VirtualizingWrapPanel).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .OrderBy(name => name)
            .ToArray();

        Assert.Empty(unaudited);

        var eagerPanels = controlsAssembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                type.BaseType == typeof(VirtualizingPanel) &&
                !typeof(IScrollInfo).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal(
            [typeof(DataGridCellsPanel)],
            eagerPanels);
    }

    [Theory]
    [InlineData(Engine.Stack, Orientation.Vertical, 1)]
    [InlineData(Engine.Stack, Orientation.Horizontal, 1)]
    [InlineData(Engine.Wrap, Orientation.Horizontal, 3)]
    [InlineData(Engine.Wrap, Orientation.Vertical, 6)]
    [InlineData(Engine.DataGridRows, Orientation.Vertical, 1)]
    public void FirstFrame_RecoversBothScrollAxesWithoutInput(
        Engine engine,
        Orientation orientation,
        int expectedBlindRealized)
    {
        var setup = CreateSetup(engine, orientation);

        setup.Owner.Measure(setup.UnboundedMeasure);
        var blindRealized = setup.Panel.Children.Count;
        Assert.Equal(expectedBlindRealized, blindRealized);

        setup.Panel.Arrange(setup.FullBounds);
        Assert.False(
            setup.Panel.IsMeasureValid,
            $"{engine}/{orientation} did not request viewport recovery.");

        setup.Panel.Measure(setup.UnboundedMeasure);
        setup.Panel.Arrange(setup.FullBounds);

        Assert.True(setup.Panel.IsMeasureValid);
        Assert.InRange(
            setup.Panel.Children.Count,
            blindRealized + 1,
            ItemCount - 1);
        Assert.NotNull(
            setup.Owner.ItemContainerGenerator.ContainerFromIndex(
                blindRealized));
        Assert.Null(
            setup.Owner.ItemContainerGenerator.ContainerFromIndex(
                ItemCount - 1));

        for (var pass = 0; pass < 3; pass++)
        {
            setup.Panel.Measure(setup.UnboundedMeasure);
            setup.Panel.Arrange(setup.FullBounds);
            Assert.True(
                setup.Panel.IsMeasureValid,
                $"{engine}/{orientation} recovery looped on pass {pass}.");
        }
    }

    [Theory]
    [InlineData(Engine.Stack, Orientation.Vertical)]
    [InlineData(Engine.Stack, Orientation.Horizontal)]
    [InlineData(Engine.Wrap, Orientation.Horizontal)]
    [InlineData(Engine.Wrap, Orientation.Vertical)]
    public void Recovery_LatchesWhenMeasureCannotAdoptArrangeViewport(
        Engine engine,
        Orientation orientation)
    {
        var setup = CreateSetup(engine, orientation);
        var constrainedMeasure = setup.CreateMeasure(axisSize: 100);

        setup.Owner.Measure(constrainedMeasure);
        setup.Panel.Arrange(setup.FullBounds);
        Assert.False(setup.Panel.IsMeasureValid);

        // A custom parent can repeatedly measure with a smaller finite
        // constraint and still arrange a larger slot. Recovery may be
        // requested once, but must not ping-pong forever.
        setup.Panel.Measure(constrainedMeasure);
        setup.Panel.Arrange(setup.FullBounds);
        Assert.True(
            setup.Panel.IsMeasureValid,
            $"{engine}/{orientation} repeatedly requested an impossible " +
            "viewport recovery.");

        for (var pass = 0; pass < 3; pass++)
        {
            setup.Panel.Measure(constrainedMeasure);
            setup.Panel.Arrange(setup.FullBounds);
            Assert.True(setup.Panel.IsMeasureValid);
        }
    }

    [Theory]
    [InlineData(Engine.Stack, Orientation.Vertical)]
    [InlineData(Engine.Stack, Orientation.Horizontal)]
    [InlineData(Engine.Wrap, Orientation.Horizontal)]
    [InlineData(Engine.Wrap, Orientation.Vertical)]
    public void ShrinkingViewport_DoesNotRequestRecovery(
        Engine engine,
        Orientation orientation)
    {
        var setup = CreateSetup(engine, orientation);

        setup.Owner.Measure(setup.UnboundedMeasure);
        setup.Panel.Arrange(setup.FullBounds);
        setup.Panel.Measure(setup.UnboundedMeasure);
        setup.Panel.Arrange(setup.FullBounds);
        Assert.True(setup.Panel.IsMeasureValid);

        setup.Panel.Measure(setup.UnboundedMeasure);
        setup.Panel.Arrange(setup.CreateBounds(axisSize: 100));

        Assert.True(
            setup.Panel.IsMeasureValid,
            $"{engine}/{orientation} treated a smaller viewport as missing " +
            "realization.");
    }

    [Fact]
    public void DataGridCellsPanel_IsEagerWithinEachRealizedRow()
    {
        var panel = new DataGridCellsPanel();
        for (var index = 0; index < 8; index++)
        {
            panel.Children.Add(
                new Border
                {
                    Width = ItemWidth,
                    Height = ItemHeight
                });
        }

        panel.Measure(
            new Size(
                double.PositiveInfinity,
                ItemHeight));
        panel.Arrange(
            new Rect(
                0,
                0,
                ViewportWidth,
                ItemHeight));

        Assert.False(panel is IScrollInfo);
        Assert.Equal(8, panel.Children.Count);
        Assert.True(panel.IsMeasureValid);
        Assert.All(
            panel.Children.Cast<UIElement>(),
            child =>
            {
                Assert.Equal(ItemWidth, child.VisualBounds.Width);
                Assert.Equal(ItemHeight, child.VisualBounds.Height);
            });
    }

    private static Setup CreateSetup(
        Engine engine,
        Orientation orientation)
    {
        var owner = new ProbeItemsControl(
            engine,
            orientation,
            ItemCount);
        var scrollsVertically =
            engine == Engine.Wrap
                ? orientation == Orientation.Horizontal
                : orientation == Orientation.Vertical;

        return new Setup(
            owner,
            owner.Panel,
            scrollsVertically);
    }

    public enum Engine
    {
        Stack,
        Wrap,
        DataGridRows
    }

    private sealed class ProbeItemsControl : ItemsControl
    {
        private readonly Engine _engine;
        private readonly Orientation _orientation;

        public ProbeItemsControl(
            Engine engine,
            Orientation orientation,
            int itemCount)
        {
            _engine = engine;
            _orientation = orientation;
            VirtualizingPanel.SetCacheLength(
                this,
                new VirtualizationCacheLength(0));
            ItemsSource = Enumerable
                .Range(0, itemCount)
                .Select(index => $"Item {index}")
                .ToArray();
        }

        public VirtualizingPanel Panel =>
            (VirtualizingPanel)ItemsHost!;

        protected override Panel CreateItemsPanel()
        {
            return _engine switch
            {
                Engine.Stack => new VirtualizingStackPanel
                {
                    Orientation = _orientation
                },
                Engine.Wrap => new VirtualizingWrapPanel
                {
                    Orientation = _orientation,
                    ItemWidth = ItemWidth,
                    ItemHeight = ItemHeight
                },
                Engine.DataGridRows => new DataGridRowsPresenter(),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        protected override FrameworkElement GetContainerForItem(
            object item) =>
            new Border
            {
                Width = ItemWidth,
                MinWidth = ItemWidth,
                Height = ItemHeight,
                MinHeight = ItemHeight
            };
    }

    private readonly record struct Setup(
        ProbeItemsControl Owner,
        VirtualizingPanel Panel,
        bool ScrollsVertically)
    {
        public Size UnboundedMeasure =>
            CreateMeasure(double.PositiveInfinity);

        public Rect FullBounds =>
            CreateBounds(
                ScrollsVertically
                    ? ViewportHeight
                    : ViewportWidth);

        public Size CreateMeasure(double axisSize) =>
            ScrollsVertically
                ? new Size(ViewportWidth, axisSize)
                : new Size(axisSize, ViewportHeight);

        public Rect CreateBounds(double axisSize) =>
            ScrollsVertically
                ? new Rect(0, 0, ViewportWidth, axisSize)
                : new Rect(0, 0, axisSize, ViewportHeight);
    }
}
