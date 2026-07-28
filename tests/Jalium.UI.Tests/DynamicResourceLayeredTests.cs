using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Media;
using Jalium.UI.Markup;
using Jalium.UI.Shapes;
using ShapePath = Jalium.UI.Shapes.Path;

namespace Jalium.UI.Tests;

[Collection("Application")]
public class DynamicResourceLayeredTests
{
    [Fact]
    public void StyleSetterDynamicResource_ShouldRefreshInStyleLayer_AndClearWhenMissing()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            var brush1 = new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x30));
            var brush2 = new SolidColorBrush(Color.FromRgb(0x30, 0x50, 0x70));
            app.Resources["ProbeBrush"] = brush1;

            var border = new Border();
            border.Style = new Style(typeof(Border))
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, new DynamicResourceReference("ProbeBrush"))
                }
            };

            Assert.Same(brush1, border.Background);
            Assert.Equal(BaseValueSource.Style, DependencyPropertyHelper.GetValueSource(border, Border.BackgroundProperty).BaseValueSource);

            app.Resources["ProbeBrush"] = brush2;
            DynamicResourceBindingOperations.RefreshAll();
            Assert.Same(brush2, border.Background);
            Assert.Equal(BaseValueSource.Style, DependencyPropertyHelper.GetValueSource(border, Border.BackgroundProperty).BaseValueSource);

            app.Resources.Remove("ProbeBrush");
            DynamicResourceBindingOperations.RefreshAll();
            Assert.Null(border.Background);
            Assert.Equal(BaseValueSource.Default, DependencyPropertyHelper.GetValueSource(border, Border.BackgroundProperty).BaseValueSource);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void InlineDynamicResource_OnDetachedElement_ResolvesWhenAttachedToAncestorWithResource()
    {
        // Reproduces the IDE toolbar "empty icon" bug: an inline content element such as
        // <Path Stroke="{DynamicResource Foo}"> has its dynamic resource wired up by the
        // XAML source generator while the element is still detached. If the key is not
        // reachable at that instant the value resolves to null, and — because Shape.Stroke
        // defaults to null — the shape draws nothing. The fix re-resolves dynamic resources
        // when the element gains a visual parent, the same way implicit styles are re-run.
        ResetApplicationState();
        try
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));

            // Wire the dynamic resource up while detached (no Application, no parent) — the
            // key is unreachable, so it resolves to null exactly like at XAML build time.
            var path = new ShapePath { Data = "M3,5 L13,5", StrokeThickness = 1.4, Width = 14, Height = 14 };
            DynamicResourceBindingOperations.SetDynamicResource(path, Shape.StrokeProperty, "ProbeBrush");
            Assert.Null(path.Stroke); // precondition: unresolved while detached

            // The key only becomes reachable through an ancestor once attached to the tree.
            var host = new Grid();
            host.Resources["ProbeBrush"] = brush;
            host.Children.Add(path);

            // After the fix, attaching re-resolves the inline dynamic resource.
            Assert.Same(brush, path.Stroke);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void TriggerDynamicResource_ShouldNotDestroyLowerStyleResourceSubscription()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            var baseBrush1 = new SolidColorBrush(Color.FromRgb(0x21, 0x31, 0x41));
            var baseBrush2 = new SolidColorBrush(Color.FromRgb(0x31, 0x51, 0x71));
            var triggerBrush = new SolidColorBrush(Color.FromRgb(0x08, 0x94, 0x8A));
            app.Resources["BaseBrush"] = baseBrush1;
            app.Resources["TriggerBrush"] = triggerBrush;

            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new DynamicResourceReference("BaseBrush")));
            var trigger = new Trigger
            {
                Property = FrameworkElement.TagProperty,
                Value = "active"
            };
            trigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new DynamicResourceReference("TriggerBrush")));
            style.Triggers.Add(trigger);

            var border = new Border { Style = style };
            Assert.Same(baseBrush1, border.Background);

            border.Tag = "active";
            Assert.Same(triggerBrush, border.Background);

            border.Tag = null;
            Assert.Same(baseBrush1, border.Background);

            app.Resources["BaseBrush"] = baseBrush2;
            DynamicResourceBindingOperations.RefreshAll();

            Assert.Same(baseBrush2, border.Background);
            Assert.Equal(
                BaseValueSource.Style,
                DependencyPropertyHelper.GetValueSource(
                    border,
                    Border.BackgroundProperty).BaseValueSource);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void LaterConstantTrigger_ShouldRemainWinnerWhenEarlierResourceRefreshes()
    {
        var earlier1 = new object();
        var earlier2 = new object();
        var later = new object();
        var element = new TriggerResourceProbe();
        element.Resources["Earlier"] = earlier1;

        var style = new Style(typeof(TriggerResourceProbe));
        var earlierTrigger = new Trigger
        {
            Property = TriggerResourceProbe.FirstFlagProperty,
            Value = true
        };
        earlierTrigger.Setters.Add(new Setter(
            TriggerResourceProbe.TokenProperty,
            new DynamicResourceReference("Earlier")));
        style.Triggers.Add(earlierTrigger);

        var laterTrigger = new Trigger
        {
            Property = TriggerResourceProbe.SecondFlagProperty,
            Value = true
        };
        laterTrigger.Setters.Add(new Setter(TriggerResourceProbe.TokenProperty, later));
        style.Triggers.Add(laterTrigger);
        element.Style = style;

        element.FirstFlag = true;
        Assert.Same(earlier1, element.Token);

        element.SecondFlag = true;
        Assert.Same(later, element.Token);

        element.Resources["Earlier"] = earlier2;
        DynamicResourceBindingOperations.RefreshAll();
        Assert.Same(later, element.Token);

        element.SecondFlag = false;
        Assert.Same(earlier2, element.Token);
    }

    private static void ResetApplicationState()
    {
        var currentField = typeof(Application).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static);
        currentField?.SetValue(null, null);

        var resetMethod = typeof(ThemeManager).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static);
        resetMethod?.Invoke(null, null);
    }

    private sealed class TriggerResourceProbe : FrameworkElement
    {
        public static readonly DependencyProperty TokenProperty =
            DependencyProperty.Register(
                nameof(Token),
                typeof(object),
                typeof(TriggerResourceProbe),
                new PropertyMetadata(null));

        public static readonly DependencyProperty FirstFlagProperty =
            DependencyProperty.Register(
                nameof(FirstFlag),
                typeof(bool),
                typeof(TriggerResourceProbe),
                new PropertyMetadata(false));

        public static readonly DependencyProperty SecondFlagProperty =
            DependencyProperty.Register(
                nameof(SecondFlag),
                typeof(bool),
                typeof(TriggerResourceProbe),
                new PropertyMetadata(false));

        public object? Token => GetValue(TokenProperty);

        public bool FirstFlag
        {
            get => (bool)(GetValue(FirstFlagProperty) ?? false);
            set => SetValue(FirstFlagProperty, value);
        }

        public bool SecondFlag
        {
            get => (bool)(GetValue(SecondFlagProperty) ?? false);
            set => SetValue(SecondFlagProperty, value);
        }
    }
}
