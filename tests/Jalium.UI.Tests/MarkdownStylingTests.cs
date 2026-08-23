using System.Linq;
using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Media;

namespace Jalium.UI.Tests;

[Collection("Application")]
public class MarkdownStylingTests
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

    private static Markdown Realize(Markdown markdown, double width = 480, double height = 400)
    {
        var host = new StackPanel { Width = width, Height = height };
        host.Children.Add(markdown);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        return markdown;
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child != null && FindVisual<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static List<T> FindAllVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        var results = new List<T>();
        Walk(root);
        return results;

        void Walk(DependencyObject node)
        {
            if (node is T match)
            {
                results.Add(match);
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child != null)
                {
                    Walk(child);
                }
            }
        }
    }

    [Fact]
    public void Markdown_RendersOneBlockPresenterPerBlock()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var markdown = Realize(new Markdown
            {
                Text = """
                # Title

                Paragraph text.

                > Quoted text.

                - First
                - Second

                ---

                | a | b |
                | --- | --- |
                | 1 | 2 |
                """
            });

            var missing = new List<string>();
            void Require<T>() where T : DependencyObject
            {
                if (FindVisual<T>(markdown) == null) missing.Add(typeof(T).Name);
            }

            Require<MarkdownHeadingPresenter>();
            Require<MarkdownParagraphPresenter>();
            Require<MarkdownQuotePresenter>();
            Require<MarkdownListPresenter>();
            Require<MarkdownTablePresenter>();
            Require<MarkdownTableCellPresenter>();
            Require<MarkdownRulePresenter>();

            Assert.True(missing.Count == 0, "missing: " + string.Join(", ", missing) +
                " | blocks: " + string.Join(", ", markdown.DebugBlocks.Select(b => b.GetType().Name)));
            Assert.Equal(2, FindAllVisuals<MarkdownListItemPresenter>(markdown).Count);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Markdown_HeadingStyle_OverridesThemeWithoutLosingItsTemplate()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var accent = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
            var markdown = Realize(new Markdown
            {
                Text = "# Title",
                HeadingStyle = new Style(typeof(MarkdownHeadingPresenter))
                {
                    Setters = { new Setter(Control.ForegroundProperty, accent) }
                }
            });

            var heading = FindVisual<MarkdownHeadingPresenter>(markdown);
            Assert.NotNull(heading);

            // 显式容器样式叠在主题样式之上：自定义 setter 生效，模板与按级别触发的默认值仍来自主题。
            Assert.Same(accent, heading!.Foreground);
            Assert.NotNull(heading.Template);
            Assert.Equal(2.0, heading.FontSizeRatio);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Markdown_HeadingLevel_DrivesFontSizeRatioAndSeparator()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var markdown = Realize(new Markdown
            {
                Text = """
                # One

                ### Three
                """
            });

            var headings = FindAllVisuals<MarkdownHeadingPresenter>(markdown);
            Assert.Equal(2, headings.Count);

            Assert.Equal(1, headings[0].Level);
            Assert.Equal(2.0, headings[0].FontSizeRatio);
            Assert.True(headings[0].HasSeparator);

            Assert.Equal(3, headings[1].Level);
            Assert.Equal(1.4, headings[1].FontSizeRatio);
            Assert.False(headings[1].HasSeparator);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Markdown_InlineStylesAndBrushes_InheritDownToTextPresenter()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var codeStyle = new MarkdownInlineStyle
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2, 7, 2),
            };
            var linkForeground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

            var markdown = Realize(new Markdown
            {
                Text = "Text with `code` and [a link](https://example.com).",
                InlineCodeStyle = codeStyle,
                LinkForeground = linkForeground,
            });

            var text = FindVisual<MarkdownTextPresenter>(markdown);
            Assert.NotNull(text);
            Assert.Same(codeStyle, text!.InlineCodeStyle);
            Assert.Same(linkForeground, text.LinkForeground);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Markdown_BlockLevelStyle_OverridesInheritedInlineStyle()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var documentBold = MarkdownInlineStyle.CreateBold();
            var headingBold = new MarkdownInlineStyle { FontWeight = FontWeights.Black };

            var markdown = Realize(new Markdown
            {
                Text = """
                # **Heading**

                Body **bold**.
                """,
                BoldStyle = documentBold,
                HeadingStyle = new Style(typeof(MarkdownHeadingPresenter))
                {
                    Setters = { new Setter(MarkdownBlockPresenter.BoldStyleProperty, headingBold) }
                }
            });

            var heading = FindVisual<MarkdownHeadingPresenter>(markdown);
            var paragraph = FindVisual<MarkdownParagraphPresenter>(markdown);
            Assert.NotNull(heading);
            Assert.NotNull(paragraph);

            Assert.Same(headingBold, FindVisual<MarkdownTextPresenter>(heading!)!.BoldStyle);
            Assert.Same(documentBold, FindVisual<MarkdownTextPresenter>(paragraph!)!.BoldStyle);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void MarkdownListItem_MarkerFollowsKindAndGlyphs()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var markdown = Realize(new Markdown
            {
                Text = """
                - Bullet item

                1. Numbered item
                2. Another one

                - [x] Done
                - [ ] Todo
                """
            });

            var items = FindAllVisuals<MarkdownListItemPresenter>(markdown);
            Assert.Equal(5, items.Count);

            Assert.Equal(MarkdownListMarkerKind.Bullet, items[0].MarkerKind);
            Assert.Equal("•", items[0].Marker);

            Assert.Equal(MarkdownListMarkerKind.Number, items[1].MarkerKind);
            Assert.Equal("1.", items[1].Marker);
            Assert.Equal("2.", items[2].Marker);

            Assert.Equal(MarkdownListMarkerKind.Task, items[3].MarkerKind);
            Assert.Equal("[x]", items[3].Marker);
            Assert.Equal("[ ]", items[4].Marker);

            Assert.True(items[4].IsLastItem);

            // 模板里的标记文本走 {TemplateBinding Marker}，顺带确认这条绑定真的接上了。
            Assert.Equal("•", FindVisual<TextBlock>(items[0])?.Text);
            Assert.Equal("1.", FindVisual<TextBlock>(items[1])?.Text);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void MarkdownListItem_BulletGlyphFromStyle_ChangesTheMarker()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var markdown = Realize(new Markdown
            {
                Text = "- First\n- Second",
                ListItemStyle = new Style(typeof(MarkdownListItemPresenter))
                {
                    Setters = { new Setter(MarkdownListItemPresenter.BulletGlyphProperty, "→") }
                }
            });

            var items = FindAllVisuals<MarkdownListItemPresenter>(markdown);
            Assert.Equal(2, items.Count);
            Assert.All(items, item => Assert.Equal("→", item.Marker));
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Markdown_BlockContainerStyleSelector_WinsOverTheFixedStyleProperty()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var selected = new Style(typeof(MarkdownParagraphPresenter))
            {
                Setters = { new Setter(FrameworkElement.MarginProperty, new Thickness(1, 2, 3, 4)) }
            };
            var fallback = new Style(typeof(MarkdownParagraphPresenter))
            {
                Setters = { new Setter(FrameworkElement.MarginProperty, new Thickness(9)) }
            };

            var markdown = Realize(new Markdown
            {
                Text = "Paragraph text.",
                ParagraphStyle = fallback,
                BlockContainerStyleSelector = new ParagraphOnlySelector(selected),
            });

            var paragraph = FindVisual<MarkdownParagraphPresenter>(markdown);
            Assert.NotNull(paragraph);
            Assert.Equal(new Thickness(1, 2, 3, 4), paragraph!.Margin);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void MarkdownCodePresenter_ForwardsCodeAndGutterSettingsToItsTextPart()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var markdown = Realize(new Markdown
            {
                Text = """
                ```csharp
                var x = 1;
                ```
                """,
                CodeBlockStyle = new Style(typeof(MarkdownCodePresenter))
                {
                    Setters = { new Setter(MarkdownCodePresenter.ShowLineNumbersProperty, false) }
                }
            });

            var codeText = FindVisual<MarkdownCodeTextPresenter>(markdown);
            Assert.NotNull(codeText);
            Assert.Equal("var x = 1;", codeText!.Code);
            Assert.Equal("csharp", codeText.CodeLanguage);
            Assert.False(codeText.ShowLineNumbers);
        }
        finally
        {
            ResetApplicationState();
        }
    }

    [Fact]
    public void Markdown_ChangingInheritedFontSize_RelayoutsExistingTextPresenters()
    {
        ResetApplicationState();
        _ = new Application();

        try
        {
            var markdown = new Markdown { Text = "Paragraph text that stays on one line." };
            var host = new StackPanel { Width = 480, Height = 400 };
            host.Children.Add(markdown);
            host.Measure(new Size(480, 400));
            host.Arrange(new Rect(0, 0, 480, 400));

            var text = FindVisual<MarkdownTextPresenter>(markdown);
            Assert.NotNull(text);
            var before = text!.DesiredSize.Height;
            Assert.True(before > 0);

            // 属性继承是按需向上取值的，叶子呈现器收不到祖先属性变化的回调，
            // 因此这里验证 Markdown 会主动让已缓存的排版失效。
            markdown.FontSize = 28;
            Assert.False(text.IsMeasureValid);

            // 直接量这个呈现器：InvalidateMeasure 只标记自身并入队，测试里没有布局管理器
            // 驱动整帧，从 host 再量一次会因为约束没变而短路。
            text.Measure(new Size(400, double.PositiveInfinity));

            Assert.True(text.DesiredSize.Height > before,
                $"expected relayout after FontSize change, before={before}, after={text.DesiredSize.Height}");
        }
        finally
        {
            ResetApplicationState();
        }
    }

    private sealed class ParagraphOnlySelector : StyleSelector
    {
        private readonly Style _style;

        public ParagraphOnlySelector(Style style) => _style = style;

        public override Style? SelectStyle(object item, DependencyObject container)
            => container is MarkdownParagraphPresenter ? _style : null;
    }
}
