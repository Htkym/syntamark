using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

// Test-only observation of the internal AST. No HTML renderer is shipped in the product.
internal static class SyntaxHtml
{
    internal static string Render(IEnumerable blocks, bool decodedInfo = false) => string.Concat(blocks.Cast<object>().Select(node => Block(node, decodedInfo)));

    private static string Block(object node, bool decodedInfo) => node.GetType().Name switch
    {
        "LithoParagraph" => "<p>" + Inlines(Get<IEnumerable>(node, "Inlines")) + "</p>\n",
        "LithoHeading" => "<h" + Get<int>(node, "Level") + ">" + Inlines(Get<IEnumerable>(node, "Inlines"))
            + "</h" + Get<int>(node, "Level") + ">\n",
        "LithoBreak" => "<hr />\n",
        "LithoCode" => Code(node, decodedInfo),
        "LithoQuote" => "<blockquote>\n" + Render(Get<IEnumerable>(node, "Children"), decodedInfo) + "</blockquote>\n",
        "LithoHtmlBlock" => Get<string>(node, "Text"),
        "LithoList" => List(node, decodedInfo),
        _ => throw new NotSupportedException("AST node not supported by test renderer: " + node.GetType().Name),
    };

    private static string Code(object node, bool decodedInfo)
    {
        var info = Get<string?>(node, "Info");
        if (!decodedInfo && info is not null && (info.Contains('\\') || info.Contains('&')))
            throw new NotSupportedException("Test renderer does not interpret raw info-string escape/entity semantics.");
        var language = info?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return "<pre><code" + (language is null ? "" : " class=\"language-" + Escape(language) + "\"") + ">"
            + Escape(Get<string>(node, "Text")) + "</code></pre>\n";
    }

    private static string List(object node, bool decodedInfo)
    {
        var ordered = Get<bool>(node, "Ordered");
        var tag = ordered ? "ol" : "ul";
        var start = Get<int>(node, "Start");
        var output = new StringBuilder("<" + tag + (ordered && start != 1 ? " start=\"" + start.ToString(CultureInfo.InvariantCulture) + "\"" : "") + ">\n");
        foreach (var item in Get<IEnumerable>(node, "Items"))
        {
            if (Get<bool?>(item!, "Checked").HasValue)
                throw new NotSupportedException("GFM task item conflicts with pure CommonMark.");
            var children = Get<IEnumerable>(item!, "Children").Cast<object>().ToArray();
            output.Append("<li>");
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (Get<bool>(node, "Tight") && child.GetType().Name == "LithoParagraph")
                {
                    output.Append(Inlines(Get<IEnumerable>(child, "Inlines")));
                    if (i + 1 < children.Length)
                        output.Append('\n');
                }
                else
                {
                    if (i == 0)
                        output.Append('\n');
                    output.Append(Block(child, decodedInfo));
                }
            }
            output.Append("</li>\n");
        }
        return output.Append("</" + tag + ">\n").ToString();
    }

    private static string Inlines(IEnumerable nodes) => string.Concat(nodes.Cast<object>().Select(Inline));

    private static string Inline(object node) => node.GetType().Name switch
    {
        "LithoText" => Escape(Get<string>(node, "Text")),
        "LithoCodeSpan" => "<code>" + Escape(Get<string>(node, "Code")) + "</code>",
        "LithoLineBreak" => Get<bool>(node, "Hard") ? "<br />\n" : "\n",
        "LithoEmphasis" => Emphasis(node),
        "LithoLink" => Link(node),
        "LithoAutolink" => "<a href=\"" + Escape(Href(Get<string>(node, "Href"))) + "\">" + Escape(Get<string>(node, "Text")) + "</a>",
        "LithoHtmlInline" => Get<string>(node, "Text"),
        _ => throw new NotSupportedException("AST inline not supported by test renderer: " + node.GetType().Name),
    };

    private static string Emphasis(object node)
    {
        var tag = Get<int>(node, "Level") == 1 ? "em" : "strong";
        return "<" + tag + ">" + Inlines(Get<IEnumerable>(node, "Children")) + "</" + tag + ">";
    }

    private static string Link(object node)
    {
        var title = Get<string?>(node, "Title");
        var suffix = title is null ? "" : " title=\"" + Escape(title) + "\"";
        var label = Get<IEnumerable>(node, "Label");
        var url = Escape(Href(Get<string>(node, "Url")));
        return Get<bool>(node, "Image")
            ? "<img src=\"" + url + "\" alt=\"" + Escape(Plain(label)) + "\"" + suffix + " />"
            : "<a href=\"" + url + "\"" + suffix + ">" + Inlines(label) + "</a>";
    }

    private static string Plain(IEnumerable nodes) => string.Concat(nodes.Cast<object>().Select(node => node.GetType().Name switch
    {
        "LithoText" => Get<string>(node, "Text"),
        "LithoCodeSpan" => Get<string>(node, "Code"),
        "LithoAutolink" => Get<string>(node, "Text"),
        "LithoLineBreak" => "\n",
        "LithoHtmlInline" => "",
        "LithoEmphasis" => Plain(Get<IEnumerable>(node, "Children")),
        "LithoLink" => Plain(Get<IEnumerable>(node, "Label")),
        _ => throw new NotSupportedException("Image label node not supported by test renderer: " + node.GetType().Name),
    }));

    internal static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string Href(string text)
    {
        var output = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes(text))
            if (value < 128 && (char.IsAsciiLetterOrDigit((char)value) || "!#$%&'()*+,-./:;=?@_~".Contains((char)value)))
                output.Append((char)value);
            else
                output.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
        return output.ToString();
    }

    internal static T Get<T>(object value, string property) => (T)value.GetType()
        .GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetValue(value)!;

    internal static void CheckAdapter()
    {
        // Hand-built DTOs isolate the renderer from parser behavior and official examples.
        var label = new object[] { new LithoText("a<&\""), new LithoEmphasis(1, [new LithoText("b")]) };
        var blocks = new object[]
        {
            new LithoHeading(2, label), new LithoBreak(), new LithoCode("x<&\n", "c# ignored"),
            new LithoParagraph([new LithoLink(label, "/日本 a[]", "t&\"", false), new LithoLineBreak(true), new LithoCodeSpan("x&")]),
            new LithoList(true, 3, true, [new LithoItem([new LithoParagraph([new LithoText("item")])], null)]),
            new LithoQuote([new LithoParagraph([new LithoText("q")])]),
            new LithoParagraph([new LithoLink(label, "/i", null, true), new LithoAutolink("mailto:x@y", "x@y")]),
            new LithoHtmlBlock("<div>\nraw\n</div>\n"),
            new LithoParagraph([new LithoHtmlInline("<span>"), new LithoText("<&"), new LithoHtmlInline("</span>")]),
        };
        const string expected = "<h2>a&lt;&amp;&quot;<em>b</em></h2>\n<hr />\n<pre><code class=\"language-c#\">x&lt;&amp;\n</code></pre>\n"
            + "<p><a href=\"/%E6%97%A5%E6%9C%AC%20a%5B%5D\" title=\"t&amp;&quot;\">a&lt;&amp;&quot;<em>b</em></a><br />\n<code>x&amp;</code></p>\n"
            + "<ol start=\"3\">\n<li>item</li>\n</ol>\n<blockquote>\n<p>q</p>\n</blockquote>\n"
            + "<p><img src=\"/i\" alt=\"a&lt;&amp;&quot;b\" /><a href=\"mailto:x@y\">x@y</a></p>\n"
            + "<div>\nraw\n</div>\n<p><span>&lt;&amp;</span></p>\n";
        if (Render(blocks) != expected)
            throw new InvalidOperationException("Test renderer golden failed.");
        if (Render(new object[] { new LithoCode("x\n", "c&") }, decodedInfo: true)
            != "<pre><code class=\"language-c&amp;\">x\n</code></pre>\n")
            throw new InvalidOperationException("Decoded info renderer golden failed.");
        try
        {
            Render(new object[] { new object() });
            throw new InvalidOperationException("Test renderer silently accepted an unknown node.");
        }
        catch (NotSupportedException) { }
    }

    private sealed record LithoText(string Text);
    private sealed record LithoEmphasis(int Level, object[] Children);
    private sealed record LithoHeading(int Level, object[] Inlines);
    private sealed record LithoParagraph(object[] Inlines);
    private sealed record LithoBreak;
    private sealed record LithoCode(string Text, string? Info);
    private sealed record LithoCodeSpan(string Code);
    private sealed record LithoLineBreak(bool Hard);
    private sealed record LithoLink(object[] Label, string Url, string? Title, bool Image);
    private sealed record LithoAutolink(string Href, string Text);
    private sealed record LithoList(bool Ordered, int Start, bool Tight, object[] Items);
    private sealed record LithoItem(object[] Children, bool? Checked);
    private sealed record LithoQuote(object[] Children);
    private sealed record LithoHtmlBlock(string Text);
    private sealed record LithoHtmlInline(string Text);
}
