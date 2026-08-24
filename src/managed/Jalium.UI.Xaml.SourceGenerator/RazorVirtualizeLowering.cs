using System.Collections.Generic;
using Jalium.UI.Markup;

namespace Jalium.UI.Xaml.SourceGenerator;

/// <summary>
/// Second half of the <c>@virtualize</c> lowering: parses each lifted header, checks the body can
/// be a template, and rewrites loop-variable references to be DataContext-relative.
/// </summary>
/// <remarks>
/// <para>
/// This runs against the parsed tree rather than the raw text, which is the whole reason it is a
/// separate pass. The scanner that lifts the block cannot tell a loop variable inside an attribute
/// value from the same characters inside a comment or a CDATA section, and it has no line numbers
/// to put on a diagnostic. The tree has both, and the same lesson is already baked into how
/// <c>@if</c> purity is judged.
/// </para>
/// <para>
/// Nested loops fall out of the walk rather than needing special handling. A nested host element
/// sits inside the outer body, so its own header is rewritten in the outer scope — <c>g.Items</c>
/// becomes <c>#.Items</c>, resolved against the outer item — while its body is rewritten in the
/// inner scope. The two scopes stay apart because the bindings hang off different elements.
/// </para>
/// </remarks>
internal static class RazorVirtualizeLowering
{
    internal const string HeaderAttributeName = "__Header";

    /// <summary>Diagnostic id for a header that is not a loop shape we can lower.</summary>
    internal const string UnsupportedHeaderId = "JALXAML004";

    /// <summary>Diagnostic id for a body that cannot become a data template.</summary>
    internal const string UnsupportedBodyId = "JALXAML005";

    /// <summary>Diagnostic id for a body that reaches for an enclosing loop's item.</summary>
    internal const string OuterVariableId = "JALXAML006";

    public static void Lower(JalxamlParseResult result)
    {
        if (result.Root is null)
        {
            return;
        }

        Walk(result.Root, new List<string>(), result);
    }

    private static void Walk(JalxamlAstNode node, List<string> scopes, JalxamlParseResult result)
    {
        if (node.LocalName == JalxamlParser.RazorVirtualizeElementName)
        {
            LowerVirtualizeNode(node, scopes, result);
            return;
        }

        if (scopes.Count > 0)
        {
            RewriteNode(node, scopes, result);
        }

        foreach (var child in node.Children)
        {
            Walk(child, scopes, result);
        }

        foreach (var propertyElement in node.PropertyElements)
        {
            foreach (var child in propertyElement.Children)
            {
                Walk(child, scopes, result);
            }
        }
    }

    private static void LowerVirtualizeNode(JalxamlAstNode node, List<string> scopes, JalxamlParseResult result)
    {
        var header = ReadHeader(node);
        if (header is null || !RazorVirtualizeDirective.TryParseHeader(header, out var loop))
        {
            Report(result, node, UnsupportedHeaderId,
                $"'@virtualize({header})' is not a loop shape that can be lowered. Write " +
                "'@virtualize(var item in Collection)' or '@virtualize(var i = 0; i < Count; i++)'.");
            return;
        }

        // The header names things in the ENCLOSING scope: a nested loop's source expression is a
        // member of the outer item, not of its own.
        foreach (var enclosing in scopes)
        {
            loop.SourceExpression = RazorLoopVariableRewriter.RewriteExpression(loop.SourceExpression, enclosing);
            loop.StartExpression = RazorLoopVariableRewriter.RewriteExpression(loop.StartExpression, enclosing);
            loop.EndExpression = RazorLoopVariableRewriter.RewriteExpression(loop.EndExpression, enclosing);
            loop.StepExpression = RazorLoopVariableRewriter.RewriteExpression(loop.StepExpression, enclosing);
        }

        if (!ValidateBody(node, result))
        {
            return;
        }

        node.Virtualize = loop;
        result.HasVirtualize = true;

        scopes.Add(loop.ItemVariable);
        foreach (var child in node.Children)
        {
            Walk(child, scopes, result);
        }

        scopes.RemoveAt(scopes.Count - 1);
    }

    /// <summary>
    /// A data template has exactly one visual root, so the body must too.
    /// </summary>
    /// <remarks>
    /// Wrapping a multi-root body in a synthetic panel would compile, but it would also change the
    /// layout — silently, and in the one place where layout decides whether virtualization works
    /// at all. Saying so is better than guessing which panel the author meant.
    /// </remarks>
    private static bool ValidateBody(JalxamlAstNode node, JalxamlParseResult result)
    {
        if (node.Children.Count != 1)
        {
            Report(result, node, UnsupportedBodyId,
                node.Children.Count == 0
                    ? "'@virtualize' has an empty body; it needs one element to use as the item template."
                    : $"'@virtualize' has {node.Children.Count} root elements in its body but an item " +
                      "template takes exactly one. Wrap the body in a single container element.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(node.TextContent))
        {
            Report(result, node, UnsupportedBodyId,
                "'@virtualize' has loose text in its body, which cannot be a template root. " +
                "Wrap it in a <TextBlock>.");
            return false;
        }

        if (node.PropertyElements.Count > 0)
        {
            Report(result, node, UnsupportedBodyId,
                "'@virtualize' has a property element in its body, which cannot be a template root.");
            return false;
        }

        return true;
    }

    private static void RewriteNode(JalxamlAstNode node, List<string> scopes, JalxamlParseResult result)
    {
        var item = scopes[scopes.Count - 1];

        foreach (var attribute in node.Attributes)
        {
            // x:Name and friends are directives, not values, and xmlns declarations are neither.
            if (attribute.Kind is JalxamlAttributeKind.XDirective or JalxamlAttributeKind.XmlnsDecl)
            {
                continue;
            }

            if (ReportOuterReference(attribute.Value, scopes, result, node))
            {
                continue;
            }

            attribute.Value = RazorLoopVariableRewriter.RewriteMarkup(attribute.Value, item);
        }

        if (node.TextContent is { } text && text.Length > 0)
        {
            if (!ReportOuterReference(text, scopes, result, node))
            {
                node.TextContent = RazorLoopVariableRewriter.RewriteMarkup(text, item);
            }
        }

        if (node.RazorIfCondition is { } condition && condition.Length > 0)
        {
            node.RazorIfCondition = RazorLoopVariableRewriter.RewriteExpression(condition, item);
        }
    }

    /// <summary>
    /// Flags a reference to an item from an enclosing loop.
    /// </summary>
    /// <remarks>
    /// There is one DataContext per template, so the inner body simply cannot see the outer item,
    /// and any binding produced for it would resolve to null at run time. Emitting a diagnostic
    /// instead of that binding is the difference between a build error and a blank cell nobody can
    /// explain. The escape hatch is a plain <c>{Binding}</c> with a RelativeSource, which the
    /// generator passes through untouched.
    /// </remarks>
    private static bool ReportOuterReference(
        string value, List<string> scopes, JalxamlParseResult result, JalxamlAstNode node)
    {
        if (scopes.Count < 2)
        {
            return false;
        }

        for (var i = 0; i < scopes.Count - 1; i++)
        {
            if (!RazorLoopVariableRewriter.ReferencesAny(value, new[] { scopes[i] }))
            {
                continue;
            }

            Report(result, node, OuterVariableId,
                $"'{scopes[i]}' belongs to an enclosing '@virtualize', and a data template has a " +
                "single DataContext, so the inner body cannot resolve it. Project the value into " +
                "the inner collection, or bind explicitly with " +
                "'{Binding DataContext." + scopes[i] + "..., RelativeSource={RelativeSource AncestorType=...}}'.");
            return true;
        }

        return false;
    }

    private static string? ReadHeader(JalxamlAstNode node)
    {
        foreach (var attribute in node.Attributes)
        {
            if (attribute.Kind == JalxamlAttributeKind.Value &&
                string.Equals(attribute.LocalName, HeaderAttributeName, System.StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    private static void Report(JalxamlParseResult result, JalxamlAstNode node, string id, string message)
    {
        result.LoweringDiagnostics.Add(new JalxamlLoweringDiagnostic
        {
            Id = id,
            Message = message,
            LineNumber = node.LineNumber,
            LinePosition = node.LinePosition,
            IsError = true,
        });
    }
}
