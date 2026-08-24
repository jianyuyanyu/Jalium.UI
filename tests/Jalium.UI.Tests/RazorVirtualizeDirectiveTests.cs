using System.Collections.ObjectModel;
using Jalium.UI.Controls;
using Jalium.UI.Markup;

namespace Jalium.UI.Tests;

/// <summary>
/// Covers <c>@virtualize</c> end to end through the runtime XAML reader — the path hot reload and
/// <see cref="XamlReader.Parse"/> take.
/// </summary>
/// <remarks>
/// The contrast with <c>@foreach</c> is the point. That one is expanded in the tokenizer: the body
/// is appended once per element and re-tokenized, producing a real element per item, and it cannot
/// see the DataContext at all because the interpreter runs in an empty scope. <c>@virtualize</c>
/// instead emits a host bound to the collection, so the items arrive as data and the element count
/// follows the viewport.
/// </remarks>
[Collection("Application")]
public sealed class RazorVirtualizeDirectiveTests
{
    private sealed class Row
    {
        public Row(string name) => Name = name;

        public string Name { get; }
    }

    private sealed class Model
    {
        public ObservableCollection<Row> Rows { get; } = new();
    }

    private const string Xmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static Border ParseCollectionForm() => (Border)XamlReader.Parse($$"""
        <Border xmlns="{{Xmlns}}">
          @virtualize(var row in Rows)
          {
            <TextBlock Text="@row.Name" />
          }
        </Border>
        """);

    [Fact]
    public void CollectionForm_LowersToAHostRatherThanExpandingTheBody()
    {
        var root = ParseCollectionForm();

        var host = Assert.IsType<RazorItemsHost>(root.Child);
        Assert.NotNull(host.ItemTemplate);
    }

    [Fact]
    public void CollectionForm_BindsTheSourceToTheDataContext()
    {
        var root = ParseCollectionForm();
        var model = new Model();
        model.Rows.Add(new Row("first"));
        model.Rows.Add(new Row("second"));

        root.DataContext = model;

        var host = (RazorItemsHost)root.Child!;
        Assert.Same(model.Rows, host.ItemsSource);
        Assert.Equal(2, host.Items.Count);
    }

    [Fact]
    public void CollectionForm_RebindsTheLoopVariableToTheItem()
    {
        var root = ParseCollectionForm();
        root.DataContext = new Model();

        var host = (RazorItemsHost)root.Child!;
        var content = host.ItemTemplate!.LoadContent();

        var text = Assert.IsType<TextBlock>(content);
        text.DataContext = new Row("bound");

        Assert.Equal("bound", text.Text);
    }

    [Fact]
    public void CollectionForm_FollowsCollectionChanges()
    {
        var root = ParseCollectionForm();
        var model = new Model();
        root.DataContext = model;

        var host = (RazorItemsHost)root.Child!;
        Assert.Equal(0, host.Items.Count);

        model.Rows.Add(new Row("added"));

        Assert.Equal(1, host.Items.Count);
    }

    [Fact]
    public void NumericForm_LowersToARangeSource()
    {
        var root = (Border)XamlReader.Parse($$"""
            <Border xmlns="{{Xmlns}}">
              @virtualize(var i = 0; i < 2500; i++)
              {
                <TextBlock Text="@i." />
              }
            </Border>
            """);

        var host = Assert.IsType<RazorItemsHost>(root.Child);
        Assert.True(host.IsRangeSource);
        Assert.IsType<RazorIntRange>(host.ItemsSource);
        Assert.Equal(2500, host.Items.Count);
    }

    [Fact]
    public void ForeachStillExpandsSoNothingExistingChangesMeaning()
    {
        var panel = (StackPanel)XamlReader.Parse($$"""
            <StackPanel xmlns="{{Xmlns}}">
              @foreach(var name in new[]{"a", "b", "c"})
              {
                <TextBlock Text="@name" />
              }
            </StackPanel>
            """);

        Assert.Equal(3, panel.Children.Count);
    }

    [Theory]
    [InlineData("var row in Rows", "row", "Rows")]
    [InlineData("var r in Model.Items.Where(x => x.Ok)", "r", "Model.Items.Where(x => x.Ok)")]
    [InlineData("Person p in People", "p", "People")]
    public void HeaderParsesTheCollectionForm(string header, string variable, string source)
    {
        Assert.True(RazorVirtualizeDirective.TryParseHeader(header, out var loop));
        Assert.Equal(RazorVirtualizeKind.Collection, loop.Kind);
        Assert.Equal(variable, loop.ItemVariable);
        Assert.Equal(source, loop.SourceExpression);
    }

    [Fact]
    public void HeaderKeepsAnItemTypeAnnotationForTrimming()
    {
        Assert.True(RazorVirtualizeDirective.TryParseHeader("vm:Person p in People", out var loop));
        Assert.Equal("vm:Person", loop.ItemTypeName);

        Assert.True(RazorVirtualizeDirective.TryParseHeader("var p in People", out var untyped));
        Assert.Null(untyped.ItemTypeName);
    }

    [Fact]
    public void HeaderDoesNotSplitOnTheWordInInsideALiteral()
    {
        // Scanning for " in " rather than a whole word at bracket depth zero would cut this in the
        // middle of the lambda.
        Assert.True(RazorVirtualizeDirective.TryParseHeader(
            """var x in Items.Where(i => i.Tag == " in ")""", out var loop));

        Assert.Equal("x", loop.ItemVariable);
        Assert.Equal("""Items.Where(i => i.Tag == " in ")""", loop.SourceExpression);
    }

    [Theory]
    [InlineData("var i = 0; i < N; i++", "0", "N", "1", false)]
    [InlineData("var i = 1; i <= N; i += 2", "1", "N", "2", true)]
    [InlineData("int i = N; i > 0; i--", "N", "0", "-1", false)]
    public void HeaderParsesTheNumericForm(
        string header, string start, string end, string step, bool inclusive)
    {
        Assert.True(RazorVirtualizeDirective.TryParseHeader(header, out var loop));
        Assert.Equal(RazorVirtualizeKind.Range, loop.Kind);
        Assert.Equal(start, loop.StartExpression);
        Assert.Equal(end, loop.EndExpression);
        Assert.Equal(step, loop.StepExpression);
        Assert.Equal(inclusive, loop.EndInclusive);
    }

    [Theory]
    [InlineData("var i = 0; i < N; i--")]   // condition and step disagree: never terminates
    [InlineData("var i = 0; i > N; i++")]
    [InlineData("")]
    [InlineData("row in")]
    [InlineData("var a, b in Items")]
    [InlineData("var i = 0; i < N")]
    public void HeaderRejectsShapesItCannotLowerFaithfully(string header)
    {
        Assert.False(RazorVirtualizeDirective.TryParseHeader(header, out _));
    }

    [Theory]
    [InlineData("@row.Name", "@#.Name")]
    [InlineData("@row", "@#.")]
    [InlineData("@(row.Age > 18)", "@(#.Age > 18)")]
    [InlineData("Hi @row.Name!", "Hi @#.Name!")]
    [InlineData("@rowIndex", "@rowIndex")]              // longer identifier, not the loop variable
    [InlineData("@other.row", "@other.row")]            // loop name as a member, not a root
    [InlineData("@@row", "@@row")]                      // escaped '@' opens no region
    [InlineData("{Binding Name}", "{Binding Name}")]    // already item-relative inside a template
    [InlineData("""@("row is a string")""", """@("row is a string")""")]
    [InlineData("@(1.5e3 + row.X)", "@(1.5e3 + #.X)")]  // numeric suffixes are not identifiers
    [InlineData("@#.Name", "@#.Name")]                  // already rewritten
    public void TheRewriterOnlyTouchesRealReferences(string input, string expected)
    {
        Assert.Equal(expected, RazorLoopVariableRewriter.RewriteMarkup(input, "row"));
    }

    [Fact]
    public void TheRewriterReportsReferencesToAnEnclosingLoopVariable()
    {
        // A data template has one DataContext, so an inner body cannot also speak about the outer
        // item. Detecting it is what lets the caller say so instead of emitting a null binding.
        Assert.True(RazorLoopVariableRewriter.ReferencesAny("@g.Title", new[] { "g" }));
        Assert.False(RazorLoopVariableRewriter.ReferencesAny("@it.Title", new[] { "g" }));
    }

    private sealed class Priced
    {
        public double Value { get; set; } = 12.34;

        public string Label { get; set; } = "plain";
    }

    [Fact]
    public void APlainItemPathRendersInsideATemplate()
    {
        var block = (TextBlock)XamlReader.Parse($$"""
            <TextBlock xmlns="{{Xmlns}}" Text="@#.Label" />
            """);

        block.DataContext = new Priced();

        Assert.Equal("plain", block.Text);
    }

    [Fact]
    public void AMethodCallOnAnItemPathRendersInsideATemplate()
    {
        // The rewriter turns "@(row.Value.ToString(\"F1\"))" into this, so whether a call on a
        // "#."-rooted path evaluates decides whether formatting inside a loop body works at all.
        var block = (TextBlock)XamlReader.Parse($$"""
            <TextBlock xmlns="{{Xmlns}}" Text="@(#.Value.ToString(&quot;F1&quot;))" />
            """);

        block.DataContext = new Priced();

        Assert.Equal("12.3", block.Text);
    }

    [Fact]
    public void AMethodCallOnAPlainPathRendersInsideATemplate()
    {
        // Same call, without the "#." prefix. If this works and the prefixed one does not, the
        // prefix handling is the problem rather than method calls in general.
        var block = (TextBlock)XamlReader.Parse($$"""
            <TextBlock xmlns="{{Xmlns}}" Text="@(Value.ToString(&quot;F1&quot;))" />
            """);

        block.DataContext = new Priced();

        Assert.Equal("12.3", block.Text);
    }
}
