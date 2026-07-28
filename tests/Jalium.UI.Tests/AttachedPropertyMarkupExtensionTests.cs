using System.ComponentModel;
using Jalium.UI.Controls;
using Jalium.UI.Markup;

namespace Jalium.UI.Tests;

public sealed class AttachedPropertyMarkupExtensionTests
{
    [Fact]
    public void Binding_OnAttachedDependencyProperty_TracksDataContext()
    {
        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Grid.Row="{Binding Row}" />
            </Grid>
            """;

        var grid = Assert.IsType<Grid>(XamlReader.Parse(xaml));
        var textBlock = Assert.IsType<TextBlock>(
            Assert.Single(grid.Children));
        var source = new RowSource { Row = 2 };

        textBlock.DataContext = source;

        Assert.Equal(2, Grid.GetRow(textBlock));

        source.Row = 4;

        Assert.Equal(4, Grid.GetRow(textBlock));
    }

    private sealed class RowSource : INotifyPropertyChanged
    {
        private int _row;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Row
        {
            get => _row;
            set
            {
                if (_row == value)
                {
                    return;
                }

                _row = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(
                        nameof(Row)));
            }
        }
    }
}
