using System.Collections.ObjectModel;
using Jalium.UI.Build;
using Jalium.UI.Controls;
using Jalium.UI.Markup;
using Microsoft.Build.Utilities;

namespace Jalium.UI.Tests;

/// <summary>
/// Covers <c>@foreach</c> / <c>@for</c> reading the loaded component and its DataContext.
/// </summary>
/// <remarks>
/// <para>
/// These loops are expanded while the document is being tokenized: the body is appended once per
/// element and the result re-tokenized. Until now that ran in an empty scope, so a loop could only
/// walk a self-contained literal — and a loop over a view-model property failed the <em>build</em>,
/// because the build-time expander compiles the block against nothing but System and friends.
/// </para>
/// <para>
/// The expansion still happens once, at load. A collection that changes afterwards does not
/// re-expand and the elements are not virtualized; that is what <c>@virtualize</c> is for. What
/// changes here is only that the loop can see data at all.
/// </para>
/// </remarks>
[Collection("Application")]
public sealed class RazorLoopDataAccessTests
{
    private const string Xmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private sealed class ViewWithRows : Border
    {
        public ObservableCollection<string> Rows { get; } = new() { "alpha", "beta", "gamma" };

        public int RowCount => 4;
    }

    private sealed class Model
    {
        public string[] Rows { get; } = { "from-data-context" };
    }

    [Fact]
    public void ForeachReadsACollectionOffTheLoadedComponent()
    {
        var view = new ViewWithRows();

        XamlReader.LoadComponentFromString(view, $$"""
            <Border xmlns="{{Xmlns}}">
              <StackPanel>
                @foreach(var row in Rows)
                {
                  <TextBlock Text="@row" />
                }
              </StackPanel>
            </Border>
            """);

        var panel = Assert.IsType<StackPanel>(view.Child);
        Assert.Equal(3, panel.Children.Count);
        Assert.Equal("alpha", ((TextBlock)panel.Children[0]).Text);
        Assert.Equal("gamma", ((TextBlock)panel.Children[2]).Text);
    }

    [Fact]
    public void ForReadsACountOffTheLoadedComponent()
    {
        var view = new ViewWithRows();

        XamlReader.LoadComponentFromString(view, $$"""
            <Border xmlns="{{Xmlns}}">
              <StackPanel>
                @for(var i = 0; i < RowCount; i++)
                {
                  <TextBlock Text="row" />
                }
              </StackPanel>
            </Border>
            """);

        Assert.Equal(4, ((StackPanel)view.Child!).Children.Count);
    }

    [Fact]
    public void TheDataContextShadowsTheComponent()
    {
        // Same order every other Razor path uses: DataContext first, then the component.
        var view = new ViewWithRows();
        view.DataContext = new Model();

        XamlReader.LoadComponentFromString(view, $$"""
            <Border xmlns="{{Xmlns}}">
              <StackPanel>
                @foreach(var row in Rows)
                {
                  <TextBlock Text="@row" />
                }
              </StackPanel>
            </Border>
            """);

        var panel = (StackPanel)view.Child!;
        Assert.Equal(1, panel.Children.Count);
        Assert.Equal("from-data-context", ((TextBlock)panel.Children[0]).Text);
    }

    [Fact]
    public void ParsingWithoutAComponentBehavesAsBefore()
    {
        // XamlReader.Parse has nothing to read from, so an unresolved name still yields nothing
        // rather than throwing.
        var panel = (StackPanel)XamlReader.Parse($$"""
            <StackPanel xmlns="{{Xmlns}}">
              @foreach(var row in Rows)
              {
                <TextBlock Text="@row" />
              }
            </StackPanel>
            """);

        Assert.Equal(0, panel.Children.Count);
    }

    [Fact]
    public void LiteralLoopsStillExpandTheWayTheyAlwaysDid()
    {
        var panel = (StackPanel)XamlReader.Parse($$"""
            <StackPanel xmlns="{{Xmlns}}">
              @foreach(var name in new[]{"a", "b"})
              {
                <TextBlock Text="@name" />
              }
            </StackPanel>
            """);

        Assert.Equal(2, panel.Children.Count);
    }

    [Fact]
    public void TheBuildTaskLeavesADataDrivenLoopForTheRuntime()
    {
        // Before, this failed the build: the block is compiled against System/Linq/Collections/Text
        // only, so 'Rows' is an unresolved name — a compile error the task surfaced as MSBuild
        // error. It has to survive to the runtime instead, unchanged.
        var (succeeded, output, errors) = RunTransform("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              @foreach(var row in Rows)
              {
                <TextBlock Text="@row" />
              }
            </Border>
            """);

        Assert.True(succeeded, $"a loop over runtime data must not fail the build: {errors}");
        Assert.Contains("@foreach(var row in Rows)", output);
    }

    [Fact]
    public void TheBuildTaskStillExpandsALiteralLoop()
    {
        var (succeeded, output, errors) = RunTransform("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              @foreach(var name in new[]{"a", "b"})
              {
                <TextBlock Text="@name" />
              }
            </Border>
            """);

        Assert.True(succeeded, errors);
        Assert.DoesNotContain("@foreach", output);
    }

    [Fact]
    public void TheBuildTaskStillFailsOnARealSyntaxError()
    {
        // Deferring everything would trade a clear build error for a mystery at run time.
        var (succeeded, _, _) = RunTransform("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              @foreach(var name in new[]{"a" "b"})
              {
                <TextBlock Text="@name" />
              }
            </Border>
            """);

        Assert.False(succeeded);
    }

    /// <summary>Keeps the task's errors so a failure says why rather than just that.</summary>
    private sealed class CollectingBuildEngine : Microsoft.Build.Framework.IBuildEngine
    {
        public List<string> Errors { get; } = new();

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(
            string projectFileName, string[] targetNames,
            System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;

        public void LogCustomEvent(Microsoft.Build.Framework.CustomBuildEventArgs e) { }

        public void LogErrorEvent(Microsoft.Build.Framework.BuildErrorEventArgs e) => Errors.Add(e.Message);

        public void LogMessageEvent(Microsoft.Build.Framework.BuildMessageEventArgs e) { }

        public void LogWarningEvent(Microsoft.Build.Framework.BuildWarningEventArgs e) { }
    }

    private static (bool Succeeded, string Output, string Errors) RunTransform(string markup)
    {
        var root = Path.Combine(Path.GetTempPath(), "Jalium.UI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "View.jalxaml");
            var outputDirectory = Path.Combine(root, "obj");
            File.WriteAllText(source, markup);

            var engine = new CollectingBuildEngine();
            var task = new TransformJalxamlRazorTask
            {
                BuildEngine = engine,
                SourceFiles = new[] { new TaskItem(source) },
                OutputDirectory = outputDirectory,
                ProjectDirectory = root,
            };

            var succeeded = task.Execute();
            var produced = Path.Combine(outputDirectory, "View.jalxaml");
            var text = File.Exists(produced) ? File.ReadAllText(produced) : string.Empty;
            return (succeeded, text, string.Join(" | ", engine.Errors));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A transient lock on a temp file is not worth failing a test over.
            }
        }
    }
}
