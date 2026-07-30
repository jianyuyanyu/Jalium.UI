using System.Reflection;

namespace Jalium.UI.Tests;

public sealed class SizeChangedEventArgsParityTests
{
    private sealed class ClassHandlerElement : FrameworkElement
    {
        internal static int InvocationCount;

        static ClassHandlerElement()
        {
            EventManager.RegisterClassHandler(
                typeof(ClassHandlerElement),
                FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler((_, _) => Interlocked.Increment(ref InvocationCount)));
        }
    }

    [Fact]
    public void InvokeEventHandlerIsProtectedOverrideAndInvokesTypedDelegate()
    {
        var method = typeof(SizeChangedEventArgs).GetMethod(
            "InvokeEventHandler",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.NotNull(method);
        Assert.True(method!.IsFamily);
        Assert.True(method.IsVirtual);

        var element = new FrameworkElement();
        SizeChangedEventArgs? received = null;
        element.AddHandler(
            FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((_, args) => received = args));
        var info = new SizeChangedInfo(element, new Size(10, 20), true, true);
        var eventArgs = new SizeChangedEventArgs(info)
        {
            RoutedEvent = FrameworkElement.SizeChangedEvent,
            Source = element,
        };

        element.RaiseEvent(eventArgs);

        Assert.Same(eventArgs, received);
    }

    [Fact]
    public void RenderSizeChangeStillRaisesClrSizeChangedEvent()
    {
        var element = new FrameworkElement();
        SizeChangedEventArgs? received = null;
        element.SizeChanged += (_, args) => received = args;

        element.RenderSize = new Size(30, 40);

        Assert.NotNull(received);
        Assert.Equal(default, received!.PreviousSize);
        Assert.Equal(new Size(30, 40), received.NewSize);
        Assert.True(received.WidthChanged);
        Assert.True(received.HeightChanged);
    }

    [Fact]
    public void RenderSizeChangeStillRaisesRegisteredClassHandler()
    {
        var element = new ClassHandlerElement();
        Volatile.Write(ref ClassHandlerElement.InvocationCount, 0);

        element.RenderSize = new Size(50, 60);

        Assert.Equal(1, Volatile.Read(ref ClassHandlerElement.InvocationCount));
    }
}
