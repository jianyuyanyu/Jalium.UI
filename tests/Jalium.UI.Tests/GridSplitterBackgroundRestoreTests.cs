using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

[Collection("Application")]
public class GridSplitterBackgroundRestoreTests
{
    private static GridSplitter CreateHostedSplitter(Brush? background)
    {
        var splitter = new GridSplitter();
        if (background is not null)
            splitter.Background = background;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        return splitter;
    }

    private static void SimulateDrag(GridSplitter splitter)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0)
        {
            RoutedEvent = Thumb.DragStartedEvent,
            Source = splitter
        });
        splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false)
        {
            RoutedEvent = Thumb.DragCompletedEvent,
            Source = splitter
        });
    }

    [Fact]
    public void Drag_MustNotDestroyCallerSuppliedBackground()
    {
        var splitter = CreateHostedSplitter(Brushes.Transparent);

        SimulateDrag(splitter);

        Assert.Same(Brushes.Transparent, splitter.Background);
    }

    [Fact]
    public void Drag_MustNotLeaveLocalBackgroundBehindWhenCallerNeverSetOne()
    {
        var splitter = CreateHostedSplitter(null);

        SimulateDrag(splitter);

        Assert.Equal(DependencyProperty.UnsetValue, splitter.ReadLocalValue(Control.BackgroundProperty));
    }
}
