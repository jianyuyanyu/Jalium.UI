using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Data;
using Jalium.UI.Markup;
using Jalium.UI.Media;
using ShapePath = Jalium.UI.Shapes.Path;

namespace Jalium.UI.Tests;

/// <summary>
/// Regression coverage for TemplateBinding string → target-type conversion.
///
/// A control that models an icon as a path string (<c>IconGeometry</c> registered as
/// <see cref="string"/>) and feeds it to a <see cref="ShapePath.Data"/> target
/// (<c>Data="{TemplateBinding IconGeometry}"</c>) is ordinary WPF-style markup — the very same string
/// form the parser accepts for a literal attribute (<c>Data="M0,0 L10,10"</c>, which routes through
/// <c>XamlBuilder.SetProperty</c> → <c>TypeConverterRegistry</c>).
///
/// Before the fix, the TemplateBinding transfer performed no conversion at all: the string failed
/// <see cref="DependencyProperty.IsValidType"/> against the Geometry-typed target, the value was
/// dropped with only a <see cref="System.Diagnostics.Trace"/> line, and <c>Path.Data</c> stayed null.
/// A Path with no geometry draws nothing — the failure surfaced only as a blank icon, with no
/// exception and nothing in the UI to point at the cause. Both transfer paths
/// (<c>TemplateBindingExpression</c> for code templates and <c>DeferredTemplateBindingExpression</c>
/// for <c>{TemplateBinding}</c> markup) now route the value through
/// <c>TemplateBindingValueCoercion</c>.
/// </summary>
public class TemplateBindingStringConversionTests
{
    private const string IconPath = "M12,5 L12,19 M5,12 L19,12";

    // --- The markup path: DeferredTemplateBindingExpression ({TemplateBinding} in jalxaml) ---

    [Fact]
    public void DeferredTemplateBinding_StringSourceIntoGeometryTarget_ConvertsInsteadOfDropping()
    {
        EnsureXamlConvertersRegistered();

        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = IconPath };
        control.Template = BuildPathTemplate(p =>
            p.SetBinding(ShapePath.DataProperty, new DeferredTemplateBinding(nameof(IconProbeControl.IconGeometry))));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        Assert.NotNull(path.Data);
        Assert.Equal(
            BaseValueSource.ParentTemplate,
            DependencyPropertyHelper.GetValueSource(path, ShapePath.DataProperty).BaseValueSource);
    }

    [Fact]
    public void DeferredTemplateBinding_ConvertedGeometry_ProducesRenderableFigures()
    {
        EnsureXamlConvertersRegistered();

        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = IconPath };
        control.Template = BuildPathTemplate(p =>
            p.SetBinding(ShapePath.DataProperty, new DeferredTemplateBinding(nameof(IconProbeControl.IconGeometry))));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        // Non-null Data is not enough to prove the icon draws: Path.OnRender bails when the
        // rendered geometry has no figures, which is exactly what a blank icon looks like.
        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        var rendered = Assert.IsAssignableFrom<Geometry>(path.RenderedGeometry);
        Assert.False(rendered.Bounds.IsEmpty);
    }

    [Fact]
    public void DeferredTemplateBinding_SourceStringChangesLater_RetransfersConvertedValue()
    {
        EnsureXamlConvertersRegistered();

        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = "M0,0 L4,0" };
        control.Template = BuildPathTemplate(p =>
            p.SetBinding(ShapePath.DataProperty, new DeferredTemplateBinding(nameof(IconProbeControl.IconGeometry))));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        var firstWidth = path.Data!.Bounds.Width;

        // Conversion must live on the transfer path, not only on first activation.
        control.IconGeometry = "M0,0 L40,0";
        Assert.NotNull(path.Data);
        Assert.True(path.Data!.Bounds.Width > firstWidth);
    }

    // --- The code path: TemplateBindingExpression (SetTemplateBinding(DP, DP)) ---

    [Fact]
    public void TemplateBinding_StringSourceIntoGeometryTarget_ConvertsInsteadOfDropping()
    {
        EnsureXamlConvertersRegistered();

        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = IconPath };
        control.Template = BuildPathTemplate(p =>
            p.SetTemplateBinding(ShapePath.DataProperty, IconProbeControl.IconGeometryProperty));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        Assert.NotNull(path.Data);
        Assert.Equal(
            BaseValueSource.ParentTemplate,
            DependencyPropertyHelper.GetValueSource(path, ShapePath.DataProperty).BaseValueSource);
    }

    [Fact]
    public void TemplateBinding_StringSourceIntoBrushTarget_ConvertsInsteadOfDropping()
    {
        EnsureXamlConvertersRegistered();

        // The gate is not Geometry-specific — it is the whole registered converter pipeline.
        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = "#FF3366" };
        control.Template = BuildBorderTemplate(b =>
            b.SetTemplateBinding(Border.BackgroundProperty, IconProbeControl.IconGeometryProperty));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var border = Assert.IsType<Border>(control.GetVisualChild(0));
        var brush = Assert.IsType<SolidColorBrush>(border.Background);
        Assert.Equal(Color.FromRgb(0xFF, 0x33, 0x66), brush.Color);
    }

    // --- The fail-closed half: what must still be dropped ---

    [Fact]
    public void TemplateBinding_UnconvertibleString_StillFallsBackToDefault()
    {
        EnsureXamlConvertersRegistered();

        // "not a thickness" has three whitespace-separated parts, so ThicknessConverter throws
        // FormatException. A converter that fails must not escape and must not pin garbage.
        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = "not a thickness" };
        control.Template = BuildBorderTemplate(b =>
            b.SetTemplateBinding(Border.BorderThicknessProperty, IconProbeControl.IconGeometryProperty));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var border = Assert.IsType<Border>(control.GetVisualChild(0));
        Assert.Equal(new Thickness(0), border.BorderThickness);
        Assert.Equal(
            BaseValueSource.Default,
            DependencyPropertyHelper.GetValueSource(border, Border.BorderThicknessProperty).BaseValueSource);
    }

    [Fact]
    public void TemplateBinding_MalformedGeometryString_StillFallsBackToDefault()
    {
        EnsureXamlConvertersRegistered();

        // GeometryTypeConverter signals failure by returning null rather than throwing — the other
        // of the two failure shapes the gate has to absorb.
        var control = new IconProbeControl { Width = 42, Height = 42, IconGeometry = "Q@@ not a path" };
        control.Template = BuildPathTemplate(p =>
            p.SetTemplateBinding(ShapePath.DataProperty, IconProbeControl.IconGeometryProperty));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        Assert.Null(path.Data);
        Assert.Equal(
            BaseValueSource.Default,
            DependencyPropertyHelper.GetValueSource(path, ShapePath.DataProperty).BaseValueSource);
    }

    [Fact]
    public void TemplateBinding_NonStringTypeMismatch_StillFallsBackToDefault()
    {
        EnsureXamlConvertersRegistered();

        // Only a string is offered to the converter pipeline. A structurally wrong non-string source
        // stays fail-closed rather than being guessed at.
        var control = new IconProbeControl { Width = 42, Height = 42, SourceCount = 7 };
        control.Template = BuildPathTemplate(p =>
            p.SetTemplateBinding(ShapePath.DataProperty, IconProbeControl.SourceCountProperty));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        Assert.Null(path.Data);
        Assert.Equal(
            BaseValueSource.Default,
            DependencyPropertyHelper.GetValueSource(path, ShapePath.DataProperty).BaseValueSource);
    }

    [Fact]
    public void TemplateBinding_MatchingTypes_TransferUnchanged_NoConversionInvolved()
    {
        EnsureXamlConvertersRegistered();

        // The conversion branch must never touch a binding that already transfers: same instance in,
        // same instance out.
        var geometry = Geometry.Parse(IconPath);
        var control = new IconProbeControl { Width = 42, Height = 42, SourceGeometry = geometry };
        control.Template = BuildPathTemplate(p =>
            p.SetTemplateBinding(ShapePath.DataProperty, IconProbeControl.SourceGeometryProperty));

        control.Measure(new Size(42, 42));
        control.Arrange(new Rect(0, 0, 42, 42));

        var path = Assert.IsType<ShapePath>(control.GetVisualChild(0));
        Assert.Same(geometry, path.Data);
    }

    /// <summary>
    /// The converter pipeline reaches Core through <c>Style.StringValueConverter</c>, which
    /// Jalium.UI.Xaml installs from a module initializer. Touching a type from that assembly
    /// guarantees it is loaded — in a real app any jalxaml load does this long before templates apply.
    /// </summary>
    private static void EnsureXamlConvertersRegistered()
    {
        _ = new DeferredTemplateBinding("__ensure_xaml_loaded");
        Assert.NotNull(Style.StringValueConverter);
    }

    private static ControlTemplate BuildPathTemplate(Action<ShapePath> configure)
    {
        var template = new ControlTemplate(typeof(IconProbeControl));
        template.SetVisualTree(() =>
        {
            var path = new ShapePath { Width = 22, Height = 22, Stretch = Stretch.Uniform };
            configure(path);
            return path;
        });
        return template;
    }

    private static ControlTemplate BuildBorderTemplate(Action<Border> configure)
    {
        var template = new ControlTemplate(typeof(IconProbeControl));
        template.SetVisualTree(() =>
        {
            var border = new Border();
            configure(border);
            return border;
        });
        return template;
    }

    /// <summary>
    /// Mirrors the shape of a real consumer control (Jalium.One's <c>WelcomeActionCard</c>): the icon
    /// path is modelled as a string and handed to a Geometry-typed template part.
    /// </summary>
    private sealed class IconProbeControl : Control
    {
        public static readonly DependencyProperty IconGeometryProperty =
            DependencyProperty.Register(
                nameof(IconGeometry),
                typeof(string),
                typeof(IconProbeControl),
                new PropertyMetadata(string.Empty));

        public string IconGeometry
        {
            get => (string)GetValue(IconGeometryProperty)!;
            set => SetValue(IconGeometryProperty, value);
        }

        public static readonly DependencyProperty SourceGeometryProperty =
            DependencyProperty.Register(
                nameof(SourceGeometry),
                typeof(Geometry),
                typeof(IconProbeControl),
                new PropertyMetadata(null));

        public Geometry? SourceGeometry
        {
            get => (Geometry?)GetValue(SourceGeometryProperty);
            set => SetValue(SourceGeometryProperty, value);
        }

        public static readonly DependencyProperty SourceCountProperty =
            DependencyProperty.Register(
                nameof(SourceCount),
                typeof(int),
                typeof(IconProbeControl),
                new PropertyMetadata(0));

        public int SourceCount
        {
            get => (int)GetValue(SourceCountProperty)!;
            set => SetValue(SourceCountProperty, value);
        }
    }
}
