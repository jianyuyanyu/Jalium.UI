using System.Collections.Generic;
using System.Text;

namespace Jalium.UI.Markup;

/// <summary>
/// Rewrites references to a <c>@virtualize</c> loop variable into DataContext-relative Razor
/// paths, so a loop body can be lifted verbatim into a <see cref="System.Object"/> item template.
/// </summary>
/// <remarks>
/// <para>
/// This file is compiled into both the runtime XAML reader and the source generator. Those two
/// lower <c>@virtualize</c> along different paths, and if they disagreed by even one character the
/// same markup would bind differently depending on whether it was compiled or parsed at run time.
/// The project files link this single copy rather than keeping one each, which is what the
/// <c>{Binding}</c> lowering has to police by hand.
/// </para>
/// <para>
/// The rewrite targets keep their <c>@</c> sigil deliberately. <c>RazorTemplateParser</c> only
/// produces a path or expression segment when it sees one, so a bare <c>#.Name</c> would be
/// treated as literal text. Rewriting a whole loop variable produces <c>@#.</c> with the trailing
/// dot, because the <c>#.</c> case in <c>CreatePreferredPathBinding</c> requires at least two
/// characters; a lone <c>#</c> falls through to an unusable <c>Binding("#")</c>, whereas
/// <c>@#.</c> becomes <c>Binding("")</c>, which resolves to the DataContext itself.
/// </para>
/// </remarks>
internal static class RazorLoopVariableRewriter
{
    /// <summary>
    /// Rewrites every Razor region in <paramref name="value"/> — an attribute value, a text node,
    /// or a whole markup body. Text outside a Razor region, including markup extensions such as
    /// <c>{Binding Name}</c>, is copied through untouched: inside an item template those already
    /// resolve against the item.
    /// </summary>
    public static string RewriteMarkup(string value, string itemVariable)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(itemVariable) ||
            value.IndexOf('@') < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        var i = 0;

        while (i < value.Length)
        {
            var c = value[i];

            // "\@" and "@@" are escapes for a literal '@'; neither opens a region.
            if (c == '\\' && i + 1 < value.Length && value[i + 1] == '@')
            {
                sb.Append(c).Append('@');
                i += 2;
                continue;
            }

            if (c != '@')
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '@')
            {
                sb.Append("@@");
                i += 2;
                continue;
            }

            if (i + 1 < value.Length && value[i + 1] == '(')
            {
                var close = FindBalanced(value, i + 1, '(', ')');
                if (close < 0)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                sb.Append("@(")
                  .Append(RewriteExpression(value.Substring(i + 2, close - i - 2), itemVariable))
                  .Append(')');
                i = close + 1;
                continue;
            }

            // A "@{ ... }" code block runs in the interpreter's own scope and never sees the item,
            // so its contents are left alone; the caller reports the reference instead.
            if (i + 1 < value.Length && value[i + 1] == '{')
            {
                var close = FindBalanced(value, i + 1, '{', '}');
                if (close < 0)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                sb.Append(value, i, close - i + 1);
                i = close + 1;
                continue;
            }

            var pathEnd = ReadPath(value, i + 1);
            if (pathEnd < 0)
            {
                sb.Append(c);
                i++;
                continue;
            }

            sb.Append('@').Append(RewritePath(value.Substring(i + 1, pathEnd - i - 1), itemVariable));
            i = pathEnd;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rewrites a bare C# expression — an <c>@if</c> condition, or the source expression of a
    /// nested <c>@virtualize</c> header, neither of which carries a <c>@</c> sigil.
    /// </summary>
    public static string RewriteExpression(string expression, string itemVariable)
    {
        if (string.IsNullOrEmpty(expression) || string.IsNullOrEmpty(itemVariable))
        {
            return expression;
        }

        var sb = new StringBuilder(expression.Length + 8);
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            // A loop variable named inside a string literal is just text.
            if (c == '"' || c == '\'')
            {
                var quote = c;
                sb.Append(c);
                i++;
                while (i < expression.Length)
                {
                    sb.Append(expression[i]);
                    if (expression[i] == '\\' && i + 1 < expression.Length)
                    {
                        sb.Append(expression[i + 1]);
                        i += 2;
                        continue;
                    }

                    var isClose = expression[i] == quote;
                    i++;
                    if (isClose)
                    {
                        break;
                    }
                }

                continue;
            }

            // "$." and "#." are already element- or DataContext-relative.
            if ((c == '$' || c == '#') && i + 1 < expression.Length && expression[i + 1] == '.')
            {
                var start = i;
                i += 2;
                while (i < expression.Length && IsPathPart(expression[i]))
                {
                    i++;
                }

                sb.Append(expression, start, i - start);
                continue;
            }

            // Numeric literals can contain '.' and letters ("1.5e3f"), so consume them whole
            // rather than letting the identifier scanner see the suffix.
            if (c >= '0' && c <= '9')
            {
                var start = i;
                while (i < expression.Length && IsNumberPart(expression[i]))
                {
                    i++;
                }

                sb.Append(expression, start, i - start);
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i;
                while (i < expression.Length && IsPathPart(expression[i]))
                {
                    i++;
                }

                sb.Append(RewritePath(expression.Substring(start, i - start), itemVariable));
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reports every enclosing-scope loop variable referenced by <paramref name="value"/>. A data
    /// template has one DataContext, so a body can only speak about its own item; naming an outer
    /// one is a mistake worth a diagnostic rather than a binding that silently resolves to null.
    /// </summary>
    public static bool ReferencesAny(string value, IEnumerable<string> variables)
    {
        foreach (var variable in variables)
        {
            if (string.IsNullOrEmpty(variable))
            {
                continue;
            }

            // A reference is exactly what the rewriter would change, so ask it rather than
            // maintaining a second scanner that could disagree about what counts as a reference.
            if (!string.Equals(RewriteMarkup(value, variable), value, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces a dotted path whose root is the loop variable. Only the root segment is compared,
    /// so <c>rowIndex</c>, <c>other.row</c> and <c>row2</c> are all left alone.
    /// </summary>
    private static string RewritePath(string path, string itemVariable)
    {
        if (path.Length == 0 || path[0] == '$' || path[0] == '#')
        {
            return path;
        }

        var rootEnd = path.IndexOf('.');
        var root = rootEnd < 0 ? path : path.Substring(0, rootEnd);
        if (!string.Equals(root, itemVariable, System.StringComparison.Ordinal))
        {
            return path;
        }

        // "@row" alone means the item itself. "#." with the trailing dot binds to an empty path,
        // which resolves to the DataContext; a lone "#" does not.
        return rootEnd < 0 ? "#." : "#" + path.Substring(rootEnd);
    }

    /// <summary>
    /// Returns the index just past a <c>@path</c> starting at <paramref name="start"/>, or -1 when
    /// there is no path there. Mirrors <c>RazorTemplateParser.ParsePath</c>.
    /// </summary>
    private static int ReadPath(string value, int start)
    {
        if (start >= value.Length || !IsPathStart(value[start]))
        {
            return -1;
        }

        var i = start + 1;
        while (i < value.Length && IsPathPart(value[i]))
        {
            i++;
        }

        return i;
    }

    private static int FindBalanced(string value, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < value.Length && value[i] != quote)
                {
                    if (value[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsPathStart(char c) => c == '_' || c == '$' || c == '#' || char.IsLetter(c);

    private static bool IsPathPart(char c) =>
        char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '[' || c == ']' || c == '$' || c == '#';

    private static bool IsNumberPart(char c) =>
        char.IsDigit(c) || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-' ||
        c == 'L' || c == 'l' || c == 'F' || c == 'f' || c == 'D' || c == 'd' ||
        c == 'M' || c == 'm' || c == 'U' || c == 'u' || c == 'x' || c == 'X' ||
        (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
