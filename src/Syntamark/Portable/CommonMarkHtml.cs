using System;
using System.Collections.Generic;

namespace Syntamark.Compilation;

/// <summary>CommonMark HTML lexical rules; deliberately does not interpret HTML contents.</summary>
internal static class CommonMarkHtml
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center",
        "col", "colgroup", "dd", "details", "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption",
        "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head",
        "header", "hr", "html", "iframe", "legend", "li", "link", "main", "menu", "menuitem", "nav",
        "noframes", "ol", "optgroup", "option", "p", "param", "search", "section", "summary", "table",
        "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track", "ul",
    };

    internal static int BlockKind(string text, int start, MdParseContext? context = null)
    {
        if (start >= text.Length || text[start] != '<')
            return 0;
        if (Starts(text, start, "<!--"))
            return 2;
        if (Starts(text, start, "<?"))
            return 3;
        if (Starts(text, start, "<![CDATA["))
            return 5;
        if (start + 2 < text.Length && text[start + 1] == '!' && Letter(text[start + 2]))
            return 4;
        var cursor = start + 1;
        var closing = cursor < text.Length && text[cursor] == '/';
        if (closing)
            cursor++;
        var nameStart = cursor;
        while (cursor < text.Length && Name(text[cursor]))
        {
            context?.Scan(8);
            cursor++;
        }
        if (cursor == nameStart)
            return 0;
        var name = text.Substring(nameStart, cursor - nameStart);
        var boundary = cursor == text.Length || text[cursor] is ' ' or '\t' or '>';
        if (!closing && RawTextTag(name) && boundary)
            return 1;
        if (BlockTags.Contains(name) && (boundary || Starts(text, cursor, "/>")))
            return 6;
        if ((!RawTextTag(name) || closing) && TryReadTag(text, start, out var length, context))
        {
            var end = start + length;
            while (end < text.Length && text[end] is ' ' or '\t')
            {
                context?.Scan(8);
                end++;
            }
            if (end == text.Length)
                return 7;
        }
        return 0;
    }

    internal static bool EndsBlock(string text, int kind, MdParseContext? context = null)
    {
        context?.Scan(text.Length);
        return kind switch
        {
            1 => text.IndexOf("</pre>", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("</script>", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("</style>", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("</textarea>", StringComparison.OrdinalIgnoreCase) >= 0,
            2 => text.IndexOf("-->", StringComparison.Ordinal) >= 0,
            3 => text.IndexOf("?>", StringComparison.Ordinal) >= 0,
            4 => text.IndexOf('>') >= 0,
            5 => text.IndexOf("]]>", StringComparison.Ordinal) >= 0,
            _ => false,
        };
    }

    internal static bool TryReadInline(string text, int start, out int length, MdParseContext? context = null)
    {
        length = 0;
        if (start < 0 || start >= text.Length || text[start] != '<')
            return false;
        if (Starts(text, start, "<!-->"))
        {
            length = 5;
            return true;
        }
        if (Starts(text, start, "<!--->"))
        {
            length = 6;
            return true;
        }
        string? terminator = null;
        var contentStart = start;
        if (Starts(text, start, "<!--"))
        {
            terminator = "-->";
            contentStart += 4;
        }
        else if (Starts(text, start, "<?"))
        {
            terminator = "?>";
            contentStart += 2;
        }
        else if (Starts(text, start, "<![CDATA["))
        {
            terminator = "]]>";
            contentStart += 9;
        }
        else if (start + 2 < text.Length && text[start + 1] == '!' && Letter(text[start + 2]))
        {
            terminator = ">";
            contentStart += 3;
        }
        if (terminator is null)
            return TryReadTag(text, start, out length, context);
        context?.Scan(text.Length - contentStart);
        var end = text.IndexOf(terminator, contentStart, StringComparison.Ordinal);
        if (end < 0)
            return false;
        length = end + terminator.Length - start;
        return true;
    }

    private static bool TryReadTag(string text, int start, out int length, MdParseContext? context)
    {
        length = 0;
        var cursor = start + 1;
        var closing = cursor < text.Length && text[cursor] == '/';
        if (closing)
            cursor++;
        if (cursor >= text.Length || !Letter(text[cursor]))
            return false;
        cursor++;
        while (cursor < text.Length && Name(text[cursor]))
        {
            context?.Scan(8);
            cursor++;
        }
        while (cursor < text.Length)
        {
            var beforeSpace = cursor;
            if (!SkipWhitespace(text, ref cursor, context))
                return false;
            if (cursor < text.Length && text[cursor] == '>')
            {
                length = cursor + 1 - start;
                return true;
            }
            if (!closing && Starts(text, cursor, "/>"))
            {
                length = cursor + 2 - start;
                return true;
            }
            if (closing || cursor == beforeSpace || cursor >= text.Length || !AttributeStart(text[cursor]))
                return false;
            cursor++;
            while (cursor < text.Length && AttributeName(text[cursor]))
            {
                context?.Scan(8);
                cursor++;
            }
            var afterName = cursor;
            if (!SkipWhitespace(text, ref cursor, context))
                return false;
            if (cursor >= text.Length || text[cursor] != '=')
            {
                cursor = afterName;
                continue;
            }
            cursor++;
            if (!SkipWhitespace(text, ref cursor, context) || cursor >= text.Length)
                return false;
            if (text[cursor] is '\'' or '"')
            {
                var quote = text[cursor++];
                while (cursor < text.Length && text[cursor] != quote)
                {
                    context?.Scan(8);
                    cursor++;
                }
                if (cursor == text.Length)
                    return false;
                cursor++;
            }
            else
            {
                var valueStart = cursor;
                while (cursor < text.Length && text[cursor] is not (' ' or '\t' or '\r' or '\n' or '"' or '\'' or '=' or '<' or '>' or '`'))
                {
                    context?.Scan(8);
                    cursor++;
                }
                if (cursor == valueStart)
                    return false;
            }
        }
        return false;
    }

    private static bool SkipWhitespace(string text, ref int cursor, MdParseContext? context)
    {
        var breaks = 0;
        while (cursor < text.Length)
        {
            context?.Scan(8);
            if (text[cursor] is ' ' or '\t')
            {
                cursor++;
                continue;
            }
            if (text[cursor] is '\r' or '\n')
            {
                if (++breaks > 1)
                    return false;
                if (text[cursor++] == '\r' && cursor < text.Length && text[cursor] == '\n')
                    cursor++;
                continue;
            }
            break;
        }
        return true;
    }

    private static bool RawTextTag(string name) => name.Equals("pre", StringComparison.OrdinalIgnoreCase)
        || name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)
        || name.Equals("textarea", StringComparison.OrdinalIgnoreCase);
    private static bool Starts(string text, int start, string value) => start >= 0 && start <= text.Length - value.Length
        && string.CompareOrdinal(text, start, value, 0, value.Length) == 0;
    private static bool Letter(char ch) => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool Name(char ch) => Letter(ch) || ch is >= '0' and <= '9' or '-';
    private static bool AttributeStart(char ch) => Letter(ch) || ch is '_' or ':';
    private static bool AttributeName(char ch) => AttributeStart(ch) || ch is >= '0' and <= '9' or '.' or '-';
}
