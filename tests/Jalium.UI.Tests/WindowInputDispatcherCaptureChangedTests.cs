using Jalium.UI.Controls;
using Jalium.UI.Input;

namespace Jalium.UI.Tests;

/// <summary>
/// WM_CAPTURECHANGED 的语义：只有捕获**真的**离开本窗口才算丢。
///
/// <para>
/// 真实场景是「按钮按下先 CaptureMouse，祖先随后接管拖拽再 CaptureMouse」：第二次捕获会再调一次
/// <c>SetCapture(自己)</c>，Win32 同步回一条 WM_CAPTURECHANGED。曾经这条消息被无条件当成丢捕获，
/// 把接管者刚拿到的捕获清成 null（<see cref="UIElement.CaptureMouse"/> 还没返回），后续鼠标事件
/// 不再路由给它 —— 表现是「拖拽刚起步就断、元素完全不跟手」。
/// </para>
/// </summary>
public sealed class WindowInputDispatcherCaptureChangedTests
{
    [Fact]
    public void CaptureChangedToSelf_KeepsManagedCapture()
    {
        UIElement.ForceReleaseMouseCapture();
        using var host = new CountingInputHost();
        var dispatcher = new WindowInputDispatcher(host);
        var element = new Border();
        Assert.True(element.CaptureMouse());

        dispatcher.HandleNativeCaptureChanged(newCaptureWindow: 0x1234, selfWindow: 0x1234);

        Assert.True(element.IsMouseCaptured);
        Assert.Same(element, Mouse.Captured);
    }

    [Fact]
    public void CaptureChangedToAnotherWindow_DropsManagedCapture()
    {
        UIElement.ForceReleaseMouseCapture();
        using var host = new CountingInputHost();
        var dispatcher = new WindowInputDispatcher(host);
        var element = new Border();
        Assert.True(element.CaptureMouse());

        dispatcher.HandleNativeCaptureChanged(newCaptureWindow: 0x9999, selfWindow: 0x1234);

        Assert.False(element.IsMouseCaptured);
        Assert.Null(Mouse.Captured);
    }

    [Fact]
    public void CaptureChangedToNoWindow_DropsManagedCapture()
    {
        // lParam 为 0 = 谁都没接手（例如 ReleaseCapture）。自窗口句柄也可能是 0（无句柄的测试窗口），
        // 两个 0 不能被当成「自己给自己」而放过。
        UIElement.ForceReleaseMouseCapture();
        using var host = new CountingInputHost();
        var dispatcher = new WindowInputDispatcher(host);
        var element = new Border();
        Assert.True(element.CaptureMouse());

        dispatcher.HandleNativeCaptureChanged(newCaptureWindow: 0, selfWindow: 0);

        Assert.False(element.IsMouseCaptured);
        Assert.Null(Mouse.Captured);
    }

    [Fact]
    public void CaptureMouse_SurvivesReentrantCaptureChangedFromSetCapture()
    {
        // 直接复刻同步回声：SetCapture 期间收到「捕获给自己」的 WM_CAPTURECHANGED，
        // CaptureMouse 返回后捕获必须还在接管者手里。
        UIElement.ForceReleaseMouseCapture();
        using var host = new CountingInputHost();
        var dispatcher = new WindowInputDispatcher(host);
        var button = new Border();
        var dragHandle = new Border();

        Assert.True(button.CaptureMouse());
        dispatcher.HandleNativeCaptureChanged(newCaptureWindow: 0x1234, selfWindow: 0x1234);
        Assert.True(dragHandle.CaptureMouse());
        dispatcher.HandleNativeCaptureChanged(newCaptureWindow: 0x1234, selfWindow: 0x1234);

        Assert.True(dragHandle.IsMouseCaptured);
        Assert.False(button.IsMouseCaptured);
    }
}
