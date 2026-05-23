using System.Text;

namespace Jalium.UI.Tests;

/// <summary>
/// Emoji and combining-sequence strings for the grapheme-cluster tests, built
/// from explicit Unicode code points so the test source carries no invisible
/// joiners and no astral characters that could be mangled by an editor or an
/// encoding round-trip. Each value below is exactly one extended grapheme
/// cluster (one user-perceived character) yet spans several UTF-16 code units.
/// </summary>
internal static class EmojiTestStrings
{
    /// <summary>Concatenates the given Unicode code points into a string.</summary>
    public static string FromCodePoints(params int[] codePoints)
    {
        var builder = new StringBuilder();
        foreach (int codePoint in codePoints)
        {
            builder.Append(char.ConvertFromUtf32(codePoint));
        }
        return builder.ToString();
    }

    /// <summary>U+1F680 rocket — one surrogate pair (2 code units).</summary>
    public static readonly string Rocket = FromCodePoints(0x1F680);

    /// <summary>U+1F44B wave + U+1F3FF dark-skin-tone modifier (4 code units).</summary>
    public static readonly string WaveDarkSkin = FromCodePoints(0x1F44B, 0x1F3FF);

    /// <summary>
    /// U+1F468 ZWJ U+1F469 ZWJ U+1F467 ZWJ U+1F466 — family (11 code units).
    /// </summary>
    public static readonly string Family =
        FromCodePoints(0x1F468, 0x200D, 0x1F469, 0x200D, 0x1F467, 0x200D, 0x1F466);

    /// <summary>U+1F1E8 U+1F1F3 — regional-indicator flag, CN (4 code units).</summary>
    public static readonly string FlagCN = FromCodePoints(0x1F1E8, 0x1F1F3);

    /// <summary>digit one + U+FE0F + U+20E3 — enclosing keycap (3 code units).</summary>
    public static readonly string Keycap = FromCodePoints(0x0031, 0xFE0F, 0x20E3);

    /// <summary>U+2194 left-right arrow + U+FE0F emoji-presentation selector.</summary>
    public static readonly string ArrowVs16 = FromCodePoints(0x2194, 0xFE0F);

    /// <summary>
    /// U+1F642 ZWJ U+2194 U+FE0F — Emoji 15.1 "head shaking horizontally"
    /// (5 code units).
    /// </summary>
    public static readonly string HeadShaking = FromCodePoints(0x1F642, 0x200D, 0x2194, 0xFE0F);

    /// <summary>letter e + U+0301 combining acute accent (2 code units).</summary>
    public static readonly string ECombiningAcute = FromCodePoints(0x0065, 0x0301);
}
