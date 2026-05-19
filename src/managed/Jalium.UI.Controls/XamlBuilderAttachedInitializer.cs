using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Jalium.UI;
using Jalium.UI.Markup;

namespace Jalium.UI.Controls;

/// <summary>
/// Registers strongly-typed attached-property fast paths on <see cref="XamlBuilder"/>.
///
/// <para>
/// <see cref="XamlBuilder"/> lives in <c>Jalium.UI.Core</c> so that the framework's own
/// theme dictionaries can be lowered without introducing a circular reference between
/// <c>Jalium.UI.Controls</c> and <c>Jalium.UI.Xaml</c>. Core cannot reference
/// <c>Grid</c> / <c>Canvas</c> / <c>DockPanel</c> directly, so the actual setter calls
/// are wired up here, in Controls, via a <c>[ModuleInitializer]</c>.
/// </para>
///
/// <para>
/// Any caller that constructs UI through <see cref="XamlBuilder"/> attached fast paths
/// before <c>Jalium.UI.Controls</c> is loaded would observe an
/// <see cref="InvalidOperationException"/>. In practice that never happens — the SG
/// emits these calls from generated <c>InitializeComponent</c>/<c>Build</c> methods on
/// types defined in Controls or its consumers, so the assembly is always loaded by the
/// time the first call lands.
/// </para>
/// </summary>
internal static class XamlBuilderAttachedInitializer
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Required to wire framework-specific attached-property fast paths into the cross-assembly XamlBuilder.")]
    public static void Register()
    {
        XamlBuilder.SetGridRowImpl = static (e, r) => Grid.SetRow(e, r);
        XamlBuilder.SetGridColumnImpl = static (e, c) => Grid.SetColumn(e, c);
        XamlBuilder.SetGridRowSpanImpl = static (e, s) => Grid.SetRowSpan(e, s);
        XamlBuilder.SetGridColumnSpanImpl = static (e, s) => Grid.SetColumnSpan(e, s);

        XamlBuilder.SetCanvasLeftImpl = static (e, v) => Canvas.SetLeft(e, v);
        XamlBuilder.SetCanvasTopImpl = static (e, v) => Canvas.SetTop(e, v);
        XamlBuilder.SetCanvasRightImpl = static (e, v) => Canvas.SetRight(e, v);
        XamlBuilder.SetCanvasBottomImpl = static (e, v) => Canvas.SetBottom(e, v);

        XamlBuilder.SetPanelZIndexImpl = static (e, v) => Panel.SetZIndex(e, v);

        XamlBuilder.SetDockPanelDockImpl = static (e, dockEnumValue) =>
        {
            // The SG forwards the integer representation of the Dock enum so the Core
            // delegate signature stays free of a Controls-only type. Cast back here.
            DockPanel.SetDock(e, (Dock)dockEnumValue);
        };
    }
}
