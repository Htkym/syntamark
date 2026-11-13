using System;
using System.Collections.Generic;
using System.Text;

namespace Syntamark.Compilation;

internal sealed record MdDirectiveAttribute(string Name, MdRawRange RawSpan, MdRawRange NameRawSpan,
    MdRawRange? ValueRawSpan, MdRawRange? OpeningQuoteRawSpan, MdRawRange? ClosingQuoteRawSpan,
    MdTextProjection? Value);

internal sealed class MdDirectiveSyntax
{
    private MdDirectiveSyntax(string name, MdRawRange nameRawSpan, MdRawRange openingMarkerRawSpan,
        MdRawRange headerRawSpan, MdRawRange contentRawSpan, MdRawRange? closingMarkerRawSpan,
        IReadOnlyList<MdDirectiveAttribute> attributes, IReadOnlyList<MdDiagnostic> diagnostics)
    {
        Name = name;
        NameRawSpan = nameRawSpan;
        OpeningMarkerRawSpan = openingMarkerRawSpan;
        HeaderRawSpan = headerRawSpan;
        ContentRawSpan = contentRawSpan;
        ClosingMarkerRawSpan = closingMarkerRawSpan;
        Attributes = MdFreeze.Copy(attributes);
        Diagnostics = MdFreeze.Copy(diagnostics);
    }

    internal string Name { get; }
    internal MdRawRange NameRawSpan { get; }
    internal MdRawRange OpeningMarkerRawSpan { get; }
    internal MdRawRange HeaderRawSpan { get; }
    internal MdRawRange ContentRawSpan { get; }
    internal MdRawRange? ClosingMarkerRawSpan { get; }
    internal IReadOnlyList<MdDirectiveAttribute> Attributes { get; }
    internal IReadOnlyList<MdDiagnostic> Diagnostics { get; }
    internal bool IsComplete => ClosingMarkerRawSpan.HasValue && Diagnostics.Count == 0;

    internal static MdDirectiveSyntax? Parse(string? source, MdRawRange? envelope, string expectedName,
        MdParseContext? context)
    {
        if (source is null || envelope is not { } raw)
            return null;
        var cursor = raw.Start;
        while (cursor < raw.End && source[cursor] is ' ' or '\t')
            cursor++;
        var opening = cursor;
        while (cursor < raw.End && source[cursor] == ':')
            cursor++;
        var run = cursor - opening;
        if (run < 3)
            return null; // GitHub alert quotes are not directive fences.
        while (cursor < raw.End && source[cursor] is ' ' or '\t')
            cursor++;
        var nameStart = cursor;
        while (cursor < raw.End && NameCharacter(source[cursor]))
            cursor++;
        if (cursor - nameStart != expectedName.Length
            || string.CompareOrdinal(source, nameStart, expectedName, 0, expectedName.Length) != 0)
            return null;
        var nameSpan = new MdRawRange(nameStart, cursor - nameStart);
        var headerEnd = cursor;
        while (headerEnd < raw.End && source[headerEnd] is not ('\r' or '\n'))
        {
            context?.Scan();
            headerEnd++;
        }
        var contentStart = headerEnd;
        if (contentStart < raw.End && source[contentStart] == '\r')
            contentStart++;
        if (contentStart < raw.End && source[contentStart] == '\n')
            contentStart++;
        var lastLine = raw.End;
        while (lastLine > contentStart && source[lastLine - 1] is not ('\r' or '\n'))
        {
            context?.Scan();
            lastLine--;
        }
        var close = lastLine;
        while (close < raw.End && source[close] is ' ' or '\t' or '>')
            close++;
        var closeStart = close;
        while (close < raw.End && source[close] == ':')
            close++;
        var closeEnd = close;
        while (close < raw.End && source[close] is ' ' or '\t')
            close++;
        MdRawRange? closing = lastLine >= contentStart && closeEnd - closeStart >= run && close == raw.End
            ? new MdRawRange(closeStart, closeEnd - closeStart) : null;
        var contentEnd = closing.HasValue ? lastLine : raw.End;
        var attributes = new List<MdDirectiveAttribute>();
        var diagnostics = new List<MdDiagnostic>();
        if (context?.Options.ProfileId == "syntamark-docs/2")
        {
            if (expectedName.Length == 0)
                Diagnostic("LMD020", "A directive name is required.", nameSpan, "missingName");
            if (!closing.HasValue)
                Diagnostic("LMD024", "The directive has no closing fence.", new MdRawRange(opening, run), "unterminated");
            Attributes();
        }
        context?.Emit();
        return new MdDirectiveSyntax(expectedName, nameSpan, new MdRawRange(opening, run),
            new MdRawRange(raw.Start, headerEnd - raw.Start),
            new MdRawRange(contentStart, Math.Max(0, contentEnd - contentStart)), closing, attributes, diagnostics);

        void Diagnostic(string id, string message, MdRawRange span, string reason)
        {
            context?.Emit();
            diagnostics.Add(new MdDiagnostic(id, MdDiagnosticSeverity.Warning, message, span, "directive", reason));
        }

        void Attributes()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (cursor < headerEnd)
            {
                context?.Scan();
                while (cursor < headerEnd && source[cursor] is ' ' or '\t')
                    cursor++;
                if (cursor == headerEnd)
                    break;
                var start = cursor;
                while (cursor < headerEnd && AttributeCharacter(source[cursor]))
                    cursor++;
                if (cursor == start)
                {
                    Diagnostic("LMD020", "Expected an attribute name.", new MdRawRange(cursor++, 1), "attributeName");
                    continue;
                }
                var name = source.Substring(start, cursor - start);
                var attributeName = new MdRawRange(start, cursor - start);
                if (!names.Add(name))
                    Diagnostic("LMD023", "A directive attribute is duplicated.", attributeName, "duplicateAttribute");
                while (cursor < headerEnd && source[cursor] is ' ' or '\t')
                    cursor++;
                MdRawRange? valueSpan = null;
                MdRawRange? openingQuote = null;
                MdRawRange? closingQuote = null;
                MdTextProjection? value = null;
                if (cursor < headerEnd && source[cursor] == '=')
                {
                    cursor++;
                    while (cursor < headerEnd && source[cursor] is ' ' or '\t')
                        cursor++;
                    if (cursor == headerEnd)
                        Diagnostic("LMD021", "A directive attribute value is required after '='.",
                            new MdRawRange(cursor, 0), "missingValue");
                    else
                    {
                        var quote = source[cursor] is '\'' or '"' ? source[cursor] : '\0';
                        if (quote != '\0')
                            openingQuote = new MdRawRange(cursor++, 1);
                        var valueStart = cursor;
                        while (cursor < headerEnd)
                        {
                            context?.Scan();
                            if (quote == '\0' ? source[cursor] is ' ' or '\t' : source[cursor] == quote)
                                break;
                            if (source[cursor] == '\\' && quote != '\0' && cursor + 1 < headerEnd
                                && (source[cursor + 1] == quote || source[cursor + 1] == '\\'))
                                cursor++;
                            cursor++;
                        }
                        valueSpan = new MdRawRange(valueStart, cursor - valueStart);
                        value = Decode(valueSpan.Value, quote);
                        if (quote != '\0')
                        {
                            if (cursor < headerEnd)
                                closingQuote = new MdRawRange(cursor++, 1);
                            else
                                Diagnostic("LMD022", "A directive attribute quote is not closed.",
                                    openingQuote!.Value, "unclosedQuote");
                        }
                    }
                }
                else
                    Diagnostic("LMD021", "A directive attribute requires '=' and a value.", attributeName, "missingValue");
                context?.Emit();
                attributes.Add(new MdDirectiveAttribute(name, new MdRawRange(start, cursor - start),
                    attributeName, valueSpan, openingQuote, closingQuote, value));
            }
        }

        MdTextProjection Decode(MdRawRange span, char quote)
        {
            var text = new StringBuilder();
            var pieces = new List<MdSourceSegment>();
            var at = span.Start;
            while (at < span.End)
            {
                context?.Scan();
                var start = at;
                if (source[at] == '\\' && quote != '\0' && at + 1 < span.End
                    && (source[at + 1] == quote || source[at + 1] == '\\'))
                {
                    context?.Emit();
                    pieces.Add(new MdSourceSegment(new MdRawRange(text.Length, 1), new MdRawRange(at, 2), MdSegmentKind.Atomic));
                    text.Append(source[at + 1]);
                    at += 2;
                }
                else
                {
                    do
                    {
                        context?.Scan();
                        at++;
                    }
                    while (at < span.End && !(source[at] == '\\' && quote != '\0' && at + 1 < span.End
                        && (source[at + 1] == quote || source[at + 1] == '\\')));
                    context?.Emit();
                    pieces.Add(new MdSourceSegment(new MdRawRange(text.Length, at - start),
                        new MdRawRange(start, at - start), MdSegmentKind.Linear));
                    text.Append(source, start, at - start);
                }
            }
            return new MdTextProjection(text.ToString(), pieces);
        }
    }

    private static bool NameCharacter(char ch) => LithoCharacters.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';
    private static bool AttributeCharacter(char ch) => NameCharacter(ch) || ch is ':' or '.';
}
