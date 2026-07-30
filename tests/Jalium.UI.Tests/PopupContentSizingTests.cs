using System.ComponentModel;
using System.Reflection;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class PopupContentSizingTests
{
    [Fact]
    public void OpenPopup_MeasuresDataBoundContentAfterPopupDataContextInheritance()
    {
        var title = new TextBlock
        {
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PopupViewModel.Title)));

        var metadata = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 14),
        };
        metadata.SetBinding(TextBlock.TextProperty, new Binding(nameof(PopupViewModel.Metadata)));

        var actions = new Border
        {
            Width = 210,
            Height = 32,
        };

        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(metadata);
        content.Children.Add(actions);

        var card = new Border
        {
            Padding = new Thickness(16),
            MinWidth = 260,
            MaxWidth = 420,
            Child = content,
        };

        var host = new Grid
        {
            Width = 800,
            Height = 600,
        };
        var popup = new Popup
        {
            DataContext = new PopupViewModel(
                "A bound popup title that contributes to desired height",
                "Confidence 62% · position 43,142 · 118 × 23 px"),
            Child = card,
            PlacementTarget = host,
            ShouldConstrainToRootBounds = true,
            StaysOpen = true,
        };
        host.Children.Add(popup);

        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = host,
        };

        try
        {
            popup.IsOpen = true;
            window.Measure(new Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));

            var popupRoot = GetPopupRoot(popup);
            var actionsBottom = actions.VisualBounds.Y + actions.ActualHeight;

            Assert.NotEmpty(title.Text);
            Assert.NotEmpty(metadata.Text);
            Assert.True(
                popupRoot.Height + 0.01 >= card.DesiredSize.Height,
                $"Popup host height {popupRoot.Height} did not include the bound card height {card.DesiredSize.Height}.");
            Assert.True(
                actionsBottom <= content.ActualHeight + 0.01,
                $"Action row bottom {actionsBottom} overflowed content height {content.ActualHeight}.");
        }
        finally
        {
            popup.IsOpen = false;
        }
    }

    [Fact]
    public void ItemTemplatePopup_OpenedAfterLayout_KeepsTemplatedActionsInsideCard()
    {
        var model = new BindablePopupViewModel(
            "C# IcoImageLoader.cs",
            "Confidence 96% · position 164,327 · 158 × 15 px");
        Popup? popup = null;
        Border? card = null;
        StackPanel? content = null;
        StackPanel? actions = null;

        var buttonTemplate = new ControlTemplate(typeof(Button));
        buttonTemplate.SetVisualTree(() => new Border
        {
            Child = new ContentPresenter
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        var itemTemplate = new DataTemplate();
        itemTemplate.SetVisualTree(() =>
        {
            var placementTarget = new Button
            {
                Width = 158,
                Height = 23,
            };

            var title = new TextBlock
            {
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(BindablePopupViewModel.Title)));

            var metadata = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 7, 0, 14),
            };
            metadata.SetBinding(TextBlock.TextProperty, new Binding(nameof(BindablePopupViewModel.Metadata)));

            actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            actions.Children.Add(CreateActionButton("Copy all", buttonTemplate));
            actions.Children.Add(CreateActionButton("Copy line", buttonTemplate));

            content = new StackPanel();
            content.Children.Add(title);
            content.Children.Add(metadata);
            content.Children.Add(actions);

            card = new Border
            {
                Padding = new Thickness(16),
                MinWidth = 260,
                MaxWidth = 420,
                Child = content,
            };

            popup = new Popup
            {
                Child = card,
                PlacementTarget = placementTarget,
                ShouldConstrainToRootBounds = true,
                StaysOpen = false,
                VerticalOffset = 8,
            };
            popup.SetBinding(
                Popup.IsOpenProperty,
                new Binding(nameof(BindablePopupViewModel.IsPopupOpen))
                {
                    Mode = BindingMode.TwoWay,
                });

            var canvas = new Canvas
            {
                Width = 220,
                Height = 80,
            };
            canvas.Children.Add(placementTarget);
            canvas.Children.Add(popup);
            return canvas;
        });

        var itemsControl = new ItemsControl
        {
            Width = 800,
            Height = 600,
            ItemTemplate = itemTemplate,
            ItemsSource = new[] { model },
        };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = itemsControl,
        };

        try
        {
            window.Measure(new Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));

            Assert.NotNull(popup);
            Assert.Same(model, popup!.DataContext);

            model.IsPopupOpen = true;
            window.Measure(new Size(800, 600));
            window.Arrange(new Rect(0, 0, 800, 600));

            Assert.True(popup.IsOpen);
            Assert.NotNull(card);
            Assert.NotNull(content);
            Assert.NotNull(actions);

            var popupRoot = GetPopupRoot(popup);
            var actionsBottom = actions!.VisualBounds.Y + actions.ActualHeight;

            Assert.True(
                popupRoot.Height + 0.01 >= card!.DesiredSize.Height,
                $"Popup host height {popupRoot.Height} did not include the templated card height {card.DesiredSize.Height}.");
            Assert.True(
                actionsBottom <= content!.ActualHeight + 0.01,
                $"Templated action row bottom {actionsBottom} overflowed content height {content.ActualHeight}.");
        }
        finally
        {
            if (popup is not null)
            {
                popup.IsOpen = false;
            }
        }
    }

    private static Button CreateActionButton(string content, ControlTemplate template)
    {
        return new Button
        {
            Content = content,
            Template = template,
            Height = 32,
            MinWidth = 80,
            Padding = new Thickness(12, 0),
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    private static PopupRoot GetPopupRoot(Popup popup)
    {
        var field = typeof(Popup).GetField("_popupRoot", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<PopupRoot>(field?.GetValue(popup));
    }

    private sealed record PopupViewModel(string Title, string Metadata);

    private sealed class BindablePopupViewModel : INotifyPropertyChanged
    {
        private bool _isPopupOpen;

        public BindablePopupViewModel(string title, string metadata)
        {
            Title = title;
            Metadata = metadata;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; }

        public string Metadata { get; }

        public bool IsPopupOpen
        {
            get => _isPopupOpen;
            set
            {
                if (_isPopupOpen == value)
                {
                    return;
                }

                _isPopupOpen = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsPopupOpen)));
            }
        }
    }
}
