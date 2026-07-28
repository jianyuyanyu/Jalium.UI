using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;

namespace Jalium.UI.Tests;

public class TouchCaptureTests
{
    [Fact]
    public void CaptureTouch_RaisesGotTouchCapture_AndUpdatesCollections()
    {
        var element = new Border { Width = 100, Height = 100 };
        var device = Touch.RegisterTouchPoint(101, new Point(10, 10), element);
        int gotCount = 0;
        element.AddHandler(UIElement.GotTouchCaptureEvent, new RoutedEventHandler((_, _) => gotCount++));

        try
        {
            Assert.True(element.CaptureTouch(device));
            Assert.Equal(1, gotCount);
            Assert.True(element.AreAnyTouchesCaptured);
            Assert.Contains(device, element.TouchesCaptured);
            Assert.Same(element, UIElement.GetTouchCapture(device.Id));
            Assert.Same(element, device.Captured);
        }
        finally
        {
            element.ReleaseTouchCapture(device);
            Touch.UnregisterTouchPoint(101);
        }
    }

    [Fact]
    public void ReleaseTouchCapture_RaisesLostTouchCapture_AndClearsState()
    {
        var element = new Border { Width = 100, Height = 100 };
        var device = Touch.RegisterTouchPoint(102, new Point(10, 10), element);
        element.CaptureTouch(device);
        int lostCount = 0;
        element.AddHandler(UIElement.LostTouchCaptureEvent, new RoutedEventHandler((_, _) => lostCount++));

        try
        {
            Assert.True(element.ReleaseTouchCapture(device));
            Assert.Equal(1, lostCount);
            Assert.False(element.AreAnyTouchesCaptured);
            Assert.DoesNotContain(device, element.TouchesCaptured);
            Assert.Null(UIElement.GetTouchCapture(device.Id));
            Assert.Null(device.Captured);
        }
        finally
        {
            Touch.UnregisterTouchPoint(102);
        }
    }

    [Fact]
    public void CaptureTouch_StealingFromAnotherElement_RaisesLostThenGot()
    {
        var a = new Border();
        var b = new Border();
        var device = Touch.RegisterTouchPoint(103, new Point(0, 0), a);

        a.CaptureTouch(device);
        var events = new List<string>();
        a.AddHandler(UIElement.LostTouchCaptureEvent, new RoutedEventHandler((_, _) => events.Add("A-Lost")));
        b.AddHandler(UIElement.GotTouchCaptureEvent, new RoutedEventHandler((_, _) => events.Add("B-Got")));

        try
        {
            Assert.True(b.CaptureTouch(device));
            Assert.Equal(new[] { "A-Lost", "B-Got" }, events);
            Assert.False(a.AreAnyTouchesCaptured);
            Assert.True(b.AreAnyTouchesCaptured);
            Assert.Same(b, UIElement.GetTouchCapture(device.Id));
        }
        finally
        {
            b.ReleaseTouchCapture(device);
            Touch.UnregisterTouchPoint(103);
        }
    }

    [Fact]
    public void ReleaseAllTouchCaptures_ReleasesEveryCapturedDevice()
    {
        var element = new Border();
        var d1 = Touch.RegisterTouchPoint(201, new Point(0, 0), element);
        var d2 = Touch.RegisterTouchPoint(202, new Point(1, 1), element);
        element.CaptureTouch(d1);
        element.CaptureTouch(d2);

        try
        {
            Assert.Equal(2, element.TouchesCaptured.Count());
            element.ReleaseAllTouchCaptures();
            Assert.False(element.AreAnyTouchesCaptured);
            Assert.Empty(element.TouchesCaptured);
            Assert.Null(UIElement.GetTouchCapture(d1.Id));
            Assert.Null(UIElement.GetTouchCapture(d2.Id));
        }
        finally
        {
            Touch.UnregisterTouchPoint(201);
            Touch.UnregisterTouchPoint(202);
        }
    }

    [Fact]
    public void CaptureTouch_OnDisabledElement_ReturnsFalse()
    {
        var element = new Border { IsEnabled = false };
        var device = Touch.RegisterTouchPoint(301, new Point(0, 0), element);
        try
        {
            Assert.False(element.CaptureTouch(device));
            Assert.False(element.AreAnyTouchesCaptured);
        }
        finally
        {
            Touch.UnregisterTouchPoint(301);
        }
    }

    [Fact]
    public void UnregisterTouchPoint_ClearsUiElementCaptureState()
    {
        var element = new Border();
        var device = Touch.RegisterTouchPoint(302, new Point(0, 0), element);
        int lostCount = 0;
        element.AddHandler(
            UIElement.LostTouchCaptureEvent,
            new RoutedEventHandler((_, _) => lostCount++));
        Assert.True(element.CaptureTouch(device));

        Touch.UnregisterTouchPoint(device.Id);

        Assert.Equal(1, lostCount);
        Assert.Null(device.Captured);
        Assert.Null(UIElement.GetTouchCapture(device.Id));
        Assert.False(element.AreAnyTouchesCaptured);
        Assert.Empty(element.TouchesCaptured);
    }

    [Fact]
    public void CaptureTouch_StealingBetweenUiAndContentElementsKeepsOneOwner()
    {
        var visual = new Border();
        var content = new ContentElement();
        var device = Touch.RegisterTouchPoint(303, new Point(0, 0), visual);

        try
        {
            Assert.True(visual.CaptureTouch(device));
            Assert.True(content.CaptureTouch(device));

            Assert.False(visual.AreAnyTouchesCaptured);
            Assert.Empty(visual.TouchesCaptured);
            Assert.True(content.AreAnyTouchesCaptured);
            Assert.Contains(device, content.TouchesCaptured);
            Assert.Same(content, device.Captured);
            Assert.Null(UIElement.GetTouchCapture(device.Id));

            Assert.True(visual.CaptureTouch(device));

            Assert.False(content.AreAnyTouchesCaptured);
            Assert.Empty(content.TouchesCaptured);
            Assert.True(visual.AreAnyTouchesCaptured);
            Assert.Same(visual, device.Captured);
            Assert.Same(visual, UIElement.GetTouchCapture(device.Id));
        }
        finally
        {
            Touch.UnregisterTouchPoint(device.Id);
        }
    }

    [Fact]
    public void UnregisterTouchPoint_ClearsContentElementCaptureState()
    {
        var content = new ContentElement();
        var device = Touch.RegisterTouchPoint(304, new Point(0, 0), null);
        Assert.True(content.CaptureTouch(device));

        Touch.UnregisterTouchPoint(device.Id);

        Assert.Null(device.Captured);
        Assert.False(content.AreAnyTouchesCaptured);
        Assert.Empty(content.TouchesCaptured);
    }
}
