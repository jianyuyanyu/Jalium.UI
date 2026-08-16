using System.ComponentModel;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

public sealed class SelectorSelectionBindingTests
{
    [Theory]
    [InlineData(nameof(Selector.SelectedIndexProperty))]
    [InlineData(nameof(Selector.SelectedItemProperty))]
    [InlineData(nameof(Selector.SelectedValueProperty))]
    public void SelectionProperty_DefaultBindingMode_IsTwoWay(string fieldName)
    {
        var field = typeof(Selector).GetField(fieldName);
        var property = Assert.IsType<DependencyProperty>(field?.GetValue(null));
        var metadata = Assert.IsType<FrameworkPropertyMetadata>(
            property.GetMetadata(typeof(ComboBox)));

        Assert.True(metadata.BindsTwoWayByDefault);
    }

    [Fact]
    public void ComboBox_DefaultSelectedItemBinding_UpdatesSourceAndSelectionBox()
    {
        var source = new SelectionSource();
        var comboBox = new ComboBox();
        comboBox.Items.Add("Alpha");
        comboBox.Items.Add("Beta");
        comboBox.SetBinding(
            Selector.SelectedItemProperty,
            new Binding(nameof(SelectionSource.SelectedItem)) { Source = source });

        comboBox.SelectedIndex = 1;

        Assert.Equal("Beta", source.SelectedItem);
        Assert.Equal("Beta", comboBox.SelectedItem);
        Assert.Equal("Beta", comboBox.SelectionBoxItem);
        Assert.NotNull(comboBox.GetBindingExpression(Selector.SelectedItemProperty));
    }

    private sealed class SelectionSource : INotifyPropertyChanged
    {
        private object? _selectedItem;

        public object? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (Equals(_selectedItem, value)) return;
                _selectedItem = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(SelectedItem)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
