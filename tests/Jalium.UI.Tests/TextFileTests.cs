using System;
using System.IO;
using System.Text;
using Jalium.UI.Controls;

namespace Jalium.UI.Tests;

/// <summary>
/// Verifies the encoding-aware file I/O (<c>LoadFromFile</c> / <c>SaveToFile</c>)
/// added to the text controls.
/// </summary>
public class TextFileTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "jalium_textfile_test_" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void TextBox_SaveThenLoad_RoundTripsUtf8WithCjkAndZwjEmoji()
    {
        string content = "Hello 中文 " + EmojiTestStrings.Family + " end";
        new TextBox { Text = content }.SaveToFile(_path);

        var loaded = new TextBox();
        loaded.LoadFromFile(_path);

        Assert.Equal(content, loaded.Text);
    }

    [Fact]
    public void TextBox_SaveThenLoad_RoundTripsGbkCodePage()
    {
        Encoding gbk = Encoding.GetEncoding(936);
        string content = "中文 GBK 编码测试";
        new TextBox { Text = content }.SaveToFile(_path, gbk);

        // The file is genuine GBK — no UTF-8 multi-byte sequences.
        byte[] raw = File.ReadAllBytes(_path);
        Assert.Equal(gbk.GetBytes(content).Length, raw.Length);

        var loaded = new TextBox();
        loaded.LoadFromFile(_path, gbk);

        Assert.Equal(content, loaded.Text);
    }

    [Fact]
    public void LoadFromFile_LetsTheByteOrderMarkOverrideTheRequestedEncoding()
    {
        // Written as UTF-8 *with* a BOM.
        string content = "中文 with BOM";
        File.WriteAllText(_path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        // Loaded while requesting GBK — the BOM must win, so the text is intact.
        var loaded = new TextBox();
        loaded.LoadFromFile(_path, Encoding.GetEncoding(936));

        Assert.Equal(content, loaded.Text);
    }

    [Fact]
    public void EditControl_SaveThenLoad_RoundTrips()
    {
        string content = "line one\nline two 中文 " + EmojiTestStrings.WaveDarkSkin;
        var saver = new EditControl();
        saver.LoadText(content);
        saver.SaveToFile(_path);

        var loaded = new EditControl();
        loaded.LoadFromFile(_path);

        Assert.Equal(content, loaded.Text);
    }
}
