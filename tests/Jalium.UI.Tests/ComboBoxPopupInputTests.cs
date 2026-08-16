using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Data;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class ComboBoxPopupInputTests
{
    [Fact]
    public void DataContextArrivingAfterBindings_PreservesInitialSelectedItem()
    {
        var comboBox = new ComboBox();
        comboBox.SetBinding(
            ItemsControl.ItemsSourceProperty,
            new Binding(nameof(InitialSelectionSource.Items)));
        comboBox.SetBinding(
            Selector.SelectedItemProperty,
            new Binding(nameof(InitialSelectionSource.SelectedItem)) { Mode = BindingMode.TwoWay });

        var source = new InitialSelectionSource();
        comboBox.DataContext = source;

        Assert.Same(source.Items, comboBox.ItemsSource);
        Assert.Equal("xUnit v3", comboBox.SelectedItem);
        Assert.Equal(0, comboBox.SelectedIndex);
        Assert.Equal("xUnit v3", comboBox.SelectionBoxItem);
    }

    [Fact]
    public void PopupWindow_MouseHoverAndClick_SelectsComboBoxItem()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            var comboBox = new ComboBox
            {
                Width = 220,
                ItemsSource = new[] { "xUnit v3", "xUnit v2", "NUnit", "MSTest", "TUnit", "Fixie" },
                SelectedItem = "xUnit v3",
            };

            var host = new StackPanel { Width = 400, Height = 300 };
            host.Children.Add(comboBox);
            host.Measure(new Size(400, 300));
            host.Arrange(new Rect(0, 0, 400, 300));
            comboBox.IsDropDownOpen = true;

            var popup = Assert.IsType<Popup>(FindDescendant<Popup>(comboBox));
            var popupChild = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            popupChild.Measure(new Size(220, double.PositiveInfinity));

            var popupRoot = new PopupRoot(popup, popupChild, isLightDismiss: false);
            using var popupWindow = new PopupWindow(new Window { Width = 400, Height = 300 }, popupRoot);
            var popupSize = new Size(220, popupChild.DesiredSize.Height);
            popupWindow.Measure(popupSize);
            popupWindow.Arrange(new Rect(0, 0, popupSize.Width, popupSize.Height));
            popupWindow.SetVisualBounds(new Rect(0, 0, popupSize.Width, popupSize.Height));

            var itemsHost = Assert.IsAssignableFrom<Panel>(
                typeof(ItemsControl)
                    .GetProperty("ItemsHost", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(comboBox));
            var third = Assert.IsType<ComboBoxItem>(itemsHost.Children[2]);
            Assert.Same(third, comboBox.ItemContainerGenerator.ContainerFromIndex(2));
            Assert.Equal(2, comboBox.ItemContainerGenerator.IndexFromContainer(third));
            Assert.Equal("NUnit", comboBox.ItemContainerGenerator.ItemFromContainer(third));
            var position = third.TranslatePoint(
                new Point(third.RenderSize.Width / 2, third.RenderSize.Height / 2),
                popupWindow);

            InvokeMousePointerUpdate(popupWindow, position);
            Assert.True(third.IsMouseOver);
            Assert.True(third.IsHighlighted);

            InvokeMouse(popupWindow, "OnMouseButtonDown", 1, position, MouseButton.Left);
            InvokeMouse(popupWindow, "OnMouseButtonUp", 0, position, MouseButton.Left);

            Assert.Equal("NUnit", comboBox.SelectedItem);
            Assert.Equal("NUnit", comboBox.SelectionBoxItem);
            Assert.False(comboBox.IsDropDownOpen);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    private static void InvokeMouse(
        PopupWindow popupWindow,
        string methodName,
        int buttonFlags,
        Point position,
        MouseButton? button = null)
    {
        var parameterTypes = button.HasValue
            ? new[] { typeof(MouseButton), typeof(nint), typeof(nint), typeof(ModifierKeys?), typeof(int) }
            : new[] { typeof(nint), typeof(nint), typeof(ModifierKeys?), typeof(bool) };
        var method = typeof(PopupWindow).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: parameterTypes,
            modifiers: null)
            ?? throw new InvalidOperationException($"Popup mouse method '{methodName}' was not found.");
        var packedPoint = PackClientPoint(position);

        if (button is { } changedButton)
        {
            method.Invoke(popupWindow, [changedButton, (nint)buttonFlags, packedPoint, null, 1]);
        }
        else
        {
            method.Invoke(popupWindow, [(nint)buttonFlags, packedPoint, null, false]);
        }
    }

    private static void InvokeMousePointerUpdate(PopupWindow popupWindow, Point position)
    {
        var properties = new PointerPointProperties
        {
            IsPrimary = true,
            PointerUpdateKind = PointerUpdateKind.Other,
        };
        var point = new PointerPoint(
            1,
            position,
            PointerDeviceType.Mouse,
            isInContact: false,
            properties,
            (ulong)Environment.TickCount,
            frameId: 0);
        var data = new PointerInputData(
            1,
            PointerInputKind.Mouse,
            point,
            position,
            ModifierKeys.None,
            IsInRange: true,
            IsCanceled: false,
            new StylusPointCollection([new StylusPoint(position.X, position.Y)]));
        var method = typeof(PopupWindow).GetMethod(
            "DispatchMousePointerUpdate",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Popup mouse-pointer update bridge was not found.");

        method.Invoke(popupWindow, [data]);
    }

    private static nint PackClientPoint(Point position)
    {
        var x = unchecked((ushort)(short)Math.Round(position.X));
        var y = unchecked((ushort)(short)Math.Round(position.Y));
        return (nint)(x | (y << 16));
    }

    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        if (root is T match)
        {
            return match;
        }

        for (var index = 0; index < root.VisualChildrenCount; index++)
        {
            if (root.GetVisualChild(index) is { } child && FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void ResetApplicationState()
    {
        typeof(Application)
            .GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)?
            .SetValue(null, null);
        typeof(ThemeManager)
            .GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)?
            .Invoke(null, null);
    }

    private sealed class InitialSelectionSource
    {
        public IReadOnlyList<string> Items { get; } =
            ["xUnit v3", "xUnit v2", "NUnit", "MSTest", "TUnit", "Fixie"];

        public string SelectedItem { get; set; } = "xUnit v3";
    }
}
