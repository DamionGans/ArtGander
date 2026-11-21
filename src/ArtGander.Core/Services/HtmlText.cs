using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtGander.Core.Services;

/// <summary>
/// Simple HTML for descriptions, the same kind of markup the Printify and Shopify text editors produce:
/// paragraphs, line breaks, bold, italic, underline, strikethrough, headings, bulleted and numbered lists,
/// quotes and links. Anything else (scripts, styles, other tags and attributes) is removed when cleaning up,
/// keeping the text inside.
/// </summary>
public static partial class HtmlText
{
    /// <summary>The tags a description may use.</summary>
    public static IReadOnlySet<string> AllowedTags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "strong", "b", "em", "i", "u", "s", "strike", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "a",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase) { "br" };
    private static readonly HashSet<string> DropWithContent = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "iframe", "object", "embed", "head", "title" };

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"href\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"<[a-zA-Z/!]")]
    private static partial Regex AnyTagRegex();

    /// <summary>Whether the text contains HTML tags at all.</summary>
    public static bool HasMarkup(string? text) => text is not null && AnyTagRegex().IsMatch(text);

    /// <summary>
    /// Keeps only the allowed tags (and only <c>href</c> on links, only web and mail links), removes everything else
    /// but keeps its text, and closes tags left open. Plain text without any tags is returned as it is.
    /// </summary>
    public static string Clean(string? html)
    {
        if (string.IsNullOrEmpty(html) || !HasMarkup(html)) return html ?? "";

        // Drop dangerous elements with their content first.
        foreach (var tag in DropWithContent)
            html = Regex.Replace(html, $@"<{tag}\b[^>]*>.*?</{tag}\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var sb = new StringBuilder();
        var open = new Stack<string>();
        var last = 0;
        foreach (Match m in TagRegex().Matches(html))
        {
            sb.Append(EscapeStray(html[last..m.Index]));
            last = m.Index + m.Length;
            if (m.Value.StartsWith("<!--", StringComparison.Ordinal)) continue; // comments

            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (name == "div") name = "p"; // editors often produce divs for paragraphs
            if (!AllowedTags.Contains(name)) continue;

            if (VoidTags.Contains(name)) { sb.Append("<br>"); continue; }
            if (closing)
            {
                if (!open.Contains(name)) continue; // a closing tag without an opening one
                while (open.Count > 0)
                {
                    var top = open.Pop();
                    sb.Append("</").Append(top).Append('>');
                    if (top == name) break;
                }
                continue;
            }

            if (name == "a")
            {
                var href = HrefRegex().Match(m.Groups[3].Value) is { Success: true } h
                    ? h.Groups[2].Success && h.Groups[2].Value.Length > 0 ? h.Groups[2].Value : h.Groups[3].Success && h.Groups[3].Value.Length > 0 ? h.Groups[3].Value : h.Groups[4].Value
                    : "";
                href = WebUtility.HtmlDecode(href).Trim();
                if (!href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    href = "";
                sb.Append(href.Length > 0 ? $"<a href=\"{WebUtility.HtmlEncode(href)}\">" : "<a>");
            }
            else
            {
                sb.Append('<').Append(name).Append('>');
            }
            open.Push(name);
        }
        sb.Append(EscapeStray(html[last..]));
        while (open.Count > 0) sb.Append("</").Append(open.Pop()).Append('>');
        return sb.ToString();
    }

    /// <summary>A lone "&lt;" or "&gt;" in the text (e.g. "3 &lt; 4") is written as an entity, so it isn't read as a tag.</summary>
    private static string EscapeStray(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Turns plain text into paragraphs: blank lines separate paragraphs, single line breaks become &lt;br&gt;.</summary>
    public static string FromPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (HasMarkup(text)) return text;
        var paragraphs = Regex.Split(text.ReplaceLineEndings("\n").Trim(), @"\n\s*\n")
            .Select(p => "<p>" + string.Join("<br>", p.Split('\n').Select(l => WebUtility.HtmlEncode(l.Trim()))) + "</p>");
        return string.Join("\n", paragraphs);
    }

    /// <summary>The text without markup, e.g. for search and short previews. Lists become lines starting with "•".</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        if (!HasMarkup(html)) return html;
        var s = Regex.Replace(html, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</(p|h[1-6]|blockquote|div)>", "\n", RegexOptions.IgnoreCase);
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"[ \t]*\n\s*", "\n").Trim();
    }
}
