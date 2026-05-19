using System.Runtime.CompilerServices;
using Jalium.UI.Markup;

[assembly: InternalsVisibleTo("Jalium.UI.Xaml")]
[assembly: InternalsVisibleTo("Jalium.UI.Tests")]
// Platform integration packages implement INotificationBackend directly and
// need access to internal handle/helper members in Notifications/.
[assembly: InternalsVisibleTo("Jalium.UI.Desktop")]
[assembly: InternalsVisibleTo("Jalium.UI.Android")]

// Expose CLR namespaces defined in Jalium.UI.Controls under the canonical JALXAML namespace.
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Annotations")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Automation")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Charts")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.DevTools")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Helpers")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Ink")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Navigation")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Primitives")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Ribbon")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Shapes")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Shell")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.TextEffects")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.TextEffects.Effects")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Controls.Virtualization")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Documents")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Documents.DocumentStructures")]
[assembly: XmlnsDefinition(JalxamlNamespaces.Presentation, "Jalium.UI.Hosting")]

// Redirect legacy Jalium URIs (and WPF's presentation URI) to the canonical namespace so existing
// documents continue to parse without modification.
[assembly: XmlnsCompatibleWith(JalxamlNamespaces.LegacyJaliumUi, JalxamlNamespaces.Presentation)]
[assembly: XmlnsCompatibleWith(JalxamlNamespaces.LegacyJaliumDev, JalxamlNamespaces.Presentation)]
[assembly: XmlnsCompatibleWith(JalxamlNamespaces.WpfPresentation, JalxamlNamespaces.Presentation)]

[assembly: XmlnsPrefix(JalxamlNamespaces.Presentation, "ui")]
[assembly: XmlnsPrefix(JalxamlNamespaces.XamlMarkup, "x")]
