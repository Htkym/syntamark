using System;
using System.Collections.Generic;
using System.Threading;

namespace Syntamark.Compilation;

internal sealed record MdTableCellSyntax(int Row, int Column, MdRawRange? RawSpan, MdTextProjection Content);

internal static class MdSyntaxDetails
{
    internal static MdRawRange? RawContent(LithoRawText? source, int bodyStart)
    {
        if (source is null)
            return null;
        var from = source.Map.Global(0);
        var end = source.Map.GlobalEnd(source.Text.Length);
        return end - from == source.Text.Length ? new MdRawRange(checked(from + bodyStart), end - from) : null;
    }

    internal static IReadOnlyList<MdTableCellSyntax> TableCells(LithoTable table, int bodyStart,
        CancellationToken cancellationToken, MdParseContext? context)
    {
        var result = new List<MdTableCellSyntax>();
        for (var row = 0; row < table.Rows.Count; row++)
            for (var column = 0; column < table.Rows[row].Count; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context?.Emit();
                var span = table.CellSpans is { } spans && row < spans.Count && column < spans[row].Count
                    ? spans[row][column] : null;
                result.Add(new MdTableCellSyntax(row, column,
                    span.HasValue ? new MdRawRange(checked(span.Value.Start + bodyStart), span.Value.Length) : null,
                    MdSourceMapping.Shift(MdInlineFacts.Text(table.Rows[row][column], cancellationToken), bodyStart)));
            }
        return result;
    }

    internal static MdRawRange? LinkLabel(string? source, MdRawRange? envelope, bool reference,
        MdParseContext? context)
    {
        if (source is null || envelope is not { } span)
            return null;
        var cursor = span.Start;
        if (cursor < span.End && source[cursor] == '!')
            cursor++;
        if (cursor >= span.End || source[cursor] != '[')
            return null;
        var first = Bracket(cursor);
        if (first is null)
            return null;
        if (!reference)
            return first;
        cursor = first.Value.End + 1;
        while (cursor < span.End && source[cursor] is ' ' or '\t')
            cursor++;
        if (cursor < span.End && source[cursor] == '[')
        {
            var second = Bracket(cursor);
            return second is { Length: > 0 } ? second : first;
        }
        return first;

        MdRawRange? Bracket(int opening)
        {
            var depth = 1;
            for (var at = opening + 1; at < span.End; at++)
            {
                context?.Scan();
                var ch = source[at];
                // Stripped container prefixes can split a multiline label's raw representation.
                if (ch is '\r' or '\n')
                    return null;
                if (ch == '\\' && at + 1 < span.End)
                {
                    at++;
                    continue;
                }
                if (ch == '`')
                {
                    var openingRun = at;
                    while (at + 1 < span.End && source[at + 1] == '`')
                    {
                        context?.Scan();
                        at++;
                    }
                    var width = at - openingRun + 1;
                    for (var close = at + 1; close < span.End; close++)
                    {
                        context?.Scan();
                        if (source[close] != '`')
                            continue;
                        var end = close;
                        while (end < span.End && source[end] == '`')
                        {
                            context?.Scan();
                            end++;
                        }
                        if (end - close == width)
                        {
                            at = end - 1;
                            break;
                        }
                        close = end - 1;
                    }
                    continue;
                }
                if (ch == '[')
                    depth++;
                else if (ch == ']' && --depth == 0)
                    return new MdRawRange(opening + 1, at - opening - 1);
            }
            return null;
        }
    }
}
