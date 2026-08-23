using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Jalium.UI.Gallery;

internal static partial class GalleryWindow
{
    /// <summary>
    /// Backdrop materials: every card draws the same colourful tile pattern and
    /// floats a rounded panel over it whose <see cref="UIElement.BackdropEffect"/>
    /// filters what lies behind. Mirrors the external Gallery's
    /// BackdropEffectsPage so the in-repo sample exercises every preset and
    /// every colour-pipeline parameter (grayscale / sepia / invert / hue).
    /// </summary>
    public static UIElement BuildEffectsSection() => Section(
        "Backdrop Effects",
        "Blur and material effects applied to the content behind an element (IBackdropEffect).",
        BackdropCard("BackdropBlurEffect (20px)", new BackdropBlurEffect(20f)),
        BackdropCard("AcrylicEffect (blue tint, alpha 200)",
            new AcrylicEffect(Color.FromArgb(200, 30, 30, 40), tintOpacity: 0.6f, blurRadius: 30f)),
        BackdropCard("MicaEffect", new MicaEffect()),
        BackdropCard("FrostedGlassEffect (15px, noise 0.04)",
            new FrostedGlassEffect(blurRadius: 15f, noiseIntensity: 0.04f, tintColor: Color.White, tintOpacity: 0.3f)),
        BackdropCard("ColorAdjustmentEffect - Grayscale", ColorAdjustmentEffect.CreateGrayscale(1f)),
        BackdropCard("ColorAdjustmentEffect - Sepia", ColorAdjustmentEffect.CreateSepia(0.8f)),
        BackdropCard("ColorAdjustmentEffect - Invert", ColorAdjustmentEffect.CreateInvert(1f)),
        BackdropCard("ColorAdjustmentEffect - Hue Rotate 90°", ColorAdjustmentEffect.CreateHueRotate(90f)),
        BackdropCard("Box kernel + Opacity 0.5", new BackdropBlurEffect(24f, BackdropBlurType.Box) { Opacity = 0.5f }));

    private static readonly Color[] s_tileRows =
    [
        Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xFF, 0x7F, 0x00), Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0x00, 0x00, 0xFF), Color.FromRgb(0x00, 0x82, 0x2B),
        Color.FromRgb(0x00, 0xD3, 0x46), Color.FromRgb(0x14, 0xFF, 0x62),
        Color.FromRgb(0x00, 0xCE, 0xD1), Color.FromRgb(0xFF, 0x63, 0x47), Color.FromRgb(0x32, 0xCD, 0x32),
        Color.FromRgb(0xFF, 0xD7, 0x00), Color.FromRgb(0x14, 0xDC, 0x57), Color.FromRgb(0x00, 0xBF, 0xFF),
        Color.FromRgb(0x69, 0xFF, 0x9B), Color.FromRgb(0x7F, 0xFF, 0x00),
        Color.FromRgb(0x3F, 0x51, 0xB5), Color.FromRgb(0x21, 0x96, 0xF3), Color.FromRgb(0x03, 0xA9, 0xF4),
        Color.FromRgb(0x00, 0xBC, 0xD4), Color.FromRgb(0x00, 0x96, 0x88), Color.FromRgb(0xF4, 0x43, 0x36),
        Color.FromRgb(0x8B, 0xC3, 0x4A), Color.FromRgb(0x79, 0x55, 0x48),
    ];

    private static UIElement BackdropCard(string title, IBackdropEffect effect) =>
        Card(title, MakeBackdropDemo(effect), width: 360);

    /// <summary>
    /// A 3×8 grid of saturated tiles with a translucent panel floating over the
    /// middle of it. The panel is a plain <see cref="Border"/>: backdrop effects
    /// are rendered by Border.OnRender before its own background, so the label
    /// text stays crisp on top of the filtered backdrop.
    /// </summary>
    private static UIElement MakeBackdropDemo(IBackdropEffect effect)
    {
        const double tile = 36;
        var tiles = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (int row = 0; row < 3; row++)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            for (int col = 0; col < 8; col++)
            {
                line.Children.Add(new Border
                {
                    Width = tile,
                    Height = tile,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(s_tileRows[row * 8 + col]),
                });
            }
            tiles.Children.Add(line);
        }

        var panel = new Border
        {
            Margin = new Thickness(22, 16, 22, 16),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BackdropEffect = effect,
            Child = new TextBlock
            {
                Text = effect.GetType().Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var host = new Grid { Height = 150 };
        host.Children.Add(tiles);
        host.Children.Add(panel);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = host,
        };
    }
}
