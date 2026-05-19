namespace Jalium.UI.Xaml.SourceGenerator;

/// <summary>
/// Curated list of named colors / brushes recognised by the framework's
/// <c>Jalium.UI.Media.Colors</c> and <c>Jalium.UI.Media.Brushes</c> static helpers.
/// Used by <see cref="LiteralValueConverter"/> to recognise <c>Background="Black"</c>
/// style attribute values and emit <c>global::Jalium.UI.Media.Brushes.Black</c> directly.
///
/// <para>
/// The list is hand-mirrored from <c>src/managed/Jalium.UI.Core/Media/Colors.cs</c>. Both
/// files declare the same set of names so the SG can route a string-literal attribute
/// to either Colors.X or Brushes.X without needing a cross-assembly reflection lookup
/// at compile time.
/// </para>
/// </summary>
internal static class NamedColorIndex
{
    private static readonly string[] _names = new[]
    {
        "AliceBlue", "AntiqueWhite", "Aqua", "Aquamarine", "Azure", "Beige", "Bisque",
        "Black", "BlanchedAlmond", "Blue", "BlueViolet", "Brown", "BurlyWood",
        "CadetBlue", "Chartreuse", "Chocolate", "Coral", "CornflowerBlue", "Cornsilk",
        "Crimson", "Cyan", "DarkBlue", "DarkCyan", "DarkGoldenrod", "DarkGray",
        "DarkGreen", "DarkKhaki", "DarkMagenta", "DarkOliveGreen", "DarkOrange",
        "DarkOrchid", "DarkRed", "DarkSalmon", "DarkSeaGreen", "DarkSlateBlue",
        "DarkSlateGray", "DarkTurquoise", "DarkViolet", "DeepPink", "DeepSkyBlue",
        "DimGray", "DodgerBlue", "Firebrick", "FloralWhite", "ForestGreen", "Fuchsia",
        "Gainsboro", "GhostWhite", "Gold", "Goldenrod", "Gray", "Green", "GreenYellow",
        "Honeydew", "HotPink", "IndianRed", "Indigo", "Ivory", "Khaki", "Lavender",
        "LavenderBlush", "LawnGreen", "LemonChiffon", "LightBlue", "LightCoral",
        "LightCyan", "LightGoldenrodYellow", "LightGray", "LightGreen", "LightPink",
        "LightSalmon", "LightSeaGreen", "LightSkyBlue", "LightSlateGray",
        "LightSteelBlue", "LightYellow", "Lime", "LimeGreen", "Linen", "Magenta",
        "Maroon", "MediumAquamarine", "MediumBlue", "MediumOrchid", "MediumPurple",
        "MediumSeaGreen", "MediumSlateBlue", "MediumSpringGreen", "MediumTurquoise",
        "MediumVioletRed", "MidnightBlue", "MintCream", "MistyRose", "Moccasin",
        "NavajoWhite", "Navy", "OldLace", "Olive", "OliveDrab", "Orange", "OrangeRed",
        "Orchid", "PaleGoldenrod", "PaleGreen", "PaleTurquoise", "PaleVioletRed",
        "PapayaWhip", "PeachPuff", "Peru", "Pink", "Plum", "PowderBlue", "Purple",
        "Red", "RosyBrown", "RoyalBlue", "SaddleBrown", "Salmon", "SandyBrown",
        "SeaGreen", "SeaShell", "Sienna", "Silver", "SkyBlue", "SlateBlue", "SlateGray",
        "Snow", "SpringGreen", "SteelBlue", "Tan", "Teal", "Thistle", "Tomato",
        "Transparent", "Turquoise", "Violet", "Wheat", "White", "WhiteSmoke", "Yellow",
        "YellowGreen",
    };

    private static readonly Dictionary<string, string> _byInsensitiveName = BuildIndex();

    private static Dictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _names)
        {
            map[name] = name;
        }
        return map;
    }

    /// <summary>True when <paramref name="name"/> matches a curated named-color (case-insensitive).</summary>
    public static bool Contains(string name) => _byInsensitiveName.ContainsKey(name);

    /// <summary>
    /// Returns the canonical PascalCase form of <paramref name="name"/> (e.g. <c>"transparent"</c>
    /// → <c>"Transparent"</c>). Null when the name is not in the curated list.
    /// </summary>
    public static string? GetCanonical(string name)
    {
        return _byInsensitiveName.TryGetValue(name, out var canonical) ? canonical : null;
    }
}
