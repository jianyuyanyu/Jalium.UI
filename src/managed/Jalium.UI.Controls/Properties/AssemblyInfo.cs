using System.Runtime.CompilerServices;
using Jalium.UI.Markup;

[assembly: InternalsVisibleTo("Jalium.UI.Xaml")]
[assembly: InternalsVisibleTo("Jalium.UI.Tests")]
// Platform integration packages implement INotificationBackend directly and
// need access to internal handle/helper members in Notifications/.
[assembly: InternalsVisibleTo("Jalium.UI.Desktop")]
[assembly: InternalsVisibleTo("Jalium.UI.Android")]

// Expose CLR namespaces defined in Jalium.UI.Controls under the canonical JALXAML namespace.
//
// This list is load-bearing, not documentation. A framework CLR namespace that is NOT
// listed here is effectively invisible to jalxaml:
//
//   XamlReader.ResolveTypeUncached tries, in order —
//     1. clr-namespace: (only when the author wrote an explicit xmlns)
//     2. these XmlnsDefinition mappings
//     3. XamlTypeRegistry (a HAND-MAINTAINED Register<T>() list)
//     4. _fallbackClrNamespaces convention scan — but ResolveTypeInNamespace only scans
//        preferredAssembly + SourceAssembly, i.e. the *app's* assembly, never the
//        framework's. So step 4 cannot rescue a framework type.
//
// …which leaves exactly two ways for a framework type to be reachable: listed here, or
// hand-added to XamlTypeRegistry. Miss both and the failure is ugly and asymmetric:
//   · as an element attribute  -> XamlParseException "Cannot resolve attached property
//                                 owner type: X" at startup
//   · as a Style Setter        -> SILENTLY IGNORED (the compiled SourceGenerator path
//                                 never runs PostProcessSetter, which is what validates
//                                 an unresolved Setter.Property in the streaming path)
//
// An audit of every RegisterAttached owner type in the framework found 20 of 55
// unreachable this way — TextOptions, RenderOptions, KeyboardNavigation, FocusManager,
// AutomationProperties, Interaction and friends. The entries below close that gap.
// When you add a public CLR namespace that can appear in markup, add it here too.
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Media", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Media.Media3D", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Input", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Input.TextInput", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Automation", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Interactivity", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Navigation", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Data", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Markup", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Media.Animation", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Annotations", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Annotations.Storage", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Automation", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Charts", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.DevTools", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Helpers", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Ink", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Ink", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Input.StylusPlugIns", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Navigation", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Primitives", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Ribbon", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Shapes", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Shell", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Shell", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.TextEffects", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.TextEffects.Effects", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Virtualization", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Documents", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Documents.DocumentStructures", AssemblyName = "Jalium.UI.Managed")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Hosting", AssemblyName = "Jalium.UI.Managed")]

// Redirect legacy Jalium URIs (and WPF's presentation URI) to the canonical namespace so existing
// documents continue to parse without modification.
[assembly: XmlnsCompatibleWith(JalxamlNamespaces.LegacyJaliumUi, JalxamlNamespaces.Presentation)]
[assembly: XmlnsCompatibleWith(JalxamlNamespaces.LegacyJaliumDev, JalxamlNamespaces.Presentation)]
[assembly: XmlnsCompatibleWith(JalxamlNamespaces.WpfPresentation, JalxamlNamespaces.Presentation)]

[assembly: XmlnsPrefix(JalxamlNamespaces.Presentation, "ui")]
[assembly: XmlnsPrefix(JalxamlNamespaces.XamlMarkup, "x")]
