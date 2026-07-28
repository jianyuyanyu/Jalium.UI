using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Markup;

namespace Jalium.UI.Tests;

public sealed class EventTriggerRemainingParityTests
{
    [Fact]
    public void ActionsUseCanonicalCollectionAndMarkupChildContract()
    {
        Assert.Equal(
            typeof(TriggerActionCollection),
            typeof(EventTrigger).GetProperty(nameof(EventTrigger.Actions))!.PropertyType);
        Assert.True(typeof(IAddChild).IsAssignableFrom(typeof(EventTrigger)));

        var trigger = new ProbeEventTrigger();
        var action = new ProbeAction();
        trigger.Add(action);
        trigger.AddWhitespace(" \r\n");

        Assert.Same(action, Assert.Single(trigger.Actions));
        Assert.True(trigger.ShouldSerializeActions());
        Assert.Throws<ArgumentException>(() => trigger.Add(new object()));
        Assert.Throws<ArgumentException>(() => trigger.AddWhitespace("content"));
    }

    [Fact]
    public void AddChildAndAddTextAreProtectedVirtual()
    {
        foreach (string methodName in new[] { "AddChild", "AddText" })
        {
            MethodInfo method = typeof(EventTrigger).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
            Assert.True(method.IsFamily);
            Assert.True(method.IsVirtual);
            Assert.False(method.IsFinal);
        }
    }

    [Fact]
    public void SharedStyleEventTrigger_KeepsIndependentElementAttachments()
    {
        var action = new RecordingAction();
        var trigger = new EventTrigger(Button.ClickEvent);
        trigger.Actions.Add(action);
        var style = new Style(typeof(Button));
        style.Triggers.Add(trigger);

        var first = new Button { Style = style };
        var second = new Button { Style = style };

        first.PerformClick();
        second.PerformClick();

        Assert.Equal(new FrameworkElement[] { first, second }, action.Invocations);

        first.Style = null;
        second.PerformClick();
        first.PerformClick();

        Assert.Equal(new FrameworkElement[] { first, second, second }, action.Invocations);
    }

    [Fact]
    public void Detach_ShouldRemoveTheOriginallyAttachedRoutedEvent()
    {
        var action = new RecordingAction();
        var trigger = new EventTrigger(Button.ClickEvent);
        trigger.Actions.Add(action);
        var button = new Button();
        button.Triggers.Add(trigger);

        trigger.RoutedEvent = Button.MouseDownEvent;
        button.Triggers.Remove(trigger);
        button.PerformClick();

        Assert.Empty(action.Invocations);
    }

    private sealed class ProbeEventTrigger : EventTrigger
    {
        public void Add(object value) => AddChild(value);
        public void AddWhitespace(string text) => AddText(text);
    }

    private sealed class ProbeAction : TriggerAction
    {
        internal override void Invoke(FrameworkElement? element)
        {
        }
    }

    private sealed class RecordingAction : TriggerAction
    {
        public List<FrameworkElement> Invocations { get; } = new();

        internal override void Invoke(FrameworkElement? element)
        {
            if (element != null)
            {
                Invocations.Add(element);
            }
        }
    }
}
