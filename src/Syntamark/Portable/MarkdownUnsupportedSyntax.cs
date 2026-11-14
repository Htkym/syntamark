using System;
using System.Collections.Generic;

namespace Syntamark.Compilation;

internal enum MdUnsupportedSyntaxKind { Footnote, DefinitionList, GenericAttribute, GridTable }

internal sealed class MdUnsupportedSyntaxCandidate(MdUnsupportedSyntaxKind kind, string reason, MdRawRange rawSpan)
{
    internal MdUnsupportedSyntaxKind Kind { get; } = kind;
    internal string Reason { get; } = reason;
    internal MdRawRange RawSpan { get; } = rawSpan;
}

/// <summary>Recognizes literal extension markers in the existing parse; it does not parse or render extensions.</summary>
internal static class MdUnsupportedSyntax
{
    internal static void Collect(string body, int bodyStart, IReadOnlyList<LithoBlock> blocks,
        IReadOnlyList<MdRawRange> opaqueRanges, MdParseContext context, List<MdUnsupportedSyntaxCandidate> result)
    {
        try
        {
            new Scanner(body, bodyStart, context, result).Collect(blocks, opaqueRanges);
        }
        finally
        {
            // Keep already-published candidates in source order when a resource limit interrupts collection.
            result.Sort((left, right) => left.RawSpan.Start.CompareTo(right.RawSpan.Start));
        }
    }

    private sealed class Scanner(string body, int bodyStart, MdParseContext context, List<MdUnsupportedSyntaxCandidate> result)
    {
        private readonly List<MdRawRange> excluded = new();
        private readonly record struct Line(LithoRawText Content, int Start, int End, int Number)
        {
            internal int RawStart => Content.Map.Global(Start);
            internal int RawEnd => Content.Map.GlobalEnd(End);
            internal int IndentColumns
            {
                get
                {
                    var index = Array.BinarySearch(Content.Map.LocalStarts, Start);
                    return index >= 0 && Content.Map.ParagraphIndentColumns is { } columns ? columns[index] : 0;
                }
            }
        }

        internal void Collect(IReadOnlyList<LithoBlock> blocks, IReadOnlyList<MdRawRange> opaqueRanges)
        {
            foreach (var range in opaqueRanges)
            {
                context.Scan();
                excluded.Add(range);
            }
            foreach (var group in Groups(blocks))
                foreach (var block in group)
                    foreach (var inlines in Inlines(block))
                        foreach (var (node, exit) in LithoInline.Walk(inlines, cancellationToken: context.CancellationToken))
                        {
                            context.Scan();
                            if (!exit && node is LithoCodeSpan or LithoMathInline or LithoHtmlInline or LithoAutolink or LithoLink)
                            {
                                if (node is LithoLink link)
                                {
                                    if (link.LinkSyntax?.Kind == MdLinkKind.ReferenceUse
                                        && FootnoteEnd(body, node.Span.Start, node.Span.End) == node.Span.End)
                                        continue;
                                    context.Scan(node.Span.Length);
                                    var label = MdInlineFacts.Text(link.Label, context.CancellationToken);
                                    var start = node.Span.End;
                                    var end = node.Span.Start;
                                    foreach (var segment in label.SourceSegments)
                                    {
                                        context.Scan();
                                        if (segment.RawSpan is { } range)
                                        {
                                            start = Math.Min(start, range.Start);
                                            end = Math.Max(end, range.End);
                                        }
                                    }
                                    if (start <= end && start >= node.Span.Start && end <= node.Span.End)
                                    {
                                        excluded.Add(new MdRawRange(node.Span.Start, start - node.Span.Start));
                                        excluded.Add(new MdRawRange(end, node.Span.End - end));
                                        continue;
                                    }
                                }
                                if (node.Span.Length == 0)
                                    continue;
                                excluded.Add(new MdRawRange(node.Span.Start, node.Span.Length));
                            }
                        }
            context.Scan((long)excluded.Count * 32);
            excluded.Sort((left, right) => left.Start.CompareTo(right.Start));
            var count = 0;
            for (var index = 0; index < excluded.Count; index++)
            {
                context.Scan();
                var range = excluded[index];
                if (count != 0 && excluded[count - 1].End >= range.Start)
                {
                    var previous = excluded[count - 1];
                    excluded[count - 1] = new MdRawRange(previous.Start, Math.Max(previous.End, range.End) - previous.Start);
                }
                else
                    excluded[count++] = range;
            }
            excluded.RemoveRange(count, excluded.Count - count);

            foreach (var group in Groups(blocks))
            {
                var lines = new List<Line>();
                foreach (var block in group)
                {
                    context.Scan();
                    if (block is LithoParagraph { RawContent: { } paragraph })
                    {
                        ScanInline(paragraph);
                        AddLines(paragraph, lines);
                        continue;
                    }
                    ScanLines(lines);
                    lines.Clear();
                    if (block is LithoHeading { RawContent: { } heading })
                        ScanInline(heading);
                    else if (block is LithoTable table)
                    {
                        var text = body.Substring(table.Span.Start, table.Span.Length);
                        context.Scan(text.Length);
                        ScanInline(new LithoRawText(text, LithoLineMap.Contiguous(table.Span.Start, text.Length)));
                    }
                }
                ScanLines(lines);
            }

            // CommonMark consumes these as reference definitions; the legacy profile keeps them in paragraphs.
            foreach (var definition in context.Definitions)
            {
                context.Scan();
                var start = definition.RawSpan.Start;
                while (start < definition.RawSpan.End && (body[start] is ' ' or '\t'))
                {
                    context.Scan();
                    start++;
                }
                if (FootnoteEnd(body, start, definition.RawSpan.End) is var end && end > start
                    && end < body.Length && body[end] == ':')
                    Add(MdUnsupportedSyntaxKind.Footnote, "footnoteDefinition", start, end - start);
            }
        }

        private IEnumerable<IReadOnlyList<LithoBlock>> Groups(IReadOnlyList<LithoBlock> blocks)
        {
            var pending = new Stack<IReadOnlyList<LithoBlock>>();
            pending.Push(blocks);
            while (pending.Count != 0)
            {
                context.Scan();
                var group = pending.Pop();
                yield return group;
                foreach (var block in group)
                {
                    context.Scan();
                    switch (block)
                    {
                        case LithoQuote quote:
                            pending.Push(quote.Children);
                            break;
                        case LithoAdmonition admonition:
                            pending.Push(admonition.Children);
                            break;
                        case LithoDirective directive:
                            pending.Push(directive.Children);
                            break;
                        case LithoFootnoteDefinition footnote:
                            pending.Push(footnote.Children);
                            break;
                        case LithoList list:
                            foreach (var item in list.Items)
                            {
                                context.Scan();
                                pending.Push(item.Children);
                            }
                            break;
                    }
                }
            }
        }

        private static IEnumerable<IReadOnlyList<LithoInline>> Inlines(LithoBlock block)
        {
            if (block is LithoParagraph paragraph)
                yield return paragraph.Inlines;
            else if (block is LithoHeading heading)
                yield return heading.Inlines;
            else if (block is LithoTable table)
                foreach (var row in table.Rows)
                    foreach (var cell in row)
                        yield return cell;
        }

        private void ScanInline(LithoRawText content)
        {
            var text = content.Text;
            var lineStart = 0;
            for (var index = 0; index < text.Length; index++)
            {
                context.Scan();
                if (text[index] is '\r' or '\n')
                    lineStart = index + 1;
                if (text[index] is not ('[' or '{') || Escaped(text, index)
                    || (text[index] == '[' && context.Options.HasFootnotes))
                    continue;
                var end = text[index] == '[' ? FootnoteEnd(text, index, text.Length) : AttributeEnd(text, index);
                if (end <= index)
                    continue;
                var start = content.Map.Global(index);
                var rawEnd = content.Map.GlobalEnd(end);
                if (!Blocked(start, rawEnd))
                {
                    var definition = index == lineStart && end < text.Length && text[end] == ':';
                    Add(text[index] == '[' ? MdUnsupportedSyntaxKind.Footnote : MdUnsupportedSyntaxKind.GenericAttribute,
                        text[index] == '{' ? "genericAttribute" : definition ? "footnoteDefinition" : "footnoteReference",
                        start, rawEnd - start);
                }
                index = end - 1;
            }
        }

        private int FootnoteEnd(string text, int start, int limit)
        {
            if (start + 3 >= limit || text[start] != '[' || text[start + 1] != '^')
                return -1;
            for (var index = start + 2; index < limit && index - start <= LithoParserLimits.MaxReferenceLabelLength + 2; index++)
            {
                context.Scan();
                var ch = text[index];
                if (ch == ']')
                    return index > start + 2 ? index + 1 : -1;
                if (ch is '[' or '\\' || char.IsWhiteSpace(ch))
                    break;
            }
            return -1;
        }

        private int AttributeEnd(string text, int start)
        {
            if (start + 3 >= text.Length || text[start + 1] is not ('#' or '.') || !AsciiAlphaNumeric(text[start + 2]))
                return -1;
            for (var index = start + 3; index < text.Length && index - start <= 200; index++)
            {
                context.Scan();
                var ch = text[index];
                if (ch == '}')
                    return index + 1;
                if (!AsciiAlphaNumeric(ch) && ch is not ('#' or '.' or '-' or '_' or ' ' or '\t' or '=' or '"' or '\'' or ':'))
                    break;
            }
            return -1;
        }

        private static bool AsciiAlphaNumeric(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

        private bool Escaped(string text, int index)
        {
            var count = 0;
            while (index > 0 && text[--index] == '\\')
            {
                context.Scan();
                count++;
            }
            return (count & 1) != 0;
        }

        private bool Blocked(int start, int end)
        {
            var low = 0;
            var high = excluded.Count;
            while (low < high)
            {
                context.Scan();
                var middle = low + (high - low) / 2;
                if (excluded[middle].End <= start)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low < excluded.Count && excluded[low].Start < end;
        }

        private void Add(MdUnsupportedSyntaxKind kind, string reason, int start, int length)
        {
            context.Emit();
            result.Add(new MdUnsupportedSyntaxCandidate(kind, reason, new MdRawRange(checked(bodyStart + start), length)));
        }

        private void AddLines(LithoRawText content, List<Line> lines)
        {
            for (var start = 0; start < content.Text.Length;)
            {
                var end = start;
                while (end < content.Text.Length && content.Text[end] is not ('\r' or '\n'))
                {
                    context.Scan();
                    end++;
                }
                var number = 0;
                if (lines.Count != 0)
                {
                    var previous = lines[lines.Count - 1];
                    number = previous.Number;
                    for (var raw = previous.RawEnd; raw < content.Map.Global(start); raw++)
                    {
                        context.Scan();
                        if (body[raw] == '\r' || (body[raw] == '\n' && (raw == 0 || body[raw - 1] != '\r')))
                            number++;
                    }
                }
                lines.Add(new Line(content, start, end, number));
                start = end + 1;
                if (start < content.Text.Length && content.Text[end] == '\r' && content.Text[start] == '\n')
                    start++;
            }
        }

        private void ScanLines(List<Line> lines)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                context.Scan();
                var line = lines[index];
                var text = line.Content.Text;
                var start = line.Start;
                var end = line.End;
                while (end > start && text[end - 1] is ' ' or '\t')
                {
                    context.Scan();
                    end--;
                }
                if (start == end || line.IndentColumns >= 4 || Blocked(line.RawStart, line.RawStart + 1))
                    continue;
                if (index > 0 && text[start] == ':' && start + 2 < end && text[start + 1] is ' ' or '\t')
                {
                    var previous = lines[index - 1];
                    var description = start + 2;
                    while (description < end && text[description] is ' ' or '\t')
                    {
                        context.Scan();
                        description++;
                    }
                    if (description < end && line.Number - previous.Number <= 2
                        && previous.Start < previous.End && previous.Content.Text[previous.Start] != ':')
                        Add(MdUnsupportedSyntaxKind.DefinitionList, "definitionListMarker", line.RawStart, 1);
                }
                if (!GridBorder(line, end))
                    continue;
                for (var neighbor = Math.Max(0, index - 4); neighbor < Math.Min(lines.Count, index + 5); neighbor++)
                {
                    context.Scan();
                    if (neighbor == index || Math.Abs(lines[neighbor].Number - line.Number) > 4)
                        continue;
                    var other = lines[neighbor];
                    var otherEnd = other.End;
                    while (otherEnd > other.Start && other.Content.Text[otherEnd - 1] is ' ' or '\t')
                    {
                        context.Scan();
                        otherEnd--;
                    }
                    if (otherEnd > other.Start && other.IndentColumns < 4 && !Blocked(other.RawStart, other.RawStart + 1)
                        && (GridBorder(other, otherEnd) || (other.Content.Text[other.Start] == '|' && other.Content.Text[otherEnd - 1] == '|')))
                    {
                        var rawEnd = line.Content.Map.GlobalEnd(end);
                        Add(MdUnsupportedSyntaxKind.GridTable, "gridTableBorder", line.RawStart, rawEnd - line.RawStart);
                        break;
                    }
                }
            }
        }

        private bool GridBorder(Line line, int end)
        {
            var text = line.Content.Text;
            if (end - line.Start < 3 || text[line.Start] != '+' || text[end - 1] != '+')
                return false;
            var pluses = 0;
            var separator = false;
            for (var index = line.Start; index < end; index++)
            {
                context.Scan();
                switch (text[index])
                {
                    case '+':
                        pluses++;
                        break;
                    case '-':
                    case '=':
                        separator = true;
                        break;
                    case ' ':
                    case '\t':
                    case ':':
                    case '|':
                        break;
                    default:
                        return false;
                }
            }
            return pluses >= 2 && separator;
        }
    }
}
