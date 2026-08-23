using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Input;

namespace Jalium.UI.Tests;

/// <summary>
/// 级联弹窗（外飞菜单里再弹子菜单/下拉）的宿主选择与 light dismiss 链感知。
/// 背景：MenuFlyout 总是外飞成独立 PopupWindow，而它里面的子菜单以前「装得下就落回主窗口覆盖层」——
/// 点子菜单项时主窗口分发器把这次按下当成「外飞弹窗开着时点到了外面」：父菜单被关、点击被吞、
/// 子菜单却留在原地还能继续点。
/// </summary>
[Collection("Application")]
public sealed class CascadedPopupChainTests
{
    // ─────────────────────────── 宿主选择 ───────────────────────────

    [Fact]
    public void Popup_AnchoredInsideExternalPopupWindow_IsDetected()
    {
        var ownerWindow = new Window();
        var target = new MenuFlyoutSubItem();
        var panel = new StackPanel();
        panel.Children.Add(target);
        var hostPopup = new Popup();
        var popupRoot = new PopupRoot(hostPopup, new Border { Child = panel }, isLightDismiss: true);
        _ = new PopupWindow(ownerWindow, popupRoot);   // 让 popupRoot.VisualParent 指向 PopupWindow

        var nested = new Popup { PlacementTarget = target };
        Assert.True(nested.IsAnchoredInsideExternalPopupWindow());

        // 对照：锚点直接在窗口树里
        var window = new Window();
        var plainTarget = new Border();
        window.Content = plainTarget;
        var plain = new Popup { PlacementTarget = plainTarget };
        Assert.False(plain.IsAnchoredInsideExternalPopupWindow());

        // 对照：锚点在主窗口覆盖层的弹窗里（不是外飞）
        var overlayOwner = new Window();
        var overlayTarget = new Border();
        var overlayRoot = new PopupRoot(new Popup(), overlayTarget, isLightDismiss: true);
        overlayOwner.OverlayLayer.AddPopupRoot(overlayRoot);
        try
        {
            var fromOverlay = new Popup { PlacementTarget = overlayTarget };
            Assert.False(fromOverlay.IsAnchoredInsideExternalPopupWindow());
        }
        finally
        {
            overlayOwner.OverlayLayer.RemovePopupRoot(overlayRoot);
            overlayRoot.Detach();
        }
    }

    [Fact]
    public void Popup_AnchoredInsideExternalPopupWindow_AlwaysResolvesExternalHostOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ownerWindow = new Window();
        var target = new MenuFlyoutSubItem();
        var panel = new StackPanel();
        panel.Children.Add(target);
        var popupRoot = new PopupRoot(new Popup(), new Border { Child = panel }, isLightDismiss: true);
        _ = new PopupWindow(ownerWindow, popupRoot);

        var nested = new Popup { PlacementTarget = target, ShouldConstrainToRootBounds = false };

        // 明明装得下（窗口 1000×800、弹窗落在 (10,10) 100×50），也必须外飞。
        Assert.True(InvokeResolveHostPlacement(nested, new Point(10, 10), new Size(100, 50), new Size(1000, 800)));

        // 对照：同样装得下、锚点不在外飞弹窗里 → 留在覆盖层。
        var window = new Window();
        var plainTarget = new Border();
        window.Content = plainTarget;
        var plain = new Popup { PlacementTarget = plainTarget, ShouldConstrainToRootBounds = false };
        Assert.False(InvokeResolveHostPlacement(plain, new Point(10, 10), new Size(100, 50), new Size(1000, 800)));
    }

    // ─────────────────────────── 弹窗链判定 ───────────────────────────

    [Fact]
    public void Popup_IsAncestorOfPopupRoot_FollowsPlacementTargetsAcrossPopupLevels()
    {
        // parent ─(root1 里的 anchor1)─▶ child ─(root2 里的 anchor2)─▶ grandchild ─▶ root3
        var anchor1 = new Border();
        var parent = new Popup();
        var root1 = new PopupRoot(parent, new Border { Child = anchor1 }, isLightDismiss: true);

        var anchor2 = new Border();
        var child = new Popup { PlacementTarget = anchor1 };
        var root2 = new PopupRoot(child, new Border { Child = anchor2 }, isLightDismiss: true);

        var grandchild = new Popup { PlacementTarget = anchor2 };
        var root3 = new PopupRoot(grandchild, new Border(), isLightDismiss: true);

        var unrelated = new Popup();
        var unrelatedRoot = new PopupRoot(unrelated, new Border(), isLightDismiss: true);

        Assert.True(parent.IsAncestorOfPopupRoot(root2));
        Assert.True(parent.IsAncestorOfPopupRoot(root3));
        Assert.True(child.IsAncestorOfPopupRoot(root3));

        Assert.False(child.IsAncestorOfPopupRoot(root1));       // 反向不成立
        Assert.False(grandchild.IsAncestorOfPopupRoot(root2));
        Assert.False(parent.IsAncestorOfPopupRoot(unrelatedRoot));
        Assert.False(unrelated.IsAncestorOfPopupRoot(root2));
        _ = root1; _ = unrelatedRoot;
    }

    [Fact]
    public void Popup_IsAncestorOfPopupRoot_UsesPopupOwnVisualChainWhenNoPlacementTarget()
    {
        // 模板里声明的 Popup（如 ComboBox 的下拉）没有 PlacementTarget，但它自己挂在父弹窗内容树里。
        var parent = new Popup();
        var declaredPopup = new Popup();
        var content = new StackPanel();
        content.Children.Add(declaredPopup);
        var root1 = new PopupRoot(parent, content, isLightDismiss: true);
        var root2 = new PopupRoot(declaredPopup, new Border(), isLightDismiss: true);

        Assert.True(parent.IsAncestorOfPopupRoot(root2));
        Assert.False(declaredPopup.IsAncestorOfPopupRoot(root1));
    }

    [Fact]
    public void OverlayLayer_FindPopupRootAt_ReturnsTopmostRootContainingPoint()
    {
        var overlay = new OverlayLayer();
        var lower = new PopupRoot(new Popup(), new Border(), isLightDismiss: true) { Width = 100, Height = 100 };
        var upper = new PopupRoot(new Popup(), new Border(), isLightDismiss: false) { Width = 100, Height = 100 };
        Canvas.SetLeft(lower, 0); Canvas.SetTop(lower, 0);
        Canvas.SetLeft(upper, 50); Canvas.SetTop(upper, 50);
        overlay.AddPopupRoot(lower);
        overlay.AddPopupRoot(upper);
        try
        {
            Assert.Same(lower, overlay.FindPopupRootAt(new Point(10, 10)));
            Assert.Same(upper, overlay.FindPopupRootAt(new Point(75, 75)));   // 重叠处取后加入（上面）的
            Assert.Same(upper, overlay.FindPopupRootAt(new Point(140, 140)));
            Assert.Null(overlay.FindPopupRootAt(new Point(300, 300)));
        }
        finally
        {
            overlay.RemovePopupRoot(lower);
            overlay.RemovePopupRoot(upper);
            lower.Detach();
            upper.Detach();
        }
    }

    // ─────────────────────────── 主窗口分发器 ───────────────────────────

    [Fact]
    public void ClickInsideOverlayChildOfExternalPopup_KeepsParentOpenAndDeliversClick()
    {
        UIElement.ForceReleaseMouseCapture();
        using var host = new CountingInputHost();
        host.HitTarget = new Border();
        var dispatcher = new WindowInputDispatcher(host);

        // 外飞父弹窗：根里有一个锚点元素
        var anchor = new Border();
        var parent = new Popup { IsOpen = true, StaysOpen = false };
        var parentRoot = new PopupRoot(parent, new Border { Child = anchor }, isLightDismiss: true);
        host.ExternalPopups.Add(parent);

        // 覆盖层子弹窗：锚在父弹窗内容里，落在主窗口 (300,300) 100×100
        var child = new Popup { IsOpen = true, StaysOpen = false, PlacementTarget = anchor };
        var childRoot = new PopupRoot(child, new Border(), isLightDismiss: true) { Width = 100, Height = 100 };
        Canvas.SetLeft(childRoot, 300); Canvas.SetTop(childRoot, 300);
        host.OverlayLayer.AddPopupRoot(childRoot);

        // 与点击无关的另一个外飞弹窗
        var unrelated = new Popup { IsOpen = true, StaysOpen = false };
        host.ExternalPopups.Add(unrelated);

        var pressed = MouseButtonStates.AllReleased with { Left = MouseButtonState.Pressed };
        try
        {
            dispatcher.HandleMouseDown(MouseButton.Left, new Point(320, 320), pressed, ModifierKeys.None, clickCount: 1, timestamp: 1);

            Assert.True(parent.IsOpen);                 // 祖先不关
            Assert.True(child.IsOpen);
            Assert.False(unrelated.IsOpen);             // 无关的外飞弹窗照常 light dismiss
            Assert.Null(dispatcher.SuppressMouseUpButton);   // 点击没有被吞
            Assert.Equal(1, host.HitTestCount);         // 照常走到命中测试

            // 点到所有弹窗之外：两种宿主形态一起关，按下被消费
            dispatcher.HandleMouseDown(MouseButton.Left, new Point(10, 10), pressed, ModifierKeys.None, clickCount: 1, timestamp: 2);
            Assert.False(parent.IsOpen);
            Assert.False(child.IsOpen);
            Assert.Equal(MouseButton.Left, dispatcher.SuppressMouseUpButton);
            Assert.Equal(1, host.HitTestCount);
        }
        finally
        {
            UIElement.ForceReleaseMouseCapture();
            host.ExternalPopups.Clear();
            host.OverlayLayer.RemovePopupRoot(childRoot);
            childRoot.Detach();
            parentRoot.Detach();
        }
    }

    [Fact]
    public void HoverInsideOverlayChildOfExternalPopup_IsNotBlocked()
    {
        UIElement.ForceReleaseMouseCapture();
        using var host = new CountingInputHost();
        host.HitTarget = new Border();
        var dispatcher = new WindowInputDispatcher(host);

        var anchor = new Border();
        var parent = new Popup { IsOpen = true, StaysOpen = false };
        var parentRoot = new PopupRoot(parent, new Border { Child = anchor }, isLightDismiss: true);
        host.ExternalPopups.Add(parent);

        var child = new Popup { IsOpen = true, StaysOpen = false, PlacementTarget = anchor };
        var childRoot = new PopupRoot(child, new Border(), isLightDismiss: true) { Width = 100, Height = 100 };
        Canvas.SetLeft(childRoot, 300); Canvas.SetTop(childRoot, 300);
        host.OverlayLayer.AddPopupRoot(childRoot);

        try
        {
            // 弹窗之外：外飞弹窗开着，窗口内容不悬停（不命中测试）
            dispatcher.HandleMouseMove(new Point(10, 10), MouseButtonStates.AllReleased, ModifierKeys.None, timestamp: 1);
            Assert.Equal(0, host.HitTestCount);

            // 覆盖层子弹窗之内：照常悬停
            dispatcher.HandleMouseMove(new Point(320, 320), MouseButtonStates.AllReleased, ModifierKeys.None, timestamp: 2);
            Assert.Equal(1, host.HitTestCount);
        }
        finally
        {
            UIElement.ForceReleaseMouseCapture();
            host.ExternalPopups.Clear();
            host.OverlayLayer.RemovePopupRoot(childRoot);
            childRoot.Detach();
            parentRoot.Detach();
        }
    }

    // ─────────────────────────── MenuFlyoutSubItem ───────────────────────────

    [Fact]
    public void ClosingParentMenu_ClosesOpenSubMenu()
    {
        ResetApplicationState();
        ResetInputState();
        _ = new Application();
        MenuBarItem? editItem = null;
        MenuFlyoutSubItem? refactorSubItem = null;

        try
        {
            var leafCommand = new MenuFlyoutItem { Text = "Rename" };
            refactorSubItem = new MenuFlyoutSubItem { Text = "Refactor" };
            refactorSubItem.Items.Add(leafCommand);

            editItem = new MenuBarItem { Title = "Edit" };
            editItem.Items.Add(refactorSubItem);

            var menuBar = new MenuBar();
            menuBar.Items.Add(editItem);

            var window = new Window
            {
                TitleBarStyle = WindowTitleBarStyle.Native,
                Width = 320,
                Height = 120,
                Content = menuBar
            };
            ArrangeWindow(window);

            editItem.OpenMenuAndFocusFirstItem();
            refactorSubItem.ShowSubMenu();
            Dispatcher.GetForCurrentThread().ProcessQueue();

            var subPopup = GetPrivateField<Popup>(typeof(MenuFlyoutSubItem), refactorSubItem, "_subPopup");
            Assert.True(editItem.IsMenuOpen);
            Assert.True(subPopup.IsOpen);
            Assert.True(refactorSubItem.IsSubMenuOpen);

            // 父菜单被关（light dismiss / Hide / 窗口失活……任何路径）→ 子菜单必须跟着关，不能留下孤儿窗口
            editItem.CloseMenu();
            Dispatcher.GetForCurrentThread().ProcessQueue();

            Assert.False(editItem.IsMenuOpen);
            Assert.False(subPopup.IsOpen);
            Assert.False(refactorSubItem.IsSubMenuOpen);
        }
        finally
        {
            refactorSubItem?.HideSubMenu();
            editItem?.CloseMenu();
            ResetInputState();
            ResetApplicationState();
        }
    }

    [Fact]
    public void SubItem_StaysHighlightedWhileSubMenuOpen()
    {
        // 指针移进子菜单（外飞时是另一个窗口）后本项的 IsMouseOver 会掉，但打开子菜单的那一项必须一直亮着。
        var subItem = new MenuFlyoutSubItem { Text = "Refactor" };
        subItem.Items.Add(new MenuFlyoutItem { Text = "Rename" });

        Assert.False(subItem.IsMouseOver);
        Assert.False(subItem.IsKeyboardFocused);
        Assert.False(GetIsHighlighted(subItem));

        subItem.ShowSubMenu();
        Assert.True(subItem.IsSubMenuOpen);
        Assert.True(GetIsHighlighted(subItem));

        subItem.HideSubMenu();
        Assert.False(subItem.IsSubMenuOpen);
        Assert.False(GetIsHighlighted(subItem));
    }

    [Fact]
    public void ReopeningParentMenu_ResubscribesSubMenuToNewHostPopupLifetime()
    {
        ResetApplicationState();
        ResetInputState();
        _ = new Application();
        MenuBarItem? editItem = null;
        MenuFlyoutSubItem? refactorSubItem = null;

        try
        {
            refactorSubItem = new MenuFlyoutSubItem { Text = "Refactor" };
            refactorSubItem.Items.Add(new MenuFlyoutItem { Text = "Rename" });
            editItem = new MenuBarItem { Title = "Edit" };
            editItem.Items.Add(refactorSubItem);
            var menuBar = new MenuBar();
            menuBar.Items.Add(editItem);
            var window = new Window
            {
                TitleBarStyle = WindowTitleBarStyle.Native,
                Width = 320,
                Height = 120,
                Content = menuBar
            };
            ArrangeWindow(window);

            var subPopup = default(Popup);
            for (int round = 0; round < 2; round++)
            {
                editItem.OpenMenuAndFocusFirstItem();
                refactorSubItem.ShowSubMenu();
                Dispatcher.GetForCurrentThread().ProcessQueue();
                subPopup = GetPrivateField<Popup>(typeof(MenuFlyoutSubItem), refactorSubItem, "_subPopup");
                Assert.True(subPopup.IsOpen);

                editItem.CloseMenu();
                Dispatcher.GetForCurrentThread().ProcessQueue();
                Assert.False(subPopup.IsOpen);
            }
        }
        finally
        {
            refactorSubItem?.HideSubMenu();
            editItem?.CloseMenu();
            ResetInputState();
            ResetApplicationState();
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static bool InvokeResolveHostPlacement(Popup popup, Point requested, Size popupSize, Size windowSize)
    {
        var method = typeof(Popup).GetMethod("ResolveHostPlacement", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var args = new object?[] { requested, popupSize, windowSize, true, null };
        return (bool)method!.Invoke(popup, args)!;
    }

    private static bool GetIsHighlighted(MenuFlyoutItem item)
    {
        var property = typeof(MenuFlyoutItem).GetProperty("IsHighlighted", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return (bool)property!.GetValue(item)!;
    }

    private static void ArrangeWindow(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
    }

    private static T GetPrivateField<T>(Type ownerType, object owner, string fieldName)
    {
        var field = ownerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(owner);
        Assert.NotNull(value);
        return (T)value!;
    }

    private static void ResetApplicationState()
    {
        var currentField = typeof(Application).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static);
        currentField?.SetValue(null, null);

        var resetMethod = typeof(ThemeManager).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static);
        resetMethod?.Invoke(null, null);
    }

    private static void ResetInputState()
    {
        Keyboard.Initialize();
        Keyboard.ClearFocus();
        UIElement.ForceReleaseMouseCapture();
    }
}
