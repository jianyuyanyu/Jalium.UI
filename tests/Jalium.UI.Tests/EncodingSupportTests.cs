using System.Text;
using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Verifies that the <c>Jalium.UI.Controls</c> module initializer has registered
/// <see cref="CodePagesEncodingProvider"/>, so legacy / non-Unicode encodings —
/// which the Terminal and the QR encoder rely on — are resolvable.
/// </summary>
public class EncodingSupportTests
{
    [Theory]
    [InlineData(936)]   // GBK — Simplified Chinese
    [InlineData(932)]   // Shift-JIS — Japanese
    [InlineData(950)]   // Big5 — Traditional Chinese
    [InlineData(949)]   // EUC-KR — Korean
    public void LegacyCodePage_IsResolvable(int codePage)
    {
        // Touch a Jalium.UI.Controls type so the module — and the
        // [ModuleInitializer] that registers the code-pages provider — has loaded.
        _ = GraphemeClusters.NextBoundary("a", 0);

        // Without the provider this throws NotSupportedException.
        Encoding encoding = Encoding.GetEncoding(codePage);

        Assert.Equal(codePage, encoding.CodePage);
    }

    [Fact]
    public void Gbk_RoundTripsSimplifiedChinese()
    {
        _ = GraphemeClusters.NextBoundary("a", 0);

        Encoding gbk = Encoding.GetEncoding(936);
        string original = "中文"; // CJK characters U+4E2D U+6587

        byte[] bytes = gbk.GetBytes(original);
        string roundTripped = gbk.GetString(bytes);

        Assert.Equal(original, roundTripped);
        Assert.Equal(4, bytes.Length); // each GBK han character encodes to 2 bytes
    }
}
