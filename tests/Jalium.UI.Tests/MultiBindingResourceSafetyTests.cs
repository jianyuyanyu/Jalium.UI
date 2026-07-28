using Jalium.UI;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

public sealed class MultiBindingResourceSafetyTests
{
    [Fact]
    public void RecycledExpressions_ReuseShadowDependencyProperties()
    {
        var source = new SourceValues();
        int before = MultiBindingExpression.ShadowPropertyCacheCount;

        for (int iteration = 0; iteration < 256; iteration++)
        {
            var target = new BindingTarget();
            var binding = new MultiBinding();
            binding.Bindings.Add(new Binding(nameof(SourceValues.First)) { Source = source });
            binding.Bindings.Add(new Binding(nameof(SourceValues.Second)) { Source = source });

            BindingOperations.SetBinding(target, BindingTarget.ValueProperty, binding);
            Assert.Equal("first", target.Value);
            BindingOperations.ClearBinding(target, BindingTarget.ValueProperty);
        }

        int after = MultiBindingExpression.ShadowPropertyCacheCount;
        Assert.InRange(after - before, 0, 2);
    }

    private sealed class SourceValues
    {
        public string First { get; } = "first";
        public string Second { get; } = "second";
    }

    private sealed class BindingTarget : DependencyObject
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(string),
                typeof(BindingTarget),
                new PropertyMetadata(null));

        public string? Value => (string?)GetValue(ValueProperty);
    }
}
