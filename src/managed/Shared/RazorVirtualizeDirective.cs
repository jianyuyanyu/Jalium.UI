using System.Text;

namespace Jalium.UI.Markup;

/// <summary>The two shapes a <c>@virtualize</c> header can take.</summary>
internal enum RazorVirtualizeKind
{
    /// <summary><c>var item in Collection</c>.</summary>
    Collection,

    /// <summary><c>var i = 0; i &lt; Count; i++</c>.</summary>
    Range,
}

/// <summary>One parsed <c>@virtualize</c> header.</summary>
internal sealed class RazorVirtualizeLoop
{
    public RazorVirtualizeKind Kind { get; set; }

    /// <summary>The loop variable, which becomes the item template's DataContext.</summary>
    public string ItemVariable { get; set; } = string.Empty;

    /// <summary>An optional XAML type name annotation, used to keep item members from being trimmed.</summary>
    public string? ItemTypeName { get; set; }

    /// <summary>The collection expression, for <see cref="RazorVirtualizeKind.Collection"/>.</summary>
    public string SourceExpression { get; set; } = string.Empty;

    /// <summary>The initial value, for <see cref="RazorVirtualizeKind.Range"/>.</summary>
    public string StartExpression { get; set; } = string.Empty;

    /// <summary>The bound the loop stops at.</summary>
    public string EndExpression { get; set; } = string.Empty;

    /// <summary>The increment. Always present; defaults to <c>1</c> or <c>-1</c>.</summary>
    public string StepExpression { get; set; } = "1";

    /// <summary>Whether the condition was <c>&lt;=</c> or <c>&gt;=</c> rather than <c>&lt;</c> or <c>&gt;</c>.</summary>
    public bool EndInclusive { get; set; }
}

/// <summary>
/// Parses a <c>@virtualize</c> header and builds the markup it lowers to.
/// </summary>
/// <remarks>
/// Compiled into both the runtime XAML reader and the source generator, so a directive means the
/// same thing whether it was compiled ahead of time or parsed on load.
/// </remarks>
internal static class RazorVirtualizeDirective
{
    /// <summary>
    /// Parses <paramref name="header"/> — the text between the directive's parentheses.
    /// Returns false for anything that is not one of the two supported loop shapes; the caller
    /// reports that rather than guessing, because a half-understood loop would silently produce a
    /// list bound to the wrong thing.
    /// </summary>
    public static bool TryParseHeader(string header, out RazorVirtualizeLoop loop)
    {
        loop = new RazorVirtualizeLoop();
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var parts = SplitTopLevel(header, ';');
        return parts.Count == 3
            ? TryParseRange(parts, loop)
            : parts.Count == 1 && TryParseCollection(header, loop);
    }

    private static bool TryParseCollection(string header, RazorVirtualizeLoop loop)
    {
        var inIndex = FindTopLevelWord(header, "in");
        if (inIndex < 0)
        {
            return false;
        }

        var left = header.Substring(0, inIndex).Trim();
        var right = header.Substring(inIndex + 2).Trim();
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        var tokens = left.Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 2)
        {
            return false;
        }

        var name = tokens[tokens.Length - 1];
        if (!IsIdentifier(name))
        {
            return false;
        }

        loop.Kind = RazorVirtualizeKind.Collection;
        loop.ItemVariable = name;
        loop.SourceExpression = right;

        // "var item" carries no type; "vm:Person item" names one worth pinning for the trimmer.
        if (tokens.Length == 2 && tokens[0] != "var")
        {
            loop.ItemTypeName = tokens[0];
        }

        return true;
    }

    private static bool TryParseRange(System.Collections.Generic.List<string> parts, RazorVirtualizeLoop loop)
    {
        // init: [var|int|long|...] i = expr
        var init = parts[0].Trim();
        var eq = init.IndexOf('=');
        if (eq < 0)
        {
            return false;
        }

        var declTokens = init.Substring(0, eq).Trim()
            .Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (declTokens.Length is < 1 or > 2)
        {
            return false;
        }

        var name = declTokens[declTokens.Length - 1];
        var start = init.Substring(eq + 1).Trim();
        if (!IsIdentifier(name) || start.Length == 0)
        {
            return false;
        }

        // condition: i < expr  (also <=, >, >=)
        var condition = parts[1].Trim();
        if (!TryReadComparison(condition, name, out var end, out var inclusive, out var ascending))
        {
            return false;
        }

        // step: i++ / ++i / i-- / --i / i += expr / i -= expr
        if (!TryReadStep(parts[2].Trim(), name, out var step, out var stepAscending))
        {
            return false;
        }

        // A loop whose condition and step disagree about direction never terminates. Rejecting it
        // at parse time beats materializing that as a range.
        if (ascending != stepAscending)
        {
            return false;
        }

        loop.Kind = RazorVirtualizeKind.Range;
        loop.ItemVariable = name;
        loop.StartExpression = start;
        loop.EndExpression = end;
        loop.StepExpression = step;
        loop.EndInclusive = inclusive;
        return true;
    }

    private static bool TryReadComparison(string condition, string name, out string end, out bool inclusive, out bool ascending)
    {
        end = string.Empty;
        inclusive = false;
        ascending = true;

        var opIndex = condition.IndexOfAny(new[] { '<', '>' });
        if (opIndex < 0 || condition.Substring(0, opIndex).Trim() != name)
        {
            return false;
        }

        ascending = condition[opIndex] == '<';
        var after = opIndex + 1;
        if (after < condition.Length && condition[after] == '=')
        {
            inclusive = true;
            after++;
        }

        end = condition.Substring(after).Trim();
        return end.Length > 0;
    }

    private static bool TryReadStep(string step, string name, out string expression, out bool ascending)
    {
        expression = "1";
        ascending = true;

        if (step == name + "++" || step == "++" + name)
        {
            return true;
        }

        if (step == name + "--" || step == "--" + name)
        {
            expression = "-1";
            ascending = false;
            return true;
        }

        foreach (var op in new[] { "+=", "-=" })
        {
            var index = step.IndexOf(op, System.StringComparison.Ordinal);
            if (index < 0 || step.Substring(0, index).Trim() != name)
            {
                continue;
            }

            var amount = step.Substring(index + 2).Trim();
            if (amount.Length == 0)
            {
                return false;
            }

            ascending = op == "+=";
            expression = ascending ? amount : "-(" + amount + ")";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the markup a <c>@virtualize</c> block lowers to: a host bound to the sequence, with
    /// the loop body as its item template.
    /// </summary>
    public static string BuildHostMarkup(RazorVirtualizeLoop loop, string body)
    {
        var template = RazorLoopVariableRewriter.RewriteMarkup(body, loop.ItemVariable);
        var sb = new StringBuilder(template.Length + 256);

        sb.Append("<RazorItemsHost");
        if (loop.Kind == RazorVirtualizeKind.Collection)
        {
            sb.Append(" ItemsSource=\"").Append(EscapeAttribute("@(" + loop.SourceExpression + ")")).Append('"');
        }
        else
        {
            sb.Append(" IsRangeSource=\"True\"")
              .Append(" RangeStart=\"").Append(EscapeAttribute("@(" + loop.StartExpression + ")")).Append('"')
              .Append(" RangeEnd=\"").Append(EscapeAttribute("@(" + loop.EndExpression + ")")).Append('"')
              .Append(" RangeStep=\"").Append(EscapeAttribute("@(" + loop.StepExpression + ")")).Append('"')
              .Append(" RangeEndInclusive=\"").Append(loop.EndInclusive ? "True" : "False").Append('"');
        }

        sb.Append("><RazorItemsHost.ItemTemplate><DataTemplate>")
          .Append(template)
          .Append("</DataTemplate></RazorItemsHost.ItemTemplate></RazorItemsHost>");
        return sb.ToString();
    }

    private static string EscapeAttribute(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits on <paramref name="separator"/> at bracket depth zero, skipping string and character
    /// literals, so a semicolon inside <c>"a;b"</c> or a lambda body does not split the header.
    /// </summary>
    private static System.Collections.Generic.List<string> SplitTopLevel(string text, char separator)
    {
        var result = new System.Collections.Generic.List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == separator && depth == 0)
            {
                result.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        result.Add(text.Substring(start));
        return result;
    }

    /// <summary>
    /// Finds a whole-word occurrence of <paramref name="word"/> at bracket depth zero. Scanning
    /// for <c>" in "</c> instead would split
    /// <c>var x in Items.Where(i =&gt; i.Tag == " in ")</c> in the wrong place.
    /// </summary>
    private static int FindTopLevelWord(string text, string word)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                depth--;
                continue;
            }

            if (depth != 0 || c != word[0] || i + word.Length > text.Length)
            {
                continue;
            }

            if (string.CompareOrdinal(text, i, word, 0, word.Length) != 0)
            {
                continue;
            }

            var before = i == 0 || !IsIdentifierPart(text[i - 1]);
            var afterIndex = i + word.Length;
            var after = afterIndex >= text.Length || !IsIdentifierPart(text[afterIndex]);
            if (before && after)
            {
                return i;
            }
        }

        return -1;
    }

    private static int SkipLiteral(string text, int index)
    {
        var quote = text[index];
        var i = index + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return i;
            }

            i++;
        }

        return text.Length - 1;
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!IsIdentifierPart(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);
}
