using Jalium.UI;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

public class StyleTriggerPrecedenceTests
{
    [Fact]
    public void StyleTrigger_ShouldOverrideStyleSetter_AndRestoreOnDeactivate()
    {
        var element = new TriggerProbeElement();
        element.Style = BuildStyle();

        Assert.Equal("Style", element.GetValue(TriggerProbeElement.TokenProperty));
        Assert.Equal(BaseValueSource.Style, DependencyPropertyHelper.GetValueSource(element, TriggerProbeElement.TokenProperty).BaseValueSource);

        element.SetValue(TriggerProbeElement.FlagProperty, true);
        Assert.Equal("Flag", element.GetValue(TriggerProbeElement.TokenProperty));
        Assert.Equal(BaseValueSource.StyleTrigger, DependencyPropertyHelper.GetValueSource(element, TriggerProbeElement.TokenProperty).BaseValueSource);

        element.SetValue(TriggerProbeElement.FlagProperty, false);
        Assert.Equal("Style", element.GetValue(TriggerProbeElement.TokenProperty));
        Assert.Equal(BaseValueSource.Style, DependencyPropertyHelper.GetValueSource(element, TriggerProbeElement.TokenProperty).BaseValueSource);
    }

    [Fact]
    public void MultipleActiveTriggers_OnSameProperty_ShouldRespectActivationAndReapply()
    {
        var element = new TriggerProbeElement();
        element.Style = BuildStyle();

        element.SetValue(TriggerProbeElement.FlagProperty, true);
        Assert.Equal("Flag", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.AltFlagProperty, true);
        Assert.Equal("Alt", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.AltFlagProperty, false);
        Assert.Equal("Flag", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.FlagProperty, false);
        Assert.Equal("Style", element.GetValue(TriggerProbeElement.TokenProperty));
    }

    [Fact]
    public void BasedOnAndDerivedTriggers_ShouldUseCompleteAppliedStyleOrder()
    {
        var baseStyle = new Style(typeof(TriggerProbeElement));
        baseStyle.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "BaseStyle"));
        var baseTrigger = new Trigger
        {
            Property = TriggerProbeElement.FlagProperty,
            Value = true
        };
        baseTrigger.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "BaseTrigger"));
        baseStyle.Triggers.Add(baseTrigger);

        var derivedStyle = new Style(typeof(TriggerProbeElement), baseStyle);
        derivedStyle.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "DerivedStyle"));
        var derivedTrigger = new Trigger
        {
            Property = TriggerProbeElement.AltFlagProperty,
            Value = true
        };
        derivedTrigger.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "DerivedTrigger"));
        derivedStyle.Triggers.Add(derivedTrigger);

        var element = new TriggerProbeElement { Style = derivedStyle };
        Assert.Equal("DerivedStyle", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.AltFlagProperty, true);
        Assert.Equal("DerivedTrigger", element.GetValue(TriggerProbeElement.TokenProperty));

        // Activating the base trigger later must not overwrite the later, derived trigger.
        element.SetValue(TriggerProbeElement.FlagProperty, true);
        Assert.Equal("DerivedTrigger", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.AltFlagProperty, false);
        Assert.Equal("BaseTrigger", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.FlagProperty, false);
        Assert.Equal("DerivedStyle", element.GetValue(TriggerProbeElement.TokenProperty));
    }

    [Fact]
    public void StackedStyles_ShouldResolveTriggersByActualApplicationOrder()
    {
        var lowerStyle = new Style(typeof(TriggerProbeElement));
        var lowerTrigger = new Trigger
        {
            Property = TriggerProbeElement.FlagProperty,
            Value = true
        };
        lowerTrigger.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "LowerTrigger"));
        lowerStyle.Triggers.Add(lowerTrigger);

        var upperStyle = new Style(typeof(TriggerProbeElement));
        var upperTrigger = new Trigger
        {
            Property = TriggerProbeElement.AltFlagProperty,
            Value = true
        };
        upperTrigger.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "UpperTrigger"));
        upperStyle.Triggers.Add(upperTrigger);

        var element = new TriggerProbeElement();
        lowerStyle.Apply(element);
        upperStyle.Apply(element);

        element.SetValue(TriggerProbeElement.AltFlagProperty, true);
        element.SetValue(TriggerProbeElement.FlagProperty, true);
        Assert.Equal("UpperTrigger", element.GetValue(TriggerProbeElement.TokenProperty));

        element.SetValue(TriggerProbeElement.AltFlagProperty, false);
        Assert.Equal("LowerTrigger", element.GetValue(TriggerProbeElement.TokenProperty));

        upperStyle.Remove(element);
        lowerStyle.Remove(element);
    }

    [Fact]
    public void SharedBindingTrigger_ShouldAttachSafelyAcrossConcurrentElements()
    {
        var style = new Style(typeof(TriggerProbeElement));
        var trigger = new Trigger
        {
            Property = TriggerProbeElement.FlagProperty,
            Value = true
        };
        trigger.Setters.Add(new Setter(
            TriggerProbeElement.TokenProperty,
            new Binding(nameof(BindingProbe.Token))));
        style.Triggers.Add(trigger);
        style.Seal();

        var values = new string?[64];
        Parallel.For(0, values.Length, index =>
        {
            var expected = $"value-{index}";
            var element = new TriggerProbeElement
            {
                DataContext = new BindingProbe { Token = expected }
            };
            element.SetValue(TriggerProbeElement.FlagProperty, true);
            element.Style = style;
            values[index] = element.GetValue(TriggerProbeElement.TokenProperty) as string;
        });

        for (var index = 0; index < values.Length; index++)
        {
            Assert.Equal($"value-{index}", values[index]);
        }
    }

    [Fact]
    public void LocalValue_ShouldOutrankStyleTrigger()
    {
        var element = new TriggerProbeElement();
        element.Style = BuildStyle();
        element.SetValue(TriggerProbeElement.FlagProperty, true);

        element.SetValue(TriggerProbeElement.TokenProperty, "Local");
        Assert.Equal("Local", element.GetValue(TriggerProbeElement.TokenProperty));
        Assert.Equal(BaseValueSource.Local, DependencyPropertyHelper.GetValueSource(element, TriggerProbeElement.TokenProperty).BaseValueSource);

        element.ClearValue(TriggerProbeElement.TokenProperty);
        Assert.Equal("Flag", element.GetValue(TriggerProbeElement.TokenProperty));
        Assert.Equal(BaseValueSource.StyleTrigger, DependencyPropertyHelper.GetValueSource(element, TriggerProbeElement.TokenProperty).BaseValueSource);
    }

    private static Style BuildStyle()
    {
        var style = new Style(typeof(TriggerProbeElement));
        style.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "Style"));

        var trigger1 = new Trigger
        {
            Property = TriggerProbeElement.FlagProperty,
            Value = true
        };
        trigger1.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "Flag"));
        style.Triggers.Add(trigger1);

        var trigger2 = new Trigger
        {
            Property = TriggerProbeElement.AltFlagProperty,
            Value = true
        };
        trigger2.Setters.Add(new Setter(TriggerProbeElement.TokenProperty, "Alt"));
        style.Triggers.Add(trigger2);

        return style;
    }

    private sealed class TriggerProbeElement : FrameworkElement
    {
        public static readonly DependencyProperty TokenProperty =
            DependencyProperty.Register(
                "Token",
                typeof(string),
                typeof(TriggerProbeElement),
                new PropertyMetadata("Default"));

        public static readonly DependencyProperty FlagProperty =
            DependencyProperty.Register(
                "Flag",
                typeof(bool),
                typeof(TriggerProbeElement),
                new PropertyMetadata(false));

        public static readonly DependencyProperty AltFlagProperty =
            DependencyProperty.Register(
                "AltFlag",
                typeof(bool),
                typeof(TriggerProbeElement),
                new PropertyMetadata(false));
    }

    private sealed class BindingProbe
    {
        public string? Token { get; init; }
    }
}
