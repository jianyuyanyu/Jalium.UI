using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Documents;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

/// <summary>
/// Keyboard focus must not survive on an element that has left the tree it was focused in,
/// been hidden, disabled or made non-focusable (WPF <c>KeyboardDevice.ReevaluateFocusAsync</c>
/// parity). The pass is deferred to the dispatcher so a detach + re-attach inside one turn is a
/// no-op; tests pump the dispatcher explicitly to observe the outcome.
/// </summary>
[Collection("Application")]
public class KeyboardFocusRevalidationTests
{
    [Fact]
    public void PageSwap_DetachedFocusedElement_LosesKeyboardFocusOnNextDispatcherTurn()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var page1 = new StackPanel();
            page1.Children.Add(probe);
            var host = new Border { Child = page1 };
            var window = CreateWindow(host);

            Assert.True(probe.Focus());
            Assert.True(probe.IsKeyboardFocused);
            Assert.True(window.IsKeyboardFocusWithin);

            host.Child = new StackPanel();

            // Deferred: nothing moves inside the same turn.
            Assert.Same(probe, Keyboard.FocusedElement);

            PumpDispatcher();

            Assert.Null(Keyboard.FocusedElement);
            Assert.False(probe.IsKeyboardFocused);
            Assert.False(probe.IsKeyboardFocusWithin);
            Assert.False(page1.IsKeyboardFocusWithin);
            Assert.False(host.IsKeyboardFocusWithin);
            Assert.False(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void DetachedFocusedSubtree_FocusFallsBackToNearestConnectedFocusableAncestor()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var middle = new StackPanel();
            middle.Children.Add(probe);
            var outer = new FocusableDecorator { Child = middle };
            var window = CreateWindow(outer);

            Assert.True(probe.Focus());

            outer.Child = null;
            PumpDispatcher();

            Assert.Same(outer, Keyboard.FocusedElement);
            Assert.True(outer.IsKeyboardFocused);
            Assert.False(probe.IsKeyboardFocused);
            Assert.False(middle.IsKeyboardFocusWithin);
            Assert.True(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void DetachAndReattachWithinOneTurn_KeepsFocusAndMovesFocusWithinFlags()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var subtree = new StackPanel();
            subtree.Children.Add(probe);
            var left = new Border { Child = subtree };
            var right = new Border();
            var root = new StackPanel();
            root.Children.Add(left);
            root.Children.Add(right);
            var window = CreateWindow(root);

            Assert.True(probe.Focus());
            Assert.True(left.IsKeyboardFocusWithin);
            Assert.False(right.IsKeyboardFocusWithin);

            left.Child = null;
            right.Child = subtree;
            PumpDispatcher();

            Assert.Same(probe, Keyboard.FocusedElement);
            Assert.True(probe.IsKeyboardFocused);
            Assert.False(left.IsKeyboardFocusWithin);
            Assert.True(right.IsKeyboardFocusWithin);
            Assert.True(root.IsKeyboardFocusWithin);
            Assert.True(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void PageParkedInOffScreenHolder_LosesFocusLikeARemovedOne()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var page = new StackPanel();
            page.Children.Add(probe);
            var host = new FocusableDecorator { Child = page };
            var window = CreateWindow(host);

            Assert.True(probe.Focus());

            // Page cache pattern: the page is moved into a holder that is not under any window.
            var holder = new StackPanel();
            host.Child = new StackPanel();
            holder.Children.Add(page);
            PumpDispatcher();

            Assert.Same(host, Keyboard.FocusedElement);
            Assert.False(probe.IsKeyboardFocused);
            Assert.False(holder.IsKeyboardFocusWithin);
            Assert.True(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void CollapsedAncestor_MovesFocusOffHiddenElement()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var panel = new StackPanel();
            panel.Children.Add(probe);
            var outer = new FocusableDecorator { Child = panel };
            var window = CreateWindow(outer);

            Assert.True(probe.Focus());

            panel.Visibility = Visibility.Collapsed;
            Assert.False(probe.IsVisible);
            PumpDispatcher();

            Assert.Same(outer, Keyboard.FocusedElement);
            Assert.False(probe.IsKeyboardFocused);
            Assert.True(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void VisibilityFlipWithinOneTurn_KeepsFocus()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var panel = new StackPanel();
            panel.Children.Add(probe);
            CreateWindow(panel);

            Assert.True(probe.Focus());

            panel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
            PumpDispatcher();

            Assert.Same(probe, Keyboard.FocusedElement);
            Assert.True(probe.IsKeyboardFocused);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void DisabledAncestor_MovesFocusToNearestEnabledFocusableAncestor()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var panel = new StackPanel();
            panel.Children.Add(probe);
            var outer = new FocusableDecorator { Child = panel };
            CreateWindow(outer);

            Assert.True(probe.Focus());

            panel.IsEnabled = false;
            Assert.False(probe.IsEnabled);
            PumpDispatcher();

            Assert.Same(outer, Keyboard.FocusedElement);
            Assert.False(probe.IsKeyboardFocused);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void FocusableTurnedOff_ClearsFocusWhenNoFocusableAncestor()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var panel = new StackPanel();
            panel.Children.Add(probe);
            var window = CreateWindow(panel);

            Assert.True(probe.Focus());

            probe.Focusable = false;
            PumpDispatcher();

            Assert.Null(Keyboard.FocusedElement);
            Assert.False(probe.IsKeyboardFocused);
            Assert.False(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void StandaloneFocusedSubtreeAttachedLater_FlagsNewAncestors()
    {
        ResetInputState();
        try
        {
            var probe = new FocusProbe();
            var subtree = new StackPanel();
            subtree.Children.Add(probe);

            Assert.True(probe.Focus());
            Assert.True(subtree.IsKeyboardFocusWithin);

            var host = new Border();
            var window = CreateWindow(host);
            Assert.False(host.IsKeyboardFocusWithin);

            host.Child = subtree;
            PumpDispatcher();

            Assert.Same(probe, Keyboard.FocusedElement);
            Assert.True(host.IsKeyboardFocusWithin);
            Assert.True(window.IsKeyboardFocusWithin);
        }
        finally
        {
            ResetInputState();
        }
    }

    [Fact]
    public void TabAfterPageSwap_NavigatesTheNewPage_NotTheDetachedOne()
    {
        ResetInputState();
        SetShowFocusCues(false);
        try
        {
            var first = new FocusProbe { Width = 40, Height = 20, FocusVisualStyle = new Style(typeof(Control)) };
            var second = new FocusProbe { Width = 40, Height = 20, FocusVisualStyle = new Style(typeof(Control)) };
            var page1 = new StackPanel();
            page1.Children.Add(first);
            page1.Children.Add(second);
            var host = new Border { Child = page1 };
            var window = CreateWindow(host);
            ArrangeWindow(window);

            // Keyboard-driven: Tab lands on the first stop and switches focus cues on, so the
            // ring is drawn for the focused element.
            InvokeKeyDown(window, Key.Tab);
            Assert.Same(first, Keyboard.FocusedElement);
            InvokeKeyDown(window, Key.Tab);
            Assert.Same(second, Keyboard.FocusedElement);
            ArrangeWindow(window);
            Assert.True(FocusVisualManager.ShowFocusCues);
            Assert.Single(window.AdornerLayer!.GetAdorners(second)!);

            // Page swap while the ring is showing (the reported scenario).
            var third = new FocusProbe { Width = 40, Height = 20, FocusVisualStyle = new Style(typeof(Control)) };
            var page2 = new StackPanel();
            page2.Children.Add(third);
            host.Child = page2;
            ArrangeWindow(window);
            PumpDispatcher();

            Assert.Null(window.AdornerLayer!.GetAdorners(second));
            Assert.Null(Keyboard.FocusedElement);

            // Tab now walks the live page, not the detached one.
            InvokeKeyDown(window, Key.Tab);
            Assert.Same(third, Keyboard.FocusedElement);
            ArrangeWindow(window);
            Assert.Single(window.AdornerLayer!.GetAdorners(third)!);
            Assert.False(second.IsKeyboardFocused);
        }
        finally
        {
            SetShowFocusCues(false);
            ResetInputState();
        }
    }

    [Fact]
    public void AdornerLayer_DropsAdornersOfElementsThatLeftTheWindow()
    {
        var probe = new FocusProbe { Width = 40, Height = 20 };
        var stays = new FocusProbe { Width = 40, Height = 20 };
        var page = new StackPanel();
        page.Children.Add(probe);
        var host = new Border { Child = page };
        var root = new StackPanel();
        root.Children.Add(host);
        root.Children.Add(stays);
        var window = CreateWindow(root);

        var layer = AdornerLayer.GetAdornerLayer(probe);
        Assert.NotNull(layer);
        Assert.Same(window.AdornerLayer, layer);

        layer!.Add(new MarkerAdorner(probe));
        layer.Add(new MarkerAdorner(stays));
        ArrangeWindow(window);
        Assert.Single(layer.GetAdorners(probe)!);
        Assert.Single(layer.GetAdorners(stays)!);

        host.Child = new StackPanel();
        ArrangeWindow(window);

        Assert.Null(layer.GetAdorners(probe));
        Assert.Single(layer.GetAdorners(stays)!);
        Assert.Equal(1, layer.VisualChildrenCount);
    }

    [Fact]
    public void FocusVisual_DoesNotOutliveThePageItWasDrawnOn()
    {
        ResetInputState();
        SetShowFocusCues(false);
        try
        {
            var probe = new FocusProbe
            {
                Width = 40,
                Height = 20,
                FocusVisualStyle = new Style(typeof(Control)),
            };
            var page1 = new StackPanel();
            page1.Children.Add(probe);
            var host = new Border { Child = page1 };
            var window = CreateWindow(host);
            ArrangeWindow(window);

            SetShowFocusCues(true);
            Assert.True(probe.Focus());
            ArrangeWindow(window);

            var layer = window.AdornerLayer!;
            var ring = Assert.Single(layer.GetAdorners(probe)!);
            Assert.IsType<FocusVisualAdorner>(ring);

            // Page swap: the ring must be gone in the very frame the page is, before the
            // deferred focus pass has even run.
            host.Child = new StackPanel();
            ArrangeWindow(window);
            Assert.Null(layer.GetAdorners(probe));
            Assert.Null(ring.VisualParent);

            PumpDispatcher();
            Assert.Null(Keyboard.FocusedElement);
            Assert.Null(layer.GetAdorners(probe));
        }
        finally
        {
            SetShowFocusCues(false);
            ResetInputState();
        }
    }

    [Fact]
    public void FocusVisual_HidesWhileFocusedElementIsNotVisible_AndReturnsWhenShownAgain()
    {
        ResetInputState();
        SetShowFocusCues(false);
        try
        {
            var probe = new FocusProbe
            {
                Width = 40,
                Height = 20,
                FocusVisualStyle = new Style(typeof(Control)),
            };
            var panel = new StackPanel();
            panel.Children.Add(probe);
            var window = CreateWindow(panel);
            ArrangeWindow(window);

            SetShowFocusCues(true);
            Assert.True(probe.Focus());
            var layer = window.AdornerLayer!;
            Assert.Single(layer.GetAdorners(probe)!);

            panel.Visibility = Visibility.Collapsed;
            Assert.Null(layer.GetAdorners(probe));

            panel.Visibility = Visibility.Visible;
            Assert.Single(layer.GetAdorners(probe)!);
        }
        finally
        {
            SetShowFocusCues(false);
            ResetInputState();
        }
    }

    private static Window CreateWindow(UIElement content)
    {
        var window = new Window
        {
            TitleBarStyle = WindowTitleBarStyle.Native,
            Width = 320,
            Height = 240,
            Content = content,
        };
        return window;
    }

    private static void ArrangeWindow(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
    }

    private static void PumpDispatcher()
    {
        Jalium.UI.Threading.Dispatcher.CurrentDispatcher.ProcessQueue();
    }

    private static void InvokeKeyDown(Window window, Key key)
    {
        var method = typeof(Window).GetMethod("OnNativeKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(window, new object[] { (nint)KeyInterop.VirtualKeyFromKey(key), nint.Zero });
    }

    private static void SetShowFocusCues(bool value)
    {
        var method = typeof(FocusVisualManager).GetMethod(
            "SetShowFocusCues",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(null, new object[] { value });
    }

    private static void ResetInputState()
    {
        Keyboard.Initialize();
        Keyboard.ClearFocus();
        UIElement.ForceReleaseMouseCapture();
        PumpDispatcher();
    }

    private sealed class FocusProbe : FrameworkElement
    {
        public FocusProbe()
        {
            Focusable = true;
        }
    }

    private sealed class FocusableDecorator : Decorator
    {
        public FocusableDecorator()
        {
            Focusable = true;
        }
    }

    private sealed class MarkerAdorner : Adorner
    {
        public MarkerAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
        }
    }
}
