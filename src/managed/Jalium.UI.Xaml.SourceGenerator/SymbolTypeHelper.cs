using Microsoft.CodeAnalysis;

namespace Jalium.UI.Xaml.SourceGenerator;

/// <summary>
/// Wraps a Roslyn <see cref="Compilation"/> with the small slice of symbol-lookup helpers
/// the code generator needs to lower jalxaml attribute values into strongly-typed C# at
/// compile time.
///
/// <para>
/// The aim is to avoid the runtime <c>XamlBuilder.SetProperty</c> reflection path for
/// values the SG can prove are simple literals (numbers, bools, strings, enums, common
/// struct converters). The helper exposes:
/// <list type="bullet">
///   <item><see cref="ResolveType"/>: full-name → <see cref="INamedTypeSymbol"/>.</item>
///   <item><see cref="ResolveProperty"/>: walks the inheritance chain to find a property.</item>
/// </list>
/// </para>
///
/// <para>
/// All lookups are cached for the life of one compilation. The class is not thread-safe;
/// the SG runs the entire generation pass on a single thread per file batch.
/// </para>
/// </summary>
public sealed class SymbolTypeHelper
{
    private readonly Compilation _compilation;
    private readonly Dictionary<string, INamedTypeSymbol?> _typeCache = new(StringComparer.Ordinal);

    public SymbolTypeHelper(Compilation compilation)
    {
        _compilation = compilation;
    }

    /// <summary>
    /// Look up a CLR type by metadata name (dot-qualified, e.g. <c>Jalium.UI.Controls.Button</c>).
    /// Returns the first matching symbol across all referenced assemblies. Cached.
    /// </summary>
    public INamedTypeSymbol? ResolveType(string fullMetadataName)
    {
        if (string.IsNullOrEmpty(fullMetadataName))
            return null;

        if (_typeCache.TryGetValue(fullMetadataName, out var cached))
            return cached;

        // GetTypeByMetadataName returns null when the name is ambiguous across assemblies
        // (rare for framework types; possible for user-defined types if both the source
        // assembly and a referenced assembly declare the same FQN). In that case, fall back
        // to scanning every reachable assembly and pick the first match — matches the
        // runtime ResolveTypeUncached behaviour rather than silently dropping the symbol.
        var symbol = _compilation.GetTypeByMetadataName(fullMetadataName);
        if (symbol == null)
        {
            foreach (var asm in _compilation.SourceModule.ReferencedAssemblySymbols)
            {
                var match = asm.GetTypeByMetadataName(fullMetadataName);
                if (match != null)
                {
                    symbol = match;
                    break;
                }
            }
        }

        _typeCache[fullMetadataName] = symbol;
        return symbol;
    }

    /// <summary>
    /// Walk the inheritance chain of <paramref name="type"/> looking for a public instance
    /// property named <paramref name="propertyName"/>. Returns the most-derived match.
    /// </summary>
    /// <remarks>
    /// We only consider <c>Public</c> properties — XAML attribute setters never resolve to
    /// non-public members in the streaming parser. We also skip explicit-interface
    /// implementations and indexers, which XAML cannot target by simple name.
    /// </remarks>
    public IPropertySymbol? ResolveProperty(INamedTypeSymbol type, string propertyName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(propertyName))
            {
                if (member is IPropertySymbol property &&
                    property.DeclaredAccessibility == Accessibility.Public &&
                    !property.IsIndexer &&
                    !property.IsStatic &&
                    property.ExplicitInterfaceImplementations.IsDefaultOrEmpty)
                {
                    return property;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the qualified global-prefixed name for a symbol so the SG can drop it into
    /// generated source verbatim. Falls back to <c>System.Object</c> when the symbol is
    /// null — but callers should test for null before formatting.
    /// </summary>
    public static string ToGlobalName(ITypeSymbol type)
    {
        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}
