using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

public sealed class PopupHitTestTests
{
    [Fact]
    public void ClosedPopupPlaceholder_DoesNotCoverSiblingUnderIt()
    {
        var target = new Border
        {
            Width = 100,
            Height = 40,
            Background = new SolidColorBrush(Color.Black),
        };
        var popup = new Popup
        {
            Child = new Border
            {
                Width = 80,
                Height = 30,
            },
        };
        var root = new Grid
        {
            Width = 100,
            Height = 40,
        };
        root.Children.Add(target);
        root.Children.Add(popup);

        root.Measure(new Size(100, 40));
        root.Arrange(new Rect(0, 0, 100, 40));

        Assert.Same(target, root.HitTest(new Point(20, 20))?.VisualHit);
    }
}
