using Jalium.UI;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

public sealed class DependencyPropertyTypeSafetyTests
{
    [Fact]
    public void SetValue_NonNullTypeMismatch_ThrowsWithoutPublishingLocalValue()
    {
        var target = new TypeSafeTarget();

        Assert.Throws<ArgumentException>(
            () => target.SetValue(TypeSafeTarget.NameProperty, 42));

        Assert.Equal("safe", target.Name);
        Assert.False(target.HasLocalValue(TypeSafeTarget.NameProperty));
    }

    [Fact]
    public void SetCurrentValue_NonNullTypeMismatch_ThrowsWithoutChangingValue()
    {
        var target = new TypeSafeTarget();

        Assert.Throws<ArgumentException>(
            () => target.SetCurrentValue(TypeSafeTarget.NameProperty, new object()));

        Assert.Equal("safe", target.Name);
    }

    [Fact]
    public void Register_NonNullTypeMismatchInDefaultMetadata_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => DependencyProperty.Register(
                "InvalidRegistrationDefault",
                typeof(int),
                typeof(InvalidRegistrationOwner),
                new PropertyMetadata("not an integer")));
    }

    [Fact]
    public void OverrideMetadata_NonNullTypeMismatchInDefault_ThrowsWithoutPublishingMetadata()
    {
        var property = DependencyProperty.Register(
            "InvalidOverrideDefault",
            typeof(int),
            typeof(MetadataBaseOwner),
            new PropertyMetadata(17));

        Assert.Throws<ArgumentException>(
            () => property.OverrideMetadata(
                typeof(MetadataDerivedOwner),
                new PropertyMetadata("not an integer")));

        Assert.Equal(17, property.GetMetadata(typeof(MetadataDerivedOwner)).DefaultValue);
    }

    [Fact]
    public void NullableProperty_AcceptsNullAndBoxedUnderlyingValue()
    {
        var target = new TypeSafeTarget();

        target.SetValue(TypeSafeTarget.NullableCountProperty, 23);
        Assert.Equal(23, target.NullableCount);

        target.SetValue(TypeSafeTarget.NullableCountProperty, null);
        Assert.Null(target.NullableCount);
    }

    [Fact]
    public void TransitionProperty_DefaultCollection_IsTypeSafeAndPerElement()
    {
        Assert.Null(UIElement.TransitionPropertyProperty.DefaultMetadata.DefaultValue);

        var first = new TransitionTarget();
        var second = new TransitionTarget();

        Assert.NotSame(first.TransitionProperty, second.TransitionProperty);
        first.TransitionProperty.Add(nameof(UIElement.Opacity));

        Assert.True(first.TransitionProperty.Matches(nameof(UIElement.Opacity)));
        Assert.True(second.TransitionProperty.IsNone);
    }

    [Fact]
    public void Binding_UnconvertibleTargetValue_FailsClosedAndRecovers()
    {
        var source = new BindingSource { Value = new object() };
        var target = new TypeSafeTarget();

        var expression = BindingOperations.SetBinding(
            target,
            TypeSafeTarget.PaddingProperty,
            new Binding(nameof(BindingSource.Value)) { Source = source });

        Assert.Equal(new Thickness(7), target.Padding);
        Assert.False(target.HasLocalValue(TypeSafeTarget.PaddingProperty));
        Assert.Equal(BindingStatus.UpdateTargetError, expression.Status);

        source.Value = new Thickness(3);
        expression.UpdateTarget();

        Assert.Equal(new Thickness(3), target.Padding);
        Assert.Equal(BindingStatus.Active, expression.Status);
    }

    private sealed class TypeSafeTarget : DependencyObject
    {
        public static readonly DependencyProperty NameProperty =
            DependencyProperty.Register(
                nameof(Name),
                typeof(string),
                typeof(TypeSafeTarget),
                new PropertyMetadata("safe"));

        public static readonly DependencyProperty PaddingProperty =
            DependencyProperty.Register(
                nameof(Padding),
                typeof(Thickness),
                typeof(TypeSafeTarget),
                new PropertyMetadata(new Thickness(7)));

        public static readonly DependencyProperty NullableCountProperty =
            DependencyProperty.Register(
                nameof(NullableCount),
                typeof(int?),
                typeof(TypeSafeTarget),
                new PropertyMetadata(null));

        public string? Name => (string?)GetValue(NameProperty);

        public Thickness Padding => (Thickness)GetValue(PaddingProperty)!;

        public int? NullableCount => (int?)GetValue(NullableCountProperty);
    }

    private sealed class BindingSource
    {
        public object? Value { get; set; }
    }

    private sealed class TransitionTarget : UIElement { }
    private sealed class InvalidRegistrationOwner : DependencyObject { }
    private class MetadataBaseOwner : DependencyObject { }
    private sealed class MetadataDerivedOwner : MetadataBaseOwner { }
}
