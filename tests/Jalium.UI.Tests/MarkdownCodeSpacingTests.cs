using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

/// <summary>
/// 钉住围栏代码块里「token 之间的空白」不被吞掉。
///
/// <para>
/// 回归的形状：<see cref="MarkdownCodeTextPresenter"/> 逐 token 绘制，笔位置按 token 宽度累加。
/// 高亮器会把 token 之间的空白单独产出成 PlainText token（<c>RegexSyntaxHighlighter</c> 的 gap 补齐），
/// 而 <c>FormattedText.Width</c> 按排版惯例丢弃尾随空白 —— 纯空白 token 因此量得 0，
/// 笔不前进，两个关键字被贴在一起：<c>private const int</c> 显示成 <c>privateconstint</c>。
/// </para>
///
/// <para>
/// 测法是<b>差分</b>而不是绝对值：拿「关键字 空格 关键字」与「关键字关键字」比宽度。
/// 后者整体不匹配关键字规则、是单个 PlainText token，不受这个 bug 影响，正好当基准。
/// 差分不依赖具体字体的度量，换字体 / 换 DPI 都不会让测试变脆。
/// </para>
/// </summary>
[Collection("Application")]
public class MarkdownCodeSpacingTests
{
    private static void ResetApplicationState()
    {
        var currentField = typeof(Application).GetField("_current",
            BindingFlags.NonPublic | BindingFlags.Static);
        currentField?.SetValue(null, null);

        var resetMethod = typeof(ThemeManager).GetMethod("Reset",
            BindingFlags.NonPublic | BindingFlags.Static);
        resetMethod?.Invoke(null, null);
    }

    /// <summary>量一段代码在代码块呈现器里的自然宽度（无行号栏、无内边距，量到的就是文本本身）。</summary>
    private static double MeasureCodeWidth(string code)
    {
        var presenter = new MarkdownCodeTextPresenter
        {
            Code = code,
            CodeLanguage = "csharp",
            ShowLineNumbers = false,
            Padding = new Thickness(0),
            FontSize = 14,
            MonospaceFontFamily = new FontFamily("Consolas"),
        };

        var host = new StackPanel { Width = 2000, Height = 200 };
        host.Children.Add(presenter);
        host.Measure(new Size(2000, 200));
        host.Arrange(new Rect(0, 0, 2000, 200));

        return presenter.DesiredSize.Width;
    }

    [Fact]
    public void CodeText_SpaceBetweenKeywords_ShouldAdvancePen()
    {
        ResetApplicationState();
        _ = new Application();

        // "const const" 是 [const][空格][const] 三个 token；"constconst" 整体不匹配 \bconst\b，
        // 是一个 PlainText token。前者必须比后者宽出一个空格 —— 修复前两者一样宽。
        var spaced = MeasureCodeWidth("const const");
        var glued = MeasureCodeWidth("constconst");

        Assert.True(
            spaced > glued,
            $"token 之间的空格被吞了：'const const' 量得 {spaced:F2}，'constconst' 量得 {glued:F2}");
    }

    [Fact]
    public void CodeText_RepeatedSpaces_ShouldScaleLinearly()
    {
        ResetApplicationState();
        _ = new Application();

        var oneSpace = MeasureCodeWidth("const const");
        var threeSpaces = MeasureCodeWidth("const   const");
        var glued = MeasureCodeWidth("constconst");

        var singleGap = oneSpace - glued;
        var tripleGap = threeSpaces - glued;

        Assert.True(singleGap > 0, $"一个空格没有宽度：{singleGap:F2}");

        // 三个空格应当正好是一个空格的三倍（等宽字体下 ±1px 容差给光栅取整）。
        Assert.InRange(tripleGap, (singleGap * 3) - 1.0, (singleGap * 3) + 1.0);
    }

    [Fact]
    public void CodeText_LeadingIndent_ShouldSurviveHighlighting()
    {
        ResetApplicationState();
        _ = new Application();

        // 缩进是代码块里最容易被这个 bug 打烂的东西：整行缩进后宽度必须跟着涨。
        var flush = MeasureCodeWidth("return value;");
        var indented = MeasureCodeWidth("    return value;");

        Assert.True(
            indented > flush,
            $"行首缩进被吞了：缩进行 {indented:F2}，顶格行 {flush:F2}");
    }

    [Fact]
    public void CodeText_RealWorldDeclaration_ShouldNotGlueKeywords()
    {
        ResetApplicationState();
        _ = new Application();

        // 用户截图里那一行的前半截：三个关键字连着，两个空格一被吞就成了 "privateconstint"。
        // 对照组必须只差这两个空格 —— 字符数再差别的就不是干净的差分了。
        var real = MeasureCodeWidth("private const int");
        var glued = MeasureCodeWidth("privateconstint");
        var oneSpace = MeasureCodeWidth("const const") - MeasureCodeWidth("constconst");

        // 先钉住基准本身不是 0 —— 空白全被吞时 oneSpace 与 (real - glued) 会一起塌成 0，
        // 光比差值反而会「通过」。
        Assert.True(oneSpace > 0, $"空格基准量得 {oneSpace:F2}，说明空白已经被吞光");
        Assert.InRange(real - glued, (oneSpace * 2) - 1.0, (oneSpace * 2) + 1.0);
    }
}
