using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;

namespace Jalium.UI.Tests;

/// <summary>
/// Covers layout isolation: an element whose host lays it out at a rectangle the
/// host derives itself cannot affect anything above that host, so its invalidations
/// stop there instead of dirtying the chain up to the window.
/// </summary>
/// <remarks>
/// The behaviour this buys: a scroll bar appearing, disappearing, or running its
/// auto-hide animation used to invalidate measure on every ancestor, which made the
/// layout manager run another full measure+arrange round for a change that provably
/// moved nothing outside the scroll viewer.
/// <para>
/// These tests deliberately poke the propagation helpers directly. The interactions
/// worth pinning are the ones where stopping early could strand work: the isolated
/// element must still be queued itself, and the walk must still queue the host.
/// </para>
/// </remarks>
public sealed class LayoutIsolationTests
{
    private sealed class Host : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in Children)
            {
                child.Measure(availableSize);
            }

            return new Size(100, 100);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in Children)
            {
                child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            }

            return finalSize;
        }
    }

    private sealed class IsolatedChild : FrameworkElement
    {
        internal override bool IsLayoutIsolated => true;

        protected override Size MeasureOverride(Size availableSize) => new(10, 10);
    }

    private sealed class OrdinaryChild : FrameworkElement
    {
        protected override Size MeasureOverride(Size availableSize) => new(10, 10);
    }

    private static (Host root, Host middle) BuildTree(UIElement leaf)
    {
        var root = new Host();
        var middle = new Host();
        root.Children.Add(middle);
        middle.Children.Add(leaf);

        root.Measure(new Size(100, 100));
        root.Arrange(new Rect(0, 0, 100, 100));
        return (root, middle);
    }

    private static object CreateLayoutManager()
        => Activator.CreateInstance(
            typeof(UIElement).Assembly.GetType("Jalium.UI.LayoutManager")!,
            nonPublic: true)!;

    private static void Invoke(object layoutManager, string method, UIElement element)
        => layoutManager.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(layoutManager, [element]);

    private static bool IsQueued(object layoutManager, string field, UIElement element)
    {
        var queue = layoutManager.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(layoutManager)!;
        return ((System.Collections.IEnumerable)queue).Cast<UIElement>().Contains(element);
    }

    // NOTE: these drive the LayoutManager directly rather than calling
    // element.InvalidateMeasure(). A detached tree has no layout manager to find, so
    // an element-level invalidation propagates nowhere at all — a test written that
    // way passes for isolated AND ordinary elements alike and proves nothing.

    [Fact]
    public void IsolatedChild_DoesNotQueueItsAncestors()
    {
        var leaf = new IsolatedChild();
        var (root, middle) = BuildTree(leaf);
        var manager = CreateLayoutManager();

        Invoke(manager, "InvalidateMeasure", leaf);

        Assert.False(IsQueued(manager, "_measureQueue", middle));
        Assert.False(IsQueued(manager, "_measureQueue", root));
        Assert.False(IsQueued(manager, "_arrangeQueue", root));
    }

    [Fact]
    public void OrdinaryChild_StillQueuesItsAncestors()
    {
        // Isolation is opt-in: an ordinary child must keep dirtying the chain, or
        // panels would stop reacting to their children resizing.
        var leaf = new OrdinaryChild();
        var (root, middle) = BuildTree(leaf);
        var manager = CreateLayoutManager();

        Invoke(manager, "InvalidateMeasure", leaf);

        Assert.True(IsQueued(manager, "_measureQueue", middle));
        Assert.True(IsQueued(manager, "_measureQueue", root));
    }

    [Fact]
    public void IsolatedChild_DoesNotQueueAncestorsForArrangeEither()
    {
        var leaf = new IsolatedChild();
        var (root, middle) = BuildTree(leaf);
        var manager = CreateLayoutManager();

        Invoke(manager, "InvalidateArrange", leaf);

        Assert.True(IsQueued(manager, "_arrangeQueue", leaf));
        Assert.False(IsQueued(manager, "_arrangeQueue", middle));
        Assert.False(IsQueued(manager, "_arrangeQueue", root));
    }

    [Fact]
    public void IsolatedChild_IsStillQueuedForItsOwnLayout()
    {
        // Stopping propagation must not strand the element: the queue add happens
        // before the walk, so it still gets measured and arranged.
        var leaf = new IsolatedChild();
        var (_, _) = BuildTree(leaf);
        var manager = CreateLayoutManager();

        Invoke(manager, "InvalidateMeasure", leaf);

        Assert.True(IsQueued(manager, "_measureQueue", leaf));
        Assert.True(IsQueued(manager, "_arrangeQueue", leaf));
    }

    [Fact]
    public void PropagationStopsAtAnIsolatedHost_ButStillQueuesThatHost()
    {
        // A non-isolated element under an isolated host: the host itself must be
        // queued (it has to re-run), but nothing above it.
        var isolatedHost = new IsolatedHostPanel();
        var inner = new OrdinaryChild();
        isolatedHost.Children.Add(inner);

        var root = new Host();
        var middle = new Host();
        root.Children.Add(middle);
        middle.Children.Add(isolatedHost);
        root.Measure(new Size(100, 100));
        root.Arrange(new Rect(0, 0, 100, 100));

        var manager = CreateLayoutManager();
        Invoke(manager, "InvalidateMeasure", inner);

        Assert.True(IsQueued(manager, "_measureQueue", inner));
        Assert.True(IsQueued(manager, "_measureQueue", isolatedHost));
        Assert.False(IsQueued(manager, "_measureQueue", middle));
        Assert.False(IsQueued(manager, "_measureQueue", root));
    }

    private sealed class IsolatedHostPanel : Panel
    {
        internal override bool IsLayoutIsolated => true;

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in Children)
            {
                child.Measure(availableSize);
            }

            return new Size(20, 20);
        }
    }

    [Fact]
    public void ScrollViewerOwnedBars_AreIsolated_TemplateBarsAreNot()
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 50 } };
        var bar = (ScrollBar)typeof(ScrollViewer)
            .GetField("_verticalScrollBar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

        Assert.True(GetIsLayoutIsolated(bar));
        // A bar someone puts in a template is an ordinary child and must keep
        // propagating, or its host would never react to it.
        Assert.False(GetIsLayoutIsolated(new ScrollBar()));
    }

    private static bool GetIsLayoutIsolated(UIElement element)
        => (bool)typeof(UIElement)
            .GetProperty("IsLayoutIsolated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(element)!;

    [Fact]
    public void TogglingAnOwnedBarsVisibility_LeavesTheViewerItselfValid()
    {
        // End to end: flipping an Auto bar used to invalidate the viewer (and from
        // there every ancestor), buying a whole extra measure+arrange round for a
        // change that moves nothing outside the viewer.
        var viewer = new ScrollViewer
        {
            Content = new Border { Height = 40 },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        viewer.Measure(new Size(200, 200));
        viewer.Arrange(new Rect(0, 0, 200, 200));

        var bar = (ScrollBar)typeof(ScrollViewer)
            .GetField("_verticalScrollBar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewer)!;

        Assert.True(viewer.IsMeasureValid);

        bar.Visibility = bar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        // The bar itself still re-lays out; the viewer above it does not.
        Assert.False(bar.IsMeasureValid);
        Assert.True(viewer.IsMeasureValid);
    }
}
