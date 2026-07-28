using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

public sealed class RadioButtonGroupingConcurrencyTests
{
    [Fact]
    public void SharedGroupRegistry_ShouldHandleIndependentUiThreads()
    {
        Parallel.For(0, 256, index =>
        {
            var parent = new StackPanel();
            var radioButton = new RadioButton
            {
                GroupName = $"concurrent-group-{index % 4}"
            };

            parent.Children.Add(radioButton);
            parent.Children.Remove(radioButton);
        });
    }
}
