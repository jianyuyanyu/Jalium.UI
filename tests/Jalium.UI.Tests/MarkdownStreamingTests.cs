using System.Reflection;
using System.Text;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Themes;

namespace Jalium.UI.Tests;

/// <summary>
/// 流式追加（AI 逐字输出那种用法）的闸门。
///
/// <para>
/// 这里同时钉两件事：<b>快没快</b>用计数器证明（重建了几个元素、排了几次全量版），
/// <b>对不对</b>用「增量结果必须与全量结果逐字节相同」证明——增量排版最容易出的错就是
/// 悄悄错一行、错几个像素，而这种错误肉眼和截图都抓不住，只有签名比对抓得住。
/// </para>
/// </summary>
[Collection("Application")]
public class MarkdownStreamingTests
{
    private static readonly MarkdownTextStyle PlainStyle = new(Bold: false, Italic: false, Code: false, LinkUri: null);

    /// <summary>离屏手动驱动布局时统一使用的宽度约束。</summary>
    private const double LayoutWidth = 420;

    private static void ResetApplicationState()
    {
        typeof(Application)
            .GetField("_current", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, null);

        typeof(ThemeManager)
            .GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, null);
    }

    /// <summary>一份把各类块都覆盖到的文档，用来验「追加过程中的每一步」都和全量解析一致。</summary>
    private const string Document = """
        # 标题一

        第一段正文，带 **粗体**、*斜体* 和 `行内代码`，还有一个 [链接](https://example.com)。

        - 列表项一
        - 列表项二
          - 嵌套项

        > 引用块的第一行
        > 引用块的第二行

        ```csharp
        var x = 1;
        Console.WriteLine(x);
        ```

        | 列 A | 列 B |
        | --- | --- |
        | 1 | 2 |

        ---

        ## 标题二

        收尾的一段文字，长度足够让它在窄一点的容器里换行，用来把续排的边界情况也覆盖到。
        """;

    #region 解析层：增量结果必须等于全量结果

    [Fact]
    public void IncrementalParseMatchesFullParse_AtEveryAppendStep()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var view = new Markdown { Text = string.Empty, FontSize = 14 };
            var host = new StackPanel { Width = 420 };
            host.Children.Add(view);
            MeasureHost(host);

            // 按不规则的步长喂，故意让切点落在块中间、围栏中间、表格中间。
            var step = 7;
            for (var length = 1; length <= Document.Length; length += step)
            {
                var prefix = Document.Substring(0, Math.Min(length, Document.Length));
                view.Text = prefix;
                MeasureHost(host);

                var expected = MarkdownParser.Parse(prefix, null);
                Assert.True(
                    MarkdownParser.BlocksEqual(expected, view.DebugBlocks),
                    $"增量解析与全量解析不一致，前缀长度 {prefix.Length}：\n" +
                    $"期望 {expected.Count} 块，实得 {view.DebugBlocks.Count} 块\n---\n{prefix}\n---");

                Assert.Equal(expected.Count, view.DebugContentHost!.Children.Count);
            }
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void NonAppendingEditsStillFallBackToFullParse()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var view = new Markdown { Text = "# 甲\n\n第一段", FontSize = 14 };
            var host = new StackPanel { Width = 420 };
            host.Children.Add(view);
            MeasureHost(host);

            // 截短
            view.Text = "# 甲";
            MeasureHost(host);
            Assert.True(MarkdownParser.BlocksEqual(MarkdownParser.Parse("# 甲", null), view.DebugBlocks));

            // 改中间（不是前缀关系）
            view.Text = "# 乙\n\n第一段";
            MeasureHost(host);
            Assert.True(MarkdownParser.BlocksEqual(MarkdownParser.Parse("# 乙\n\n第一段", null), view.DebugBlocks));

            // 清空
            view.Text = string.Empty;
            MeasureHost(host);
            Assert.Empty(view.DebugBlocks);
            Assert.Empty(view.DebugContentHost!.Children);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    /// <summary>
    /// 追加可以把「已经解析成别的东西」的尾巴改写成另一种块：一行 <c>| a | b |</c> 原本是段落，
    /// 等分隔行追上来就该变成表格。这类改写必须被增量路径覆盖到。
    /// </summary>
    [Theory]
    [InlineData("段落文字", "\n继续同一段")]
    [InlineData("| a | b |", "\n| --- | --- |\n| 1 | 2 |")]
    [InlineData("- 项一", "\n- 项二")]
    [InlineData("```cs\nvar x = 1;", "\n```")]
    [InlineData("# 标题", "\n\n新的一段")]
    [InlineData("一段", "\n\n## 二级标题\n\n又一段")]
    public void AppendCanRewriteTheTrailingBlock(string head, string tail)
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var view = new Markdown { Text = head, FontSize = 14 };
            var host = new StackPanel { Width = 420 };
            host.Children.Add(view);
            MeasureHost(host);

            for (var index = 1; index <= tail.Length; index++)
            {
                var full = head + tail.Substring(0, index);
                view.Text = full;
                MeasureHost(host);

                Assert.True(
                    MarkdownParser.BlocksEqual(MarkdownParser.Parse(full, null), view.DebugBlocks),
                    $"追加到「{full}」时增量结果与全量不一致");
            }
        }
        finally
        {
            ResetApplicationState();
        }
    }

    #endregion

    #region 排版层：续排结果必须等于全量排版结果

    [Fact]
    public void IncrementalLayoutMatchesFullLayout_ForWrappedText()
    {
        const double width = 260;
        var words = new StringBuilder();

        var streamed = new MarkdownTextPresenter { FontSize = 14 };
        streamed.Spans = new[] { new MarkdownTextSpan(string.Empty, PlainStyle) };
        _ = streamed.DebugLayoutSignature(width);

        for (var index = 0; index < 120; index++)
        {
            words.Append("word").Append(index % 7).Append(' ');

            streamed.Spans = new[] { new MarkdownTextSpan(words.ToString(), PlainStyle) };
            var incremental = streamed.DebugLayoutSignature(width);

            var reference = new MarkdownTextPresenter { FontSize = 14 };
            reference.Spans = new[] { new MarkdownTextSpan(words.ToString(), PlainStyle) };
            var full = reference.DebugLayoutSignature(width);

            Assert.Equal(full, incremental);
        }
    }

    [Fact]
    public void IncrementalLayoutMatchesFullLayout_AcrossStyleRunsAndLineBreaks()
    {
        const double width = 300;

        var streamed = new MarkdownTextPresenter { FontSize = 14 };
        var spans = new List<MarkdownTextSpan>();

        var pieces = new (string Text, MarkdownTextStyle Style, bool Break)[]
        {
            ("普通文字开头，", PlainStyle, false),
            ("加粗的一段", PlainStyle with { Bold = true }, false),
            ("，然后是", PlainStyle, false),
            ("code_token_here", PlainStyle with { Code = true }, false),
            ("", PlainStyle, true),
            ("换行之后继续写很长很长的一段话让它必须折行才放得下", PlainStyle, false),
            ("", PlainStyle, true),
            ("最后一段收尾文字", PlainStyle with { Italic = true }, false),
        };

        foreach (var piece in pieces)
        {
            // 每个片段再按字符逐步追加，覆盖「最后一个片段正在变长」这个流式真实形状。
            var grown = string.Empty;
            var limit = piece.Break ? 1 : piece.Text.Length;
            for (var index = 1; index <= limit; index++)
            {
                grown = piece.Break ? string.Empty : piece.Text.Substring(0, index);

                var next = new List<MarkdownTextSpan>(spans)
                {
                    new(grown, piece.Style, piece.Break),
                };

                streamed.Spans = next;
                var incremental = streamed.DebugLayoutSignature(width);

                var reference = new MarkdownTextPresenter { FontSize = 14 };
                reference.Spans = next;
                Assert.Equal(reference.DebugLayoutSignature(width), incremental);
            }

            spans.Add(new MarkdownTextSpan(grown, piece.Style, piece.Break));
        }
    }

    /// <summary>
    /// 宽度约束变了必须真的重排。
    /// </summary>
    /// <remarks>
    /// 排版缓存是按宽度存的，而 <c>MeasureOverride</c> 拿可用宽度、<c>ArrangeOverride</c> 拿最终宽度，
    /// 两者常常不同，所以缓存必须能识别「这次的宽度和上次等价」。但等价的判据只能是
    /// <b>「这份排版从没被宽度挤着换过行」</b>——不能用「内容宽度没超过约束」，那个条件恒真
    /// （内容宽度不超过约束，正是因为已经按它换过行了），照它放宽会让窗口拉宽后文字仍挤在窄栏里。
    /// </remarks>
    [Fact]
    public void ChangingTheWidthConstraintReflows()
    {
        var text = string.Join(' ', Enumerable.Range(0, 40).Select(index => "word" + index));

        var presenter = new MarkdownTextPresenter { FontSize = 14 };
        presenter.Spans = new[] { new MarkdownTextSpan(text, PlainStyle) };

        var narrow = presenter.DebugLayoutSignature(200);
        var widened = presenter.DebugLayoutSignature(900);
        var narrowedAgain = presenter.DebugLayoutSignature(200);

        Assert.NotEqual(narrow, widened);
        Assert.Equal(narrow, narrowedAgain);

        // 每一步都要和「直接按该宽度全量排一遍」一致。
        foreach (var (width, expected) in new[] { (200.0, narrow), (900.0, widened) })
        {
            var reference = new MarkdownTextPresenter { FontSize = 14 };
            reference.Spans = new[] { new MarkdownTextSpan(text, PlainStyle) };
            Assert.Equal(reference.DebugLayoutSignature(width), expected);
        }
    }

    /// <summary>
    /// 反过来，没被宽度挤过的排版换个更宽的约束就该直接复用——这条是增量能在真实容器里生效的前提：
    /// 放进横向可滚动容器时 Measure 给的是无穷、Arrange 给的是内容宽度，不认这条就每次布局白排两遍。
    /// </summary>
    [Fact]
    public void UnwrappedLayoutIsReusedAcrossEquivalentWidths()
    {
        var presenter = new MarkdownTextPresenter { FontSize = 14 };
        presenter.Spans = new[] { new MarkdownTextSpan("一行放得下的短文本", PlainStyle) };

        var before = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses);

        // Measure：无限宽度
        _ = presenter.DebugLayoutSignature(double.PositiveInfinity);
        var passesAfterMeasure = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses) - before;

        // Arrange：内容宽度，以及任何更宽的值
        _ = presenter.DebugLayoutSignature(presenter.DebugLayoutWidth);
        _ = presenter.DebugLayoutSignature(presenter.DebugLayoutWidth + 200);
        var passesTotal = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses) - before;

        Assert.Equal(1, passesAfterMeasure);
        Assert.Equal(1, passesTotal);
    }

    /// <summary>一个词就比整行还宽时会按字符切断——这条路径改成了二分查找，等价性单独钉一遍。</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public void OverlongWordWrappingMatchesAcrossWidths(double width)
    {
        var text = new string('あ', 40) + " 尾巴 " + new string('X', 60);

        var reference = new MarkdownTextPresenter { FontSize = 14 };
        reference.Spans = new[] { new MarkdownTextSpan(text, PlainStyle) };
        var full = reference.DebugLayoutSignature(width);

        var streamed = new MarkdownTextPresenter { FontSize = 14 };
        for (var index = 1; index <= text.Length; index++)
        {
            streamed.Spans = new[] { new MarkdownTextSpan(text.Substring(0, index), PlainStyle) };
            _ = streamed.DebugLayoutSignature(width);
        }

        Assert.Equal(full, streamed.DebugLayoutSignature(width));
    }

    #endregion

    #region 性能：工作量必须收敛到「只碰尾部」

    [Fact]
    public void StreamingAppendDoesNotRebuildTheVisualTree()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            // 先铺一份有若干块的文档，再在末尾持续追加。
            var seed = "# 标题\n\n第一段\n\n- 甲\n- 乙\n\n> 引用\n\n正在生成的段落：";
            var view = new Markdown { Text = seed, FontSize = 14 };
            var host = new StackPanel { Width = 420 };
            host.Children.Add(view);
            MeasureHost(host);

            PumpLayout(view, LayoutWidth);

            // 正在变长的那个段落的文字呈现器：整个追加过程里必须是同一个对象。
            var lastParagraph = Assert.IsType<MarkdownParagraphPresenter>(view.DebugContentHost!.Children[^1]);
            var textPresenterBefore = Assert.IsType<MarkdownTextPresenter>(lastParagraph.Content);

            var rebuildsBefore = Interlocked.Read(ref Markdown.DebugFullRebuilds);
            var elementsBefore = Interlocked.Read(ref Markdown.DebugBlockElementsCreated);
            var fullLayoutsBefore = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses);
            var incrementalBefore = Interlocked.Read(ref MarkdownTextPresenter.DebugIncrementalLayoutPasses);

            const int appends = 400;
            var text = new StringBuilder(seed);
            for (var index = 0; index < appends; index++)
            {
                text.Append("token").Append(index % 10).Append(' ');
                view.Text = text.ToString();
                MeasureHost(host);
                PumpLayout(view, LayoutWidth);
            }

            var rebuilds = Interlocked.Read(ref Markdown.DebugFullRebuilds) - rebuildsBefore;
            var elements = Interlocked.Read(ref Markdown.DebugBlockElementsCreated) - elementsBefore;
            var fullLayouts = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses) - fullLayoutsBefore;
            var incremental = Interlocked.Read(ref MarkdownTextPresenter.DebugIncrementalLayoutPasses) - incrementalBefore;

            Assert.Equal(0, rebuilds);
            Assert.Equal(0, elements);
            Assert.Equal(0, fullLayouts);
            Assert.True(incremental >= appends, $"增量续排只走了 {incremental} 次，期望至少 {appends} 次");

            // 元素被复用而不是重建——这是「排版缓存留住了」的硬证据，与布局是否真的跑过无关。
            var lastParagraphAfter = Assert.IsType<MarkdownParagraphPresenter>(view.DebugContentHost.Children[^1]);
            Assert.Same(textPresenterBefore, lastParagraphAfter.Content);

            // 内容仍然正确。
            Assert.True(MarkdownParser.BlocksEqual(MarkdownParser.Parse(text.ToString(), null), view.DebugBlocks));
        }
        finally
        {
            ResetApplicationState();
        }
    }

    /// <summary>
    /// 走**真实布局链**（而不是测试手动喂宽度）时，一趟 Measure + Arrange 只能排一次版。
    /// </summary>
    /// <remarks>
    /// 这条是增量能不能在真实应用里生效的前提，必须单独钉：Measure 和 Arrange 各自把自己拿到的宽度
    /// 当排版约束，两者一旦被判成「宽度变了」就会各全量排一遍，且下一趟又反过来排一遍——
    /// 缓存永远命不中，前面所有增量都白做。这种失效不会有任何报错，只会表现为"还是卡"。
    /// </remarks>
    [Fact]
    public void OneRealLayoutPassCostsOneLayoutOnly()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            // 单段落 → 控件内部只有一个文字呈现器，计数可以直接对上。
            var view = new Markdown
            {
                Text = string.Join(' ', Enumerable.Range(0, 60).Select(index => "word" + index)),
                FontSize = 14,
            };
            var host = new StackPanel { Width = 420 };
            host.Children.Add(view);

            var before = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses);
            MeasureHost(host);
            var first = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses) - before;

            Assert.Equal(1, first);

            // 再来一趟同样约束的布局，一次都不该重排。
            view.InvalidateMeasure();
            MeasureHost(host);
            var second = Interlocked.Read(ref MarkdownTextPresenter.DebugFullLayoutPasses) - before;
            Assert.Equal(1, second);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    /// <summary>
    /// 可选中片段也是「只重收尾部」的，得证明它与全量重收等价——否则流式完的那条消息选不全、
    /// 复制出来缺字，而这类错要等用户真的去框选才会暴露。
    /// </summary>
    [Fact]
    public void SelectionStaysCorrectAfterStreamingAppend()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            const string seed = "# 标题\n\n第一段\n\n- 甲\n- 乙\n\n正在写：";
            var streamed = new Markdown { Text = seed, FontSize = 14, IsTextSelectionEnabled = true };
            var host = new StackPanel { Width = 420 };
            host.Children.Add(streamed);
            MeasureHost(host);
            PumpLayout(streamed, LayoutWidth);

            var full = seed;
            for (var index = 0; index < 30; index++)
            {
                full += "词" + index + " ";
                streamed.Text = full;
                MeasureHost(host);
                PumpLayout(streamed, LayoutWidth);
            }

            // 对照：同样的文本一次到位地建一份。
            var reference = new Markdown { Text = full, FontSize = 14, IsTextSelectionEnabled = true };
            var referenceHost = new StackPanel { Width = 420 };
            referenceHost.Children.Add(reference);
            MeasureHost(referenceHost);

            // 全新的控件整条链都是脏的，MeasureHost 会真的下探到呈现器，用「内部 ScrollViewer 给的宽度」
            // 排一遍；而流式那份是靠 PumpLayout 按 LayoutWidth 排的。两者的宽度约束一旦不同，
            // 换行位置就不同，比出来的差异是约束差异而不是增量的错。所以这里把两份都按同一宽度重排。
            ForceLayout(streamed, LayoutWidth);
            ForceLayout(reference, LayoutWidth);

            streamed.SelectAll();
            reference.SelectAll();

            Assert.Equal(reference.SelectedText, streamed.SelectedText);
            Assert.False(string.IsNullOrWhiteSpace(streamed.SelectedText), "选中文本不该为空");
            Assert.Contains("词29", streamed.SelectedText, StringComparison.Ordinal);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    /// <summary>
    /// 绘制批次合并的闸门：切词把一行摊成「词/空白」交替的一长串片段，逐个 DrawText 会让一次
    /// 重绘录上几千条指令。合并后录制量必须显著下降，且内容一个字都不能少。
    /// </summary>
    [Fact]
    public void AdjacentPlacementsMergeIntoFewerDrawRuns()
    {
        const double width = 320;
        var text = string.Join(' ', Enumerable.Range(0, 400).Select(index => "minecraft" + (index % 3)));

        var presenter = new MarkdownTextPresenter { FontSize = 14 };
        presenter.Spans = new[] { new MarkdownTextSpan(text, PlainStyle) };

        var placements = presenter.DebugPlacementCount(width);
        var runs = presenter.DebugDrawRunCount(width);

        Assert.True(placements > 500, $"样本太小，片段只有 {placements} 个");

        // 一行 N 个词摊成 2N-1 个片段，合并后每行只剩一条（同款式、首尾相接）。
        Assert.True(
            runs * 4 < placements,
            $"绘制批次没有明显合并：{placements} 个片段只合成了 {runs} 条绘制指令");

        // 合并不能吃掉任何字符。
        var signature = presenter.DebugLayoutSignature(width);
        foreach (var word in new[] { "minecraft0", "minecraft1", "minecraft2" })
        {
            Assert.Contains(word, signature, StringComparison.Ordinal);
        }
    }

    /// <summary>行内代码带底色和内边距，绘制原点被 padding 推开，绝不能被并进邻居。</summary>
    [Fact]
    public void InlineCodeStaysItsOwnDrawRun()
    {
        const double width = 400;
        var presenter = new MarkdownTextPresenter { FontSize = 14 };
        presenter.Spans = new[]
        {
            new MarkdownTextSpan("前面 ", PlainStyle),
            new MarkdownTextSpan("code", PlainStyle with { Code = true }),
            new MarkdownTextSpan(" 后面", PlainStyle),
        };

        var signature = presenter.DebugLayoutSignature(width);
        Assert.Contains("␟code@", signature, StringComparison.Ordinal);
    }

    /// <summary>
    /// 排版工作量必须是「每次追加只测量新增的那点内容」，而不是「每次都把全文重测一遍」。
    /// 用测量次数的增长曲线钉死：追加次数翻倍，总测量次数不该跟着平方增长。
    /// </summary>
    [Fact]
    public void MeasurementWorkGrowsLinearlyWithAppendCount()
    {
        static long MeasureWork(int appends)
        {
            var presenter = new MarkdownTextPresenter { FontSize = 14 };
            var text = new StringBuilder();
            presenter.Spans = new[] { new MarkdownTextSpan(string.Empty, PlainStyle) };
            _ = presenter.DebugLayoutSignature(300);

            var before = Interlocked.Read(ref MarkdownTextPresenter.DebugTokenMeasurements);
            for (var index = 0; index < appends; index++)
            {
                text.Append("word").Append(index % 9).Append(' ');
                presenter.Spans = new[] { new MarkdownTextSpan(text.ToString(), PlainStyle) };
                _ = presenter.DebugLayoutSignature(300);
            }

            return Interlocked.Read(ref MarkdownTextPresenter.DebugTokenMeasurements) - before;
        }

        var small = MeasureWork(200);
        var large = MeasureWork(400);

        // 线性时比值约等于 2；退回全量重排则是 O(n²)，比值会逼近 4。留出余量取 2.6。
        var ratio = (double)large / Math.Max(1, small);
        Assert.True(ratio < 2.6, $"测量次数增长不是线性的：200 次追加 {small} 次测量，400 次追加 {large} 次测量（比值 {ratio:F2}）");
    }

    #endregion

    private static void MeasureHost(StackPanel host)
    {
        host.Measure(new Size(host.Width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, host.Width, host.DesiredSize.Height));
    }

    /// <summary>
    /// 离屏环境里把布局真正推到文字呈现器上。
    /// </summary>
    /// <remarks>
    /// <see cref="UIElement.InvalidateMeasure"/> 只把元素<b>自己</b>标脏，再交给 LayoutManager；
    /// 它不会把父链一起标脏。离屏没有窗口也就没有 LayoutManager，于是从根 <c>Measure</c> 会因为
    /// 根还是 <c>IsMeasureValid</c> 而走快路径直接返回，脏在深处的呈现器<b>一次都不会被重新测量</b>。
    /// 不补这一步，任何「排版做了多少工作」的断言都会因为「压根没排版」而假通过。
    /// 真实应用里 LayoutManager 正是从脏元素本身开始重排，这里照做。
    /// </remarks>
    private static void PumpLayout(Markdown view, double width)
    {
        foreach (var text in TextPresentersOf(view))
        {
            if (text.IsMeasureValid)
            {
                continue;
            }

            text.Measure(new Size(width, double.PositiveInfinity));
            text.Arrange(new Rect(0, 0, width, text.DesiredSize.Height));
        }
    }

    /// <summary>不管脏不脏，一律按给定宽度重排一遍，把两份控件放到同一个宽度约束上再比。</summary>
    private static void ForceLayout(Markdown view, double width)
    {
        foreach (var text in TextPresentersOf(view))
        {
            text.InvalidateMeasure();
            text.Measure(new Size(width, double.PositiveInfinity));
            text.Arrange(new Rect(0, 0, width, text.DesiredSize.Height));
        }
    }

    private static IEnumerable<MarkdownTextPresenter> TextPresentersOf(Markdown view)
    {
        var host = view.DebugContentHost;
        if (host == null)
        {
            yield break;
        }

        foreach (var child in host.Children)
        {
            if (child is ContentControl { Content: MarkdownTextPresenter text })
            {
                yield return text;
            }
        }
    }
}
