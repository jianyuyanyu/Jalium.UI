namespace Jalium.UI.Tests;

public sealed class DependencyPropertyMetadataRuntimeConsumptionTests
{
    [Fact]
    public void DerivedMetadataCoerceCallbackParticipatesInEffectiveValue()
    {
        var element = new CoercingElement();

        element.SetValue(CoerceOwner.ValueProperty, 4);
        element.ResetCoerceInvocationCount();

        Assert.Equal(5, element.GetValue(CoerceOwner.ValueProperty));
        Assert.Equal(1, element.CoerceInvocationCount);
    }

    [Fact]
    public void DerivedFrameworkMetadataCanEnableInheritance()
    {
        var parent = new InheritingElement();
        var child = new InheritingElement();
        parent.Attach(child);
        parent.SetValue(InheritanceOwner.ValueProperty, 42);

        Assert.Equal(42, child.GetValue(InheritanceOwner.ValueProperty));
    }

    private class CoerceOwner : FrameworkElement
    {
        internal static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "RuntimeMetadataCoerceValue",
            typeof(int),
            typeof(CoerceOwner),
            new PropertyMetadata(0));
    }

    private sealed class CoercingElement : CoerceOwner
    {
        static CoercingElement()
        {
            ValueProperty.OverrideMetadata(
                typeof(CoercingElement),
                new PropertyMetadata(0, null, static (owner, value) =>
                {
                    ((CoercingElement)owner).CoerceInvocationCount++;
                    return (int)value! + 1;
                }));
        }

        internal int CoerceInvocationCount { get; private set; }

        internal void ResetCoerceInvocationCount() => CoerceInvocationCount = 0;
    }

    private class InheritanceOwner : FrameworkElement
    {
        internal static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "RuntimeMetadataInheritedValue",
            typeof(int),
            typeof(InheritanceOwner),
            new FrameworkPropertyMetadata(0));
    }

    private sealed class InheritingElement : InheritanceOwner
    {
        static InheritingElement()
        {
            ValueProperty.OverrideMetadata(
                typeof(InheritingElement),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));
        }

        internal void Attach(InheritingElement child) => AddVisualChild(child);
    }
}
