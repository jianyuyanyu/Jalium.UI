using Jalium.UI;
using Jalium.UI.Controls;
using System.Reflection;

namespace Jalium.UI.Tests;

public class GridLayoutTests
{
    [Fact]
    public void Grid_RepeatedLayoutReusesImplicitDefinitionsAndTrackBuffers()
    {
        var grid = new Grid();
        grid.Children.Add(new Border { Width = 80, Height = 24 });
        grid.Measure(new Size(320, 200));
        grid.Arrange(new Rect(0, 0, 320, 200));

        var fieldNames = new[]
        {
            "_effectiveRowDefinitions",
            "_effectiveColumnDefinitions",
            "_rowHeights",
            "_columnWidths",
            "_rowStarValues",
            "_columnStarValues",
            "_rowContent",
            "_columnContent",
        };
        var fields = fieldNames.Select(name => typeof(Grid).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!).ToArray();
        var originalStorage = fields.Select(field => field.GetValue(grid)).ToArray();

        for (var pass = 0; pass < 8; pass++)
        {
            var width = (pass & 1) == 0 ? 321 : 320;
            grid.Measure(new Size(width, 200));
            grid.Arrange(new Rect(0, 0, width, 200));
        }

        for (var index = 0; index < fields.Length; index++)
        {
            Assert.Same(originalStorage[index], fields[index].GetValue(grid));
        }

        Assert.False(grid.ShouldSerializeRowDefinitions());
        Assert.False(grid.ShouldSerializeColumnDefinitions());
    }

    [Fact]
    public void Grid_AutoRow_ShouldTrackChildHeight_AfterFinalCellWidthMeasure()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 120 });

        var text = new TextBlock
        {
            Text = string.Join(" ", Enumerable.Repeat("longword", 40)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        };

        grid.Children.Add(text);
        grid.Measure(new Size(500, double.PositiveInfinity));

        Assert.True(text.DesiredSize.Height > 30, $"Expected wrapped text height, got {text.DesiredSize.Height}");
        Assert.True(
            grid.DesiredSize.Height + 0.01 >= text.DesiredSize.Height,
            $"Grid height {grid.DesiredSize.Height} should include wrapped text height {text.DesiredSize.Height}");
    }

    // ---- Star-track DesiredSize regression suite ------------------------------------------------
    // A star track must report its CONTENT size as desired (not its full proportional allocation)
    // when measured under a finite constraint; otherwise a Grid balloons to fill any content-sizing
    // parent (WrapPanel / horizontal StackPanel / auto-sized Border|Button). The star allocation is
    // applied only at arrange. See Grid.MeasureOverride.

    [Fact]
    public void Grid_BareGrid_FiniteMeasure_ReportsContentDesiredSize_NotFullAvailable()
    {
        // Bare Grid => one implicit Star row + one implicit Star column.
        var grid = new Grid();
        grid.Children.Add(new Border { Width = 80, Height = 24 });

        grid.Measure(new Size(1000, 500));

        Assert.True(Math.Abs(grid.DesiredSize.Width - 80) < 1,
            $"Expected content width 80, got {grid.DesiredSize.Width}");
        Assert.True(Math.Abs(grid.DesiredSize.Height - 24) < 1,
            $"Expected content height 24, got {grid.DesiredSize.Height}");
    }

    [Fact]
    public void Grid_ExplicitStarTracks_FiniteMeasure_ReportsContentDesiredSize()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
        grid.Children.Add(new Border { Width = 120, Height = 40 });

        grid.Measure(new Size(1000, 800));

        Assert.True(Math.Abs(grid.DesiredSize.Width - 120) < 1,
            $"Expected content width 120, got {grid.DesiredSize.Width}");
        Assert.True(Math.Abs(grid.DesiredSize.Height - 40) < 1,
            $"Expected content height 40, got {grid.DesiredSize.Height}");
    }

    [Fact]
    public void Grid_StarTracks_FillAvailable_AtArrange()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
        grid.Children.Add(new Border { Width = 120, Height = 40 });

        grid.Measure(new Size(1000, 800));
        grid.Arrange(new Rect(0, 0, 1000, 800));

        // Content-based desired must NOT prevent star tracks from filling at arrange.
        Assert.True(Math.Abs(grid.ColumnDefinitions[0].ActualWidth - 1000) < 1,
            $"Star column should fill 1000 at arrange, got {grid.ColumnDefinitions[0].ActualWidth}");
        Assert.True(Math.Abs(grid.RowDefinitions[0].ActualHeight - 800) < 1,
            $"Star row should fill 800 at arrange, got {grid.RowDefinitions[0].ActualHeight}");
    }

    [Fact]
    public void Grid_StarTrack_InfiniteMeasure_ReportsContentDesiredSize()
    {
        // Pre-existing "treat star as Auto under infinity" behavior must be preserved.
        var grid = new Grid();
        grid.Children.Add(new Border { Width = 80, Height = 24 });

        grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.True(Math.Abs(grid.DesiredSize.Width - 80) < 1,
            $"Expected content width 80, got {grid.DesiredSize.Width}");
        Assert.True(Math.Abs(grid.DesiredSize.Height - 24) < 1,
            $"Expected content height 24, got {grid.DesiredSize.Height}");
    }

    [Fact]
    public void Grid_StarColumn_MinWidth_RaisesContentDesiredWidth()
    {
        // A star column's content-based desired must still honour an explicit MinWidth.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star, MinWidth = 200 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new Border { Width = 50, Height = 30 });

        grid.Measure(new Size(1000, double.PositiveInfinity));

        Assert.True(grid.DesiredSize.Width >= 200 - 0.01,
            $"Star column MinWidth 200 should floor desired width, got {grid.DesiredSize.Width}");
    }

    [Fact]
    public void Grid_StarRowUnderInfiniteHeight_UsesResolvedColumnWidth()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });

        var content = new MeasureConstraintProbe();
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        grid.Measure(new Size(300, double.PositiveInfinity));

        Assert.Single(content.Constraints);
        Assert.Equal(200, content.Constraints[0].Width, precision: 3);
        Assert.True(
            double.IsPositiveInfinity(content.Constraints[0].Height));
    }

    [Fact]
    public void Grid_StarColumnUnderInfiniteWidth_UsesResolvedRowHeight()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Star });

        var content = new MeasureConstraintProbe();
        Grid.SetRow(content, 1);
        grid.Children.Add(content);

        grid.Measure(new Size(double.PositiveInfinity, 200));

        Assert.NotEmpty(content.Constraints);
        Assert.All(
            content.Constraints,
            constraint => Assert.Equal(
                140,
                constraint.Height,
                precision: 3));
    }

    [Fact]
    public void Grid_ArrangeAtDifferentWidth_RedistributesWithoutMeasuringChildren()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(376) });

        var content = new MeasureConstraintProbe();
        grid.Children.Add(content);

        grid.Measure(new Size(1000, 200));
        Assert.Single(content.Constraints);
        Assert.Equal(624, content.Constraints[0].Width, 3);

        // Arrange may receive a different width, but it must only redistribute
        // the measured tracks. Measuring the subtree here overwrites
        // ScrollViewer/IScrollInfo extent state halfway through arrange.
        grid.Arrange(new Rect(0, 0, 1200, 200));

        Assert.Single(content.Constraints);
        Assert.Equal(824, content.VisualBounds.Width, 3);
        Assert.True(content.IsMeasureValid);
        Assert.False(grid.IsMeasureValid);
        Assert.Equal(
            824,
            grid.ColumnDefinitions[0].ActualWidth,
            3);
    }

    [Fact]
    public void Grid_HeightOnlyArrange_ReusesUnboundedMeasureForViewportRecovery()
    {
        var grid = new Grid();
        var content = new MeasureConstraintProbe();
        grid.Children.Add(content);

        grid.Measure(
            new Size(300, double.PositiveInfinity));
        Assert.Single(content.Constraints);

        grid.Arrange(new Rect(0, 0, 300, 200));

        Assert.Single(content.Constraints);
        Assert.Equal(200, content.VisualBounds.Height, 3);
        Assert.Equal(
            double.PositiveInfinity,
            content.Constraints[0].Height);
    }

    [Fact]
    public void Grid_HeightOnlyArrange_ReallocatesZeroStarRowFromItsMinimum()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(0, GridUnitType.Star)
        });
        grid.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Star });

        var content = new Border { Height = 80 };
        grid.Children.Add(content);

        grid.Measure(
            new Size(300, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, 300, 200));

        Assert.Equal(0, grid.RowDefinitions[0].ActualHeight, 3);
        Assert.Equal(200, grid.RowDefinitions[1].ActualHeight, 3);
    }

    [Fact]
    public void Grid_MixedAutoAndStarSpan_DoesNotAbsorbScrollableExtentIntoAutoRows()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Star });
        grid.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });

        var header = new Border { Height = 40 };
        var scrollableContent = new ScrollExtentProbe();
        var footer = new Border { Height = 30 };
        Grid.SetRow(scrollableContent, 0);
        Grid.SetRowSpan(scrollableContent, 3);
        Grid.SetRow(footer, 2);
        grid.Children.Add(scrollableContent);
        grid.Children.Add(header);
        grid.Children.Add(footer);

        grid.Measure(new Size(500, 800));
        grid.Arrange(new Rect(0, 0, 500, 800));

        Assert.DoesNotContain(
            scrollableContent.Constraints,
            constraint =>
                double.IsPositiveInfinity(constraint.Height));
        Assert.Equal(40, grid.RowDefinitions[0].ActualHeight, 3);
        Assert.Equal(730, grid.RowDefinitions[1].ActualHeight, 3);
        Assert.Equal(30, grid.RowDefinitions[2].ActualHeight, 3);
        Assert.Equal(800, grid.DesiredSize.Height, 3);
    }

    [Fact]
    public void Grid_StarMinMaxBounds_RedistributeSpaceAcrossRemainingTracks()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Star,
            MinWidth = 80
        });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Star,
            MaxWidth = 30
        });

        grid.Measure(new Size(180, 40));
        grid.Arrange(new Rect(0, 0, 180, 40));

        Assert.Equal(80, grid.ColumnDefinitions[0].ActualWidth, 3);
        Assert.Equal(70, grid.ColumnDefinitions[1].ActualWidth, 3);
        Assert.Equal(30, grid.ColumnDefinitions[2].ActualWidth, 3);
    }

    [Fact]
    public void Grid_SpanningStarChild_ContributesToDesiredSize()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });

        var content = new Border { Width = 180, Height = 24 };
        Grid.SetColumnSpan(content, 2);
        grid.Children.Add(content);

        grid.Measure(new Size(500, 100));

        Assert.Equal(180, grid.DesiredSize.Width, 3);
        Assert.Equal(24, grid.DesiredSize.Height, 3);
    }

    [Fact]
    public void Grid_CellAttachedPropertyChange_InvalidatesParentMeasure()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });
        grid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Star });
        var child = new Border { Width = 20, Height = 20 };
        grid.Children.Add(child);
        grid.Measure(new Size(200, 40));
        grid.Arrange(new Rect(0, 0, 200, 40));

        Assert.True(grid.IsMeasureValid);

        Grid.SetColumn(child, 1);

        Assert.False(grid.IsMeasureValid);
        Assert.False(grid.IsArrangeValid);
    }

    [Fact]
    public void WrapPanel_WithBareGridChildren_SizesToContent_DoesNotBalloon()
    {
        // Reproduces the chip-explosion scenario: bare Grid wrappers inside a horizontal WrapPanel
        // measured with a finite width and infinite height.
        var wrap = new WrapPanel();
        for (int i = 0; i < 3; i++)
        {
            var g = new Grid();
            g.Children.Add(new Border { Width = 60, Height = 20 });
            wrap.Children.Add(g);
        }

        wrap.Measure(new Size(1000, double.PositiveInfinity));

        // Three 60-wide chips fit on a single 20-tall line. Before the fix each bare Grid reported
        // 1000 wide, forcing one chip per line => the panel ballooned to ~3 lines tall and full width.
        Assert.True(wrap.DesiredSize.Height < 40,
            $"WrapPanel ballooned vertically: height={wrap.DesiredSize.Height}");
        Assert.True(wrap.DesiredSize.Width < 500,
            $"WrapPanel ballooned horizontally: width={wrap.DesiredSize.Width}");
    }

    private sealed class MeasureConstraintProbe : FrameworkElement
    {
        public List<Size> Constraints { get; } = [];

        protected override Size MeasureOverride(Size availableSize)
        {
            Constraints.Add(availableSize);
            return new Size(50, 40);
        }
    }

    private sealed class ScrollExtentProbe : FrameworkElement
    {
        public List<Size> Constraints { get; } = [];

        protected override Size MeasureOverride(Size availableSize)
        {
            Constraints.Add(availableSize);
            return new Size(
                100,
                double.IsPositiveInfinity(availableSize.Height)
                    ? 8000
                    : Math.Min(8000, availableSize.Height));
        }
    }

}
