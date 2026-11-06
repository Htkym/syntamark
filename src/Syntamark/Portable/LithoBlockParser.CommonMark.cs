using System;
using System.Collections.Generic;
using System.Text;

namespace Syntamark.Compilation;

internal static partial class LithoBlockParser
{
    private sealed partial class Parser
    {
        private bool TryParseCommonMarkReference(int index, int to, bool store, out int next)
        {
            next = index;
            var first = Lines[index];
            var indent = CountIndent(first.Text, 0, context);
            if (indent.Columns >= 4 || indent.Chars >= first.Text.Length || first.Text[indent.Chars] != '[')
                return false;
            var buffer = new StringBuilder();
            var localStarts = new List<int>();
            var bodyStarts = new List<int>();
            var candidateEnd = CommonMarkReferenceEnd(index, to, indent.Chars);
            for (var lineIndex = index; lineIndex < candidateEnd; lineIndex++)
            {
                context?.Scan(8);
                var line = Lines[lineIndex];
                if (IsBlank(line.Text, context))
                    break;
                localStarts.Add(buffer.Length);
                var prefix = lineIndex == index ? indent.Chars : 0;
                bodyStarts.Add(line.Offset + prefix - line.Padding);
                buffer.Append(line.Text, prefix, line.Text.Length - prefix);
                if (line.Break.Length != 0)
                    buffer.Append(line.Break);
            }
            var text = buffer.ToString();
            context?.Scan(text.Length);
            var pos = 1;
            var nonWhitespace = false;
            while (pos < text.Length && text[pos] != ']')
            {
                context?.Scan(8);
                if (pos > LithoParserLimits.MaxReferenceLabelLength * 2 || text[pos] == '[')
                    return false;
                if (text[pos] == '\\' && pos + 1 < text.Length && LithoCharacters.IsAsciiPunctuation(text[pos + 1]))
                {
                    nonWhitespace = true;
                    pos += 2;
                }
                else
                {
                    if (text[pos] is not (' ' or '\t' or '\r' or '\n'))
                        nonWhitespace = true;
                    pos++;
                }
            }
            if (!nonWhitespace || pos > LithoParserLimits.MaxReferenceLabelLength * 2 + 1 || pos + 1 >= text.Length
                || text[pos] != ']' || text[pos + 1] != ':')
                return false;
            var labelEnd = pos;
            var rawLabel = text.Substring(1, labelEnd - 1);
            if (!LithoCharacters.IsCommonMarkReferenceLabel(rawLabel, context))
                return false;
            pos += 2;
            SkipDefinitionWhitespace(text, ref pos, allowBreak: true);
            var targetStart = pos;
            int targetEnd;
            if (pos < text.Length && text[pos] == '<')
            {
                targetStart = ++pos;
                while (pos < text.Length && text[pos] != '>')
                {
                    context?.Scan(8);
                    if (text[pos] is '\r' or '\n' or '<')
                        return false;
                    if (text[pos] == '\\' && pos + 1 < text.Length && LithoCharacters.IsAsciiPunctuation(text[pos + 1]))
                        pos++;
                    pos++;
                }
                if (pos == text.Length)
                    return false;
                targetEnd = pos++;
            }
            else
            {
                var depth = 0;
                while (pos < text.Length && text[pos] is not (' ' or '\t' or '\r' or '\n'))
                {
                    context?.Scan(8);
                    var ch = text[pos];
                    if (ch <= 0x20 || ch == 0x7f)
                        return false;
                    if (ch == '\\' && pos + 1 < text.Length && LithoCharacters.IsAsciiPunctuation(text[pos + 1]))
                    {
                        pos += 2;
                        continue;
                    }
                    if (ch == '(')
                        depth++;
                    else if (ch == ')' && --depth < 0)
                        return false;
                    pos++;
                }
                if (depth != 0 || pos == targetStart)
                    return false;
                targetEnd = pos;
            }
            var destinationEnd = pos;
            var destinationLine = DefinitionLine(localStarts, Math.Max(0, destinationEnd - 1));
            SkipDefinitionWhitespace(text, ref pos, allowBreak: false);
            var separated = pos > destinationEnd;
            var titleNextLine = pos < text.Length && text[pos] is '\r' or '\n';
            if (titleNextLine)
            {
                SkipDefinitionWhitespace(text, ref pos, allowBreak: true);
                separated = true;
            }
            string? title = null;
            var consumed = destinationLine + 1;
            if (pos < text.Length && text[pos] is '"' or '\'' or '(' && separated)
            {
                var titleStart = pos;
                if (!TryParseCommonMarkTitle(text, ref pos, out title))
                {
                    if (!titleNextLine)
                        return false;
                    title = null;
                }
                else
                {
                    var titleEnd = pos;
                    SkipDefinitionWhitespace(text, ref pos, allowBreak: false);
                    if (pos < text.Length && text[pos] is not ('\r' or '\n'))
                    {
                        if (!titleNextLine)
                            return false;
                        title = null;
                    }
                    else
                        consumed = DefinitionLine(localStarts, Math.Max(titleStart, titleEnd - 1)) + 1;
                }
            }
            else if (!titleNextLine && pos < text.Length && text[pos] is not ('\r' or '\n'))
                return false;
            var url = text.Substring(targetStart, targetEnd - targetStart);
            var normalizedUrl = LithoInlineParser.DecodeEscapesAndEntities(url, context);
            var normalizedTitle = title is null ? null : LithoInlineParser.DecodeEscapesAndEntities(
                title.Replace("\r\n", "\n").Replace('\r', '\n'), context);
            var key = NormalizeLabel(rawLabel, context);
            if (store && !References.ContainsKey(key))
                References.Add(key, new LithoReference(normalizedUrl, normalizedTitle));
            if (store && context is not null)
            {
                var last = Lines[index + consumed - 1];
                var map = new LithoLineMap(localStarts.ToArray(), bodyStarts.ToArray(), text.Length,
                    RawEnd(Lines[index + localStarts.Count - 1]) + Lines[index + localStarts.Count - 1].Break.Length);
                var label = DefinitionLabelProjection(rawLabel, labelEnd, index, localStarts, bodyStarts);
                context.Emit(1 + label.SourceSegments.Count);
                context.Definitions.Add(new MdLink(MdLinkKind.ReferenceDefinition, MdLinkResolution.Inline, false,
                    new MdRawRange(first.Offset, RawEnd(last) - first.Offset), label, normalizedUrl,
                    normalizedTitle, new MdRawRange(map.Global(targetStart), url.Length), rawLabel));
            }
            next = index + consumed;
            return true;
        }

        private static int DefinitionLine(List<int> starts, int position)
        {
            var found = starts.BinarySearch(position);
            return found < 0 ? ~found - 1 : found;
        }

        private MdTextProjection DefinitionLabelProjection(string label, int labelEnd, int firstLine,
            List<int> localStarts, List<int> bodyStarts)
        {
            var segments = new List<MdSourceSegment>();
            for (var index = 0; index < localStarts.Count && localStarts[index] < labelEnd; index++)
            {
                context?.Scan(8);
                var line = Lines[firstLine + index];
                var from = Math.Max(1, localStarts[index]);
                var end = Math.Min(labelEnd, index + 1 < localStarts.Count ? localStarts[index + 1] : labelEnd);
                var paddingEnd = localStarts[index] + (index == 0 ? 0 : line.Padding);
                if (from < end && from < paddingEnd && line.ExpandedPrefix is { } prefix)
                {
                    var count = Math.Min(end, paddingEnd) - from;
                    segments.Add(new MdSourceSegment(new MdRawRange(from - 1, count),
                        new MdRawRange(prefix.Start, prefix.Length), MdSegmentKind.Atomic));
                    from += count;
                }
                if (from < end)
                    segments.Add(new MdSourceSegment(new MdRawRange(from - 1, end - from),
                        new MdRawRange(bodyStarts[index] + from - localStarts[index], end - from), MdSegmentKind.Linear));
            }
            return new MdTextProjection(label, segments);
        }

        // Bound each candidate before allocating joined text. In particular a run
        // of N one-line definitions must not repeatedly join the remaining N lines.
        private int CommonMarkReferenceEnd(int index, int to, int firstIndent)
        {
            var state = 0; // label, colon, before destination, angle, bare, after destination, title
            var labelUnits = 0;
            var beforeDestinationBreaks = 0;
            var closer = '\0';
            for (var lineIndex = index; lineIndex < to; lineIndex++)
            {
                var line = Lines[lineIndex].Text;
                if (IsBlank(line, context))
                    return lineIndex;
                for (var column = lineIndex == index ? firstIndent + 1 : 0; column < line.Length; column++)
                {
                    context?.Scan(8);
                    var ch = line[column];
                    if (state == 0)
                    {
                        if (++labelUnits > LithoParserLimits.MaxReferenceLabelLength * 2)
                            return lineIndex + 1;
                        if (ch == '\\' && column + 1 < line.Length && LithoCharacters.IsAsciiPunctuation(line[column + 1]))
                        {
                            column++;
                            labelUnits++;
                            continue;
                        }
                        if (ch == '[')
                            return lineIndex + 1;
                        if (ch == ']')
                            state = 1;
                    }
                    else if (state == 1)
                    {
                        if (ch != ':')
                            return lineIndex + 1;
                        state = 2;
                    }
                    else if (state == 2)
                    {
                        if (ch is ' ' or '\t')
                            continue;
                        state = ch == '<' ? 3 : 4;
                    }
                    else if (state == 3)
                    {
                        if (ch == '\\' && column + 1 < line.Length && LithoCharacters.IsAsciiPunctuation(line[column + 1]))
                            column++;
                        else if (ch == '>')
                            state = 5;
                    }
                    else if (state == 4)
                    {
                        if (ch is ' ' or '\t')
                            state = 5;
                        else if (ch == '\\' && column + 1 < line.Length && LithoCharacters.IsAsciiPunctuation(line[column + 1]))
                            column++;
                    }
                    else if (state == 5)
                    {
                        if (ch is ' ' or '\t')
                            continue;
                        if (ch is not ('"' or '\'' or '('))
                            return lineIndex + 1;
                        closer = ch == '(' ? ')' : ch;
                        state = 6;
                    }
                    else if (ch == closer)
                        return lineIndex + 1;
                    else if (ch == '\\' && column + 1 < line.Length && LithoCharacters.IsAsciiPunctuation(line[column + 1]))
                        column++;
                }
                if (state is 1 or 3)
                    return lineIndex + 1;
                if (state == 2 && ++beforeDestinationBreaks > 1)
                    return lineIndex + 1;
                if (state == 4)
                    state = 5;
                if (state == 5)
                {
                    if (lineIndex + 1 == to)
                        return lineIndex + 1;
                    var following = Lines[lineIndex + 1].Text.TrimStart(' ', '\t');
                    if (following.Length == 0 || following[0] is not ('"' or '\'' or '('))
                        return lineIndex + 1;
                }
            }
            return to;
        }

        private void SkipDefinitionWhitespace(string text, ref int pos, bool allowBreak)
        {
            var lineBreak = false;
            while (pos < text.Length)
            {
                context?.Scan(8);
                if (text[pos] is ' ' or '\t')
                {
                    pos++;
                    continue;
                }
                if (allowBreak && !lineBreak && text[pos] is '\r' or '\n')
                {
                    lineBreak = true;
                    if (text[pos++] == '\r' && pos < text.Length && text[pos] == '\n')
                        pos++;
                    continue;
                }
                break;
            }
        }

        private bool TryParseCommonMarkTitle(string text, ref int pos, out string? title)
        {
            var opener = text[pos++];
            var closer = opener == '(' ? ')' : opener;
            var start = pos;
            while (pos < text.Length)
            {
                context?.Scan(8);
                if (text[pos] == closer)
                {
                    title = text.Substring(start, pos - start);
                    pos++;
                    return true;
                }
                if (opener == '(' && text[pos] == '(')
                    break;
                if (text[pos] == '\\' && pos + 1 < text.Length && LithoCharacters.IsAsciiPunctuation(text[pos + 1]))
                    pos++;
                pos++;
            }
            title = null;
            return false;
        }
    }
}
