extern alias sg;

using sg::Jalium.UI.Xaml.SourceGenerator;
using SgVirtualizeKind = sg::Jalium.UI.Markup.RazorVirtualizeKind;

namespace Jalium.UI.Tests;

/// <summary>
/// Covers the compile-time half of <c>@virtualize</c>: that the parser lifts it, that the loop
/// variable is rebound to the item, and that the document keeps its generated
/// <c>InitializeComponent</c> instead of falling back to runtime parsing.
/// </summary>
/// <remarks>
/// The fallback is the part worth guarding. Any loop keyword in a document sets
/// <c>HasStructuralRazor</c>, and the generator then emits a thin wrapper that hands the whole
/// document to the runtime reader — losing compile-time binding lowering and the trimming
/// guarantees that come with it. <c>@virtualize</c> is lifted before that check runs, so it must
/// leave the flag clear.
/// </remarks>
public sealed class RazorVirtualizeLoweringTests
{
    private const string Xmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static JalxamlParseResult Parse(string body) =>
        JalxamlParser.Parse($$"""
            <StackPanel xmlns="{{Xmlns}}"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        x:Class="Sample.View">
            {{body}}
            </StackPanel>
            """, "Sample.jalxaml")!;

    private static JalxamlAstNode? FindVirtualize(JalxamlAstNode node)
    {
        if (node.LocalName == JalxamlParser.RazorVirtualizeElementName)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindVirtualize(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void CollectionForm_IsLiftedWithoutForcingTheRuntimeFallback()
    {
        var result = Parse("""
              @virtualize(var row in Rows)
              {
                <TextBlock Text="@row.Name" />
              }
            """);

        Assert.False(result.HasStructuralRazor);
        Assert.True(result.HasVirtualize);
        Assert.Empty(result.LoweringDiagnostics);

        var node = FindVirtualize(result.Root!);
        Assert.NotNull(node);
        Assert.Equal("row", node!.Virtualize!.ItemVariable);
        Assert.Equal("Rows", node.Virtualize.SourceExpression);
    }

    [Fact]
    public void TheLoopVariableIsRewrittenToTheItem()
    {
        var result = Parse("""
              @virtualize(var row in Rows)
              {
                <TextBlock Text="@row.Name" Tag="@rowIndex" />
              }
            """);

        var body = FindVirtualize(result.Root!)!.Children[0];
        var text = body.Attributes.Single(a => a.LocalName == "Text");
        var tag = body.Attributes.Single(a => a.LocalName == "Tag");

        Assert.Equal("@#.Name", text.Value);

        // A longer identifier that merely starts with the loop variable is not a reference.
        Assert.Equal("@rowIndex", tag.Value);
    }

    [Fact]
    public void NumericForm_IsLiftedWithItsBounds()
    {
        var result = Parse("""
              @virtualize(var i = 0; i <= Count; i += 2)
              {
                <TextBlock Text="@i." />
              }
            """);

        Assert.False(result.HasStructuralRazor);
        var loop = FindVirtualize(result.Root!)!.Virtualize!;

        Assert.Equal(SgVirtualizeKind.Range, loop.Kind);
        Assert.Equal("0", loop.StartExpression);
        Assert.Equal("Count", loop.EndExpression);
        Assert.Equal("2", loop.StepExpression);
        Assert.True(loop.EndInclusive);
    }

    [Fact]
    public void NumericFormSurvivesTheLessThanInItsHeader()
    {
        // The '<' would otherwise read as the start of a tag. The header is consumed as a
        // balanced parenthesis group before the scanner ever looks at it that way.
        var result = Parse("""
              @virtualize(var i = 0; i < 10; i++)
              {
                <TextBlock Text="@i." />
              }
            """);

        Assert.NotNull(FindVirtualize(result.Root!));
        Assert.Empty(result.LoweringDiagnostics);
    }

    [Fact]
    public void NestedLoops_ResolveTheInnerSourceAgainstTheOuterItem()
    {
        var result = Parse("""
              @virtualize(var g in Groups)
              {
                <StackPanel>
                  @virtualize(var it in g.Items)
                  {
                    <TextBlock Text="@it.Name" />
                  }
                </StackPanel>
              }
            """);

        Assert.Empty(result.LoweringDiagnostics);

        var outer = FindVirtualize(result.Root!)!;
        var inner = FindVirtualize(outer.Children[0])!;

        Assert.Equal("Groups", outer.Virtualize!.SourceExpression);

        // The inner host lives in the outer template, so its source is relative to the outer item.
        Assert.Equal("#.Items", inner.Virtualize!.SourceExpression);
        Assert.Equal("@#.Name", inner.Children[0].Attributes.Single(a => a.LocalName == "Text").Value);
    }

    [Fact]
    public void ReachingForAnEnclosingLoopsItemIsReported()
    {
        var result = Parse("""
              @virtualize(var g in Groups)
              {
                <StackPanel>
                  @virtualize(var it in g.Items)
                  {
                    <TextBlock Text="@g.Title" />
                  }
                </StackPanel>
              }
            """);

        var diagnostic = Assert.Single(result.LoweringDiagnostics);
        Assert.Equal(RazorVirtualizeLowering.OuterVariableId, diagnostic.Id);
    }

    [Theory]
    [InlineData("<TextBlock /><TextBlock />", "two roots")]
    [InlineData("", "empty")]
    public void ABodyThatCannotBeATemplateRootIsReported(string body, string _)
    {
        var result = Parse($$"""
              @virtualize(var row in Rows)
              {
                {{body}}
              }
            """);

        var diagnostic = Assert.Single(result.LoweringDiagnostics);
        Assert.Equal(RazorVirtualizeLowering.UnsupportedBodyId, diagnostic.Id);
    }

    [Fact]
    public void AHeaderThatIsNotALoopIsReported()
    {
        var result = Parse("""
              @virtualize(whatever)
              {
                <TextBlock />
              }
            """);

        var diagnostic = Assert.Single(result.LoweringDiagnostics);
        Assert.Equal(RazorVirtualizeLowering.UnsupportedHeaderId, diagnostic.Id);
    }

    [Fact]
    public void TheGeneratedCodeBuildsAHostWithOneSharedTemplate()
    {
        var result = Parse("""
              @virtualize(var row in Rows)
              {
                <TextBlock Text="@row.Name" />
              }
            """);

        var code = JalxamlCodeGenerator.TryEmitInitializeBody(result, symbols: null, xmlnsResolver: null);
        Assert.NotNull(code);

        Assert.Contains("new global::Jalium.UI.Controls.RazorItemsHost()", code);
        Assert.Contains("SetRazorBinding", code);
        Assert.Contains("\"ItemsSource\"", code);
        Assert.Contains("SetVisualTree", code);
        Assert.Contains("@#.Name", code);

        // One template assignment per host, so the recycling fast path in ContentPresenter can
        // compare it by reference across every container.
        Assert.Equal(1, code!.Split(".ItemTemplate =").Length - 1);
    }

    [Fact]
    public void TheNumericFormEmitsLiteralBoundsRatherThanBindings()
    {
        var result = Parse("""
              @virtualize(var i = 0; i < 500; i++)
              {
                <TextBlock Text="@i." />
              }
            """);

        var code = JalxamlCodeGenerator.TryEmitInitializeBody(result, symbols: null, xmlnsResolver: null);
        Assert.NotNull(code);

        Assert.Contains("IsRangeSource = true", code);
        Assert.Contains("RangeStart = 0;", code);
        Assert.Contains("RangeEnd = 500;", code);

        // Constant bounds have nothing to observe, so binding them would build multi-bindings that
        // can never fire.
        Assert.DoesNotContain("\"RangeEnd\"", code);
    }

    [Fact]
    public void ADocumentWithoutVirtualizeIsUnaffected()
    {
        var result = Parse("""
              <TextBlock Text="@Title" />
            """);

        Assert.False(result.HasVirtualize);
        Assert.False(result.HasUnloweredVirtualize);
        Assert.Empty(result.LoweringDiagnostics);
    }
}
