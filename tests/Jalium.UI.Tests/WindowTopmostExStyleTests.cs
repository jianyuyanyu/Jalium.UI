using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Regression cover for the Win32 creation ex-style matrix (<see cref="Window.ComputeWin32ExStyle"/>).
///
/// <para>The bug this locks down: <c>Topmost</c> was honoured only by
/// <c>OnTopmostChanged</c>, which returns early while <c>Handle</c> is still zero.
/// Setting <c>Topmost = true</c> in a constructor — the normal way to declare an
/// always-on-top overlay — therefore never reached the HWND, and the window silently
/// stayed non-topmost forever. Dock guide overlays were created, sized and positioned
/// correctly but rendered under the window being dragged, so a floating tool window
/// could never be docked back.</para>
/// </summary>
public class WindowTopmostExStyleTests
{
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    [Fact]
    public void Topmost_IsBakedIntoTheCreationExStyle()
    {
        var exStyle = Window.ComputeWin32ExStyle(
            WindowTitleBarStyle.Native, showInTaskbar: true, allowsTransparency: false, topmost: true);

        Assert.Equal(WS_EX_TOPMOST, exStyle & WS_EX_TOPMOST);
    }

    [Fact]
    public void NotTopmost_LeavesTheTopmostBitClear()
    {
        var exStyle = Window.ComputeWin32ExStyle(
            WindowTitleBarStyle.Native, showInTaskbar: true, allowsTransparency: false, topmost: false);

        Assert.Equal(0u, exStyle & WS_EX_TOPMOST);
    }

    /// <summary>
    /// The exact shape a dock guide / drag overlay asks for: transparent, off-taskbar,
    /// always on top. All three bits have to survive together — losing topmost here is
    /// what made the overlay invisible under the dragged window.
    /// </summary>
    [Fact]
    public void TransparentOffTaskbarTopmostOverlay_GetsAllThreeBits()
    {
        var exStyle = Window.ComputeWin32ExStyle(
            WindowTitleBarStyle.Native, showInTaskbar: false, allowsTransparency: true, topmost: true);

        Assert.Equal(WS_EX_TOPMOST, exStyle & WS_EX_TOPMOST);
        Assert.Equal(WS_EX_TOOLWINDOW, exStyle & WS_EX_TOOLWINDOW);
        Assert.Equal(WS_EX_NOREDIRECTIONBITMAP, exStyle & WS_EX_NOREDIRECTIONBITMAP);
        // Off-taskbar must clear APPWINDOW even though a custom title bar would set it.
        Assert.Equal(0u, exStyle & WS_EX_APPWINDOW);
    }

    [Fact]
    public void CustomTitleBarInTaskbar_KeepsAppWindow()
    {
        var exStyle = Window.ComputeWin32ExStyle(
            WindowTitleBarStyle.Custom, showInTaskbar: true, allowsTransparency: false, topmost: false);

        Assert.Equal(WS_EX_APPWINDOW, exStyle & WS_EX_APPWINDOW);
        Assert.Equal(0u, exStyle & WS_EX_TOOLWINDOW);
    }
}
