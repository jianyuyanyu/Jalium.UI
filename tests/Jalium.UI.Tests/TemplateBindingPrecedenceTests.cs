using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

public class TemplateBindingPrecedenceTests
{
    [Fact]
    public void TemplateBinding_ShouldWriteParentTemplateLayer_NotLocal()
    {
        var control = new TemplateProbeControl
        {
            Width = 200,
            Height = 32,
            Tag = "First"
        };

        control.Template = BuildTemplate();
        control.Measure(new Size(300, 100));
        control.Arrange(new Rect(0, 0, 300, 100));

        var textBlock = Assert.IsType<TextBlock>(control.GetVisualChild(0));
        Assert.Equal("First", textBlock.Text);
        Assert.False(textBlock.HasLocalValue(TextBlock.TextProperty));
        Assert.Equal(BaseValueSource.ParentTemplate, DependencyPropertyHelper.GetValueSource(textBlock, TextBlock.TextProperty).BaseValueSource);

        control.Tag = "Second";
        Assert.Equal("Second", textBlock.Text);
        Assert.Equal(BaseValueSource.ParentTemplate, DependencyPropertyHelper.GetValueSource(textBlock, TextBlock.TextProperty).BaseValueSource);
    }

    [Fact]
    public void TemplateTrigger_ShouldOverrideTemplateLiteralValue()
    {
        var control = new TemplateProbeControl
        {
            Width = 200,
            Height = 32,
            Tag = "On",
            Template = BuildTemplateWithTrigger()
        };

        control.Measure(new Size(300, 100));
        control.Arrange(new Rect(0, 0, 300, 100));

        var textBlock = Assert.IsType<TextBlock>(control.GetVisualChild(0));
        Assert.Equal("Triggered", textBlock.Text);
        // TargetName 指向模板内具名部件的 trigger 是 WPF 的 ParentTemplateTrigger 级
        // （模板对自己生成物的支配），优先级仅次于 local。
        Assert.Equal(BaseValueSource.ParentTemplateTrigger, DependencyPropertyHelper.GetValueSource(textBlock, TextBlock.TextProperty).BaseValueSource);

        control.Tag = "Off";
        Assert.Equal("Base", textBlock.Text);
        Assert.Equal(BaseValueSource.ParentTemplate, DependencyPropertyHelper.GetValueSource(textBlock, TextBlock.TextProperty).BaseValueSource);
    }

    private static ControlTemplate BuildTemplate()
    {
        var template = new ControlTemplate(typeof(TemplateProbeControl));
        template.SetVisualTree(() =>
        {
            var text = new TextBlock();
            text.SetTemplateBinding(TextBlock.TextProperty, FrameworkElement.TagProperty);
            return text;
        });
        return template;
    }

    private static ControlTemplate BuildTemplateWithTrigger()
    {
        var template = new ControlTemplate(typeof(TemplateProbeControl));
        template.SetVisualTree(() =>
        {
            return new TextBlock
            {
                Name = "PART_Text",
                Text = "Base"
            };
        });

        var trigger = new Trigger
        {
            Property = FrameworkElement.TagProperty,
            Value = "On"
        };
        trigger.Setters.Add(new Setter
        {
            TargetName = "PART_Text",
            Property = TextBlock.TextProperty,
            Value = "Triggered"
        });
        template.Triggers.Add(trigger);

        return template;
    }

    private sealed class TemplateProbeControl : Control
    {
    }
}
