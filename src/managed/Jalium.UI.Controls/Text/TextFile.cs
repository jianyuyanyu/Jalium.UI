using System.IO;
using System.Text;

namespace Jalium.UI.Controls;

/// <summary>
/// Encoding-aware text-file I/O, shared by the text controls' <c>LoadFromFile</c>
/// and <c>SaveToFile</c> methods so the encoding policy lives in one place.
/// </summary>
/// <remarks>
/// Reading auto-detects a byte-order mark; an explicit <see cref="Encoding"/> is
/// the fallback used only when the file carries no BOM (pass, for example,
/// <c>Encoding.GetEncoding(936)</c> for a BOM-less GBK file). Legacy / non-Unicode
/// code pages are resolvable because <see cref="EncodingSupport"/> registers
/// <see cref="CodePagesEncodingProvider"/> at module load.
/// </remarks>
internal static class TextFile
{
    /// <summary>
    /// Reads <paramref name="path"/> as text. A byte-order mark, if present,
    /// decides the encoding; otherwise <paramref name="encoding"/> is used,
    /// defaulting to UTF-8 when it is <see langword="null"/>.
    /// </summary>
    public static string ReadAllText(string path, Encoding? encoding)
        => encoding is null
            ? File.ReadAllText(path)
            : File.ReadAllText(path, encoding);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> using
    /// <paramref name="encoding"/>, defaulting to UTF-8 when it is
    /// <see langword="null"/>.
    /// </summary>
    public static void WriteAllText(string path, string text, Encoding? encoding)
        => File.WriteAllText(path, text, encoding ?? Encoding.UTF8);
}
