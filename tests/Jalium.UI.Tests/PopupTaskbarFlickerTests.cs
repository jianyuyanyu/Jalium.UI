using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;

namespace Jalium.UI.Tests;

/// <summary>
/// 覆盖"弹窗贴任务栏 / 屏幕边缘时反复开合闪烁"的三条不变式：
/// <list type="number">
/// <item>屏幕解算后仍落在 owner 客户区内的弹窗不得升级成独立窗口；</item>
/// <item>光标锚定（Mouse / MousePoint）的弹窗放不下时向光标上方翻转，而不是被钳到光标底下；</item>
/// <item>声明为不可命中的弹窗（ToolTip）其宿主表面同样不可命中，不能抢走 owner 的 hover。</item>
/// </list>
/// 任何一条被破坏，宿主都会在 owner 收到 WM_MOUSELEAVE 后关闭 ToolTip 并立刻重开。
/// </summary>
public sealed class PopupTaskbarFlickerTests
{
    private static readonly Size PopupSize = new(200, 32);

    [Fact]
    public void ScreenResolvedPlacementInsideOwner_KeepsPopupInOverlay()
    {
        // 最大化窗口：客户区 ≈ 工作区。请求位置越过窗口底部，但工作区钳制会把它推回窗口内，
        // 此时独立 HWND 毫无收益，只会盖住光标制造闪烁。
        var windowSize = new Size(1416, 900);
        var resolvedPosition = new Point(1180, 866);

        Assert.False(Popup.ShouldPromoteToExternalWindow(
            supportsExternalPopup: true,
            constrainToRootBounds: false,
            preferExternalWindow: false,
            resolvedPosition,
            PopupSize,
            windowSize));
    }

    [Fact]
    public void ScreenResolvedPlacementOutsideOwner_StillPromotes()
    {
        // 小窗口位于屏幕中部：解算后弹窗仍要画到 owner 之外，外飞窗口依然是唯一选择。
        var windowSize = new Size(400, 300);
        var resolvedPosition = new Point(260, 290);

        Assert.True(Popup.ShouldPromoteToExternalWindow(
            supportsExternalPopup: true,
            constrainToRootBounds: false,
            preferExternalWindow: false,
            resolvedPosition,
            PopupSize,
            windowSize));
    }

    [Fact]
    public void PreferExternalWindow_PromotesEvenWhenPlacementFits()
    {
        // ContextMenu / MenuFlyout 按平台惯例总是独立顶层窗口，不受"能放进 owner"影响。
        Assert.True(Popup.ShouldPromoteToExternalWindow(
            supportsExternalPopup: true,
            constrainToRootBounds: false,
            preferExternalWindow: true,
            new Point(10, 10),
            PopupSize,
            new Size(1416, 900)));
    }

    [Fact]
    public void ConstrainedPopupOrUnsupportedHost_NeverPromotes()
    {
        var resolvedPosition = new Point(260, 290);
        var windowSize = new Size(400, 300);

        Assert.False(Popup.ShouldPromoteToExternalWindow(
            supportsExternalPopup: true,
            constrainToRootBounds: true,
            preferExternalWindow: true,
            resolvedPosition,
            PopupSize,
            windowSize));

        Assert.False(Popup.ShouldPromoteToExternalWindow(
            supportsExternalPopup: false,
            constrainToRootBounds: false,
            preferExternalWindow: true,
            resolvedPosition,
            PopupSize,
            windowSize));
    }

    [Fact]
    public void CursorAnchoredPlacement_FlipsAboveCursorInsteadOfCoveringIt()
    {
        var popup = new Popup
        {
            Placement = PlacementMode.Mouse,
            HorizontalOffset = 12,
            VerticalOffset = 20,
        };

        // 光标 (300, 690)，窗口 800×700：请求位置 (312, 710) 的底边越出窗口。
        // 钳回窗口内会让弹窗压住光标热点 → 必须翻到光标上方。
        var flipped = InvokeAutoFlip(popup, new Point(312, 710), new Size(180, 30), new Size(800, 700));

        Assert.Equal(312, flipped.X, 3);
        Assert.Equal(658, flipped.Y, 3);
        Assert.True(flipped.Y + 30 < 690, "弹窗底边必须停在光标热点上方。");
    }

    [Fact]
    public void CursorAnchoredPlacement_KeepsPositionWhenItFits()
    {
        var popup = new Popup
        {
            Placement = PlacementMode.Mouse,
            HorizontalOffset = 12,
            VerticalOffset = 20,
        };

        var position = new Point(312, 400);
        var result = InvokeAutoFlip(popup, position, new Size(180, 30), new Size(800, 700));

        Assert.Equal(position.X, result.X, 3);
        Assert.Equal(position.Y, result.Y, 3);
    }

    [Fact]
    public void TargetAnchoredPlacement_IsUnaffectedByCursorFlip()
    {
        // Bottom 放置的翻转基准仍是 PlacementTarget，不能被光标翻转规则改写。
        var target = new Border { Width = 120, Height = 24 };
        target.Arrange(new Rect(40, 600, 120, 24));

        var popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = target,
        };

        var result = InvokeAutoFlip(popup, new Point(40, 624), new Size(120, 200), new Size(800, 700));

        Assert.Equal(400, result.Y, 3);
    }

    [Theory]
    [InlineData(273.7, 1.0)]
    [InlineData(273.7, 1.5)]
    [InlineData(28.7, 1.25)]
    [InlineData(120.0, 2.0)]
    [InlineData(33.333, 1.75)]
    public void NativeHostSize_NeverGivesTheHostLessRoomThanTheContent(double dip, double dpiScale)
    {
        var physical = Popup.ToNativeHostSize(dip, dpiScale);

        // 宿主把布局槽反算成 physical / dpi 再 Arrange；一旦它小于内容，
        // layout clip 就吃掉最后一列和一行 —— 也就是右边框和下边框。
        Assert.True(
            physical / dpiScale >= dip,
            $"host slot {physical / dpiScale} DIP must not be narrower than content {dip} DIP");
        Assert.True(physical - (dip * dpiScale) < 1.0, "rounding must not waste a whole pixel");
    }

    [Fact]
    public void NativeHostSize_StaysPositiveForDegenerateInput()
    {
        Assert.Equal(1, Popup.ToNativeHostSize(0, 1.0));
        Assert.Equal(1, Popup.ToNativeHostSize(-5, 1.0));
        Assert.Equal(1, Popup.ToNativeHostSize(double.NaN, 1.0));
        Assert.Equal(274, Popup.ToNativeHostSize(273.7, double.NaN));
    }

    [Fact]
    public void NativeHostOffset_RoundsDownOnBothSigns()
    {
        Assert.Equal(1301, Popup.ToNativeHostOffset(1301.7));
        Assert.Equal(-1302, Popup.ToNativeHostOffset(-1301.7));
        Assert.Equal(0, Popup.ToNativeHostOffset(double.NaN));
    }

    [Fact]
    public void PopupRoot_InheritsOwnerHitTestPolicy()
    {
        var transparentPopup = new Popup { IsHitTestVisible = false };
        var transparentRoot = new PopupRoot(transparentPopup, new Border(), isLightDismiss: false);
        Assert.False(transparentRoot.IsHitTestVisible);

        var interactivePopup = new Popup();
        var interactiveRoot = new PopupRoot(interactivePopup, new Border(), isLightDismiss: false);
        Assert.True(interactiveRoot.IsHitTestVisible);
    }

    [Fact]
    public void ToolTipPopup_IsDeclaredNonHitTestable()
    {
        var toolTip = new ToolTip { PlacementTarget = new Border() };

        try
        {
            toolTip.IsOpen = true;

            var field = typeof(ToolTip).GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic);
            var popup = Assert.IsType<Popup>(field?.GetValue(toolTip));

            Assert.False(popup.IsHitTestVisible);
        }
        finally
        {
            toolTip.IsOpen = false;
        }
    }

    private static Point InvokeAutoFlip(Popup popup, Point position, Size popupSize, Size windowSize)
    {
        var method = typeof(Popup).GetMethod(
            "ApplyAutoFlip",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Point)method.Invoke(popup, [position, popupSize, windowSize])!;
    }
}
