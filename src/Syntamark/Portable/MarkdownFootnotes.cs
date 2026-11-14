using System;
using System.Collections.Generic;
using System.Threading;

namespace Syntamark.Compilation;

internal sealed class MdFootnoteBinding
{
    internal bool Defined { get; set; }
    internal int? Number { get; set; }
    internal int Occurrences { get; set; }
}

internal sealed class MdFootnoteState
{
    private readonly Dictionary<string, MdFootnoteBinding> bindings = new(StringComparer.Ordinal);
    private int numbers;
    internal List<MdDiagnostic> Diagnostics { get; } = new();

    internal (MdFootnoteBinding Binding, bool Duplicate) Define(string normalized, SourceSpan labelSpan,
        MdParseContext context)
    {
        if (!bindings.TryGetValue(normalized, out var binding))
            bindings.Add(normalized, binding = new MdFootnoteBinding());
        var duplicate = binding.Defined;
        binding.Defined = true;
        if (duplicate)
        {
            context.Emit();
            Diagnostics.Add(new MdDiagnostic("LMD030", MdDiagnosticSeverity.Warning,
                "A footnote label is defined more than once; references use the first definition.",
                new MdRawRange(labelSpan.Start, labelSpan.Length), "footnote", "duplicateFootnote"));
        }
        return (binding, duplicate);
    }

    internal LithoFootnoteReference Reference(string label, string normalized, SourceSpan labelSpan,
        SourceSpan span, MdParseContext context)
    {
        if (!bindings.TryGetValue(normalized, out var binding))
            bindings.Add(normalized, binding = new MdFootnoteBinding());
        if (!binding.Defined)
        {
            context.Emit();
            Diagnostics.Add(new MdDiagnostic("LMD031", MdDiagnosticSeverity.Warning,
                "A footnote reference has no definition.", new MdRawRange(labelSpan.Start, labelSpan.Length),
                "footnote", "unresolvedFootnote"));
        }
        else if (!binding.Number.HasValue)
            binding.Number = ++numbers;
        return new LithoFootnoteReference(label, normalized, binding.Number, ++binding.Occurrences, labelSpan, span);
    }
}

internal sealed class MdFootnoteDefinitionFacts(LithoFootnoteDefinition node, int offset)
{
    internal string Label => node.Label;
    internal string NormalizedLabel => node.NormalizedLabel;
    internal int? Number => node.Number;
    internal bool IsDuplicate => node.IsDuplicate;
    internal MdRawRange RawSpan { get; } = new(checked(node.Span.Start + offset), node.Span.Length);
    internal MdRawRange LabelRawSpan { get; } = new(checked(node.LabelSpan.Start + offset), node.LabelSpan.Length);
    internal IReadOnlyList<MdRawRange> ContentRawSpans { get; } = Content(node, offset);

    private static IReadOnlyList<MdRawRange> Content(LithoFootnoteDefinition node, int offset)
    {
        var spans = new MdRawRange[node.Children.Count];
        for (var index = 0; index < spans.Length; index++)
            spans[index] = new MdRawRange(checked(node.Children[index].Span.Start + offset), node.Children[index].Span.Length);
        return MdFreeze.Owned(spans);
    }
}

internal sealed class MdFootnoteReferenceFacts(LithoFootnoteReference node, int offset)
{
    internal string Label => node.Label;
    internal string NormalizedLabel => node.NormalizedLabel;
    internal int? Number => node.Number;
    internal int Occurrence => node.Occurrence;
    internal bool IsResolved => node.Number.HasValue;
    internal MdRawRange RawSpan { get; } = new(checked(node.Span.Start + offset), node.Span.Length);
    internal MdRawRange LabelRawSpan { get; } = new(checked(node.LabelSpan.Start + offset), node.LabelSpan.Length);
}

internal sealed class MdFootnoteDocument(MdAnalysisResult analysis, IReadOnlyList<MdFootnoteDefinitionFacts> definitions,
    IReadOnlyList<MdFootnoteReferenceFacts> references)
{
    internal MdAnalysisResult Analysis { get; } = analysis;
    internal IReadOnlyList<MdFootnoteDefinitionFacts> Definitions { get; } = MdFreeze.Copy(definitions);
    internal IReadOnlyList<MdFootnoteReferenceFacts> References { get; } = MdFreeze.Copy(references);
}

internal static class MdFootnotes
{
    internal static bool TryMarker(string text, int start, MdParseContext context, out string label, out int end)
    {
        label = string.Empty;
        end = start;
        if (start + 3 >= text.Length || text[start] != '[' || text[start + 1] != '^')
            return false;
        for (var cursor = start + 2; cursor < text.Length && cursor - start <= LithoParserLimits.MaxReferenceLabelLength + 2; cursor++)
        {
            context.Scan();
            var ch = text[cursor];
            if (ch is '\r' or '\n' or '[')
                return false;
            if (ch == '\\' && cursor + 1 < text.Length && text[cursor + 1] is not ('\r' or '\n'))
            {
                cursor++;
                continue;
            }
            if (ch != ']')
                continue;
            label = text.Substring(start + 2, cursor - start - 2);
            if (label.Trim().Length == 0)
                return false;
            end = cursor + 1;
            return true;
        }
        return false;
    }

    internal static MdFootnoteDocument Analyze(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken)
    {
        var analysis = MdParser.AnalyzeWithBlocks(raw, MdAnalysisParts.AllFacts, scopeId, sourceId,
            sourceVersion, options, cancellationToken);
        return FromAnalysis(analysis, cancellationToken);
    }

    internal static MdFootnoteDocument FromAnalysis(MdAnalysisResult analysis, CancellationToken cancellationToken)
    {
        if (analysis.Blocks is null)
            return new MdFootnoteDocument(analysis, MdFreeze.Empty<MdFootnoteDefinitionFacts>(), MdFreeze.Empty<MdFootnoteReferenceFacts>());
        var definitions = new List<MdFootnoteDefinitionFacts>();
        var references = new List<MdFootnoteReferenceFacts>();
        var offset = analysis.BodySpan?.Start ?? 0;
        var pending = new Stack<LithoBlock>();
        for (var index = analysis.Blocks.Count - 1; index >= 0; index--)
            pending.Push(analysis.Blocks[index]);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = pending.Pop();
            IReadOnlyList<LithoBlock>? children = block switch
            {
                LithoFootnoteDefinition definition => definition.Children,
                LithoQuote quote => quote.Children,
                LithoDirective directive => directive.Children,
                LithoAdmonition admonition => admonition.Children,
                _ => null,
            };
            if (block is LithoFootnoteDefinition footnote)
                definitions.Add(new MdFootnoteDefinitionFacts(footnote, offset));
            if (block is LithoList list)
                for (var item = list.Items.Count - 1; item >= 0; item--)
                    for (var index = list.Items[item].Children.Count - 1; index >= 0; index--)
                        pending.Push(list.Items[item].Children[index]);
            if (children is not null)
                for (var index = children.Count - 1; index >= 0; index--)
                    pending.Push(children[index]);
            if (block is LithoParagraph paragraph)
                Collect(paragraph.Inlines);
            else if (block is LithoHeading heading)
                Collect(heading.Inlines);
            else if (block is LithoTable table)
                foreach (var row in table.Rows)
                    foreach (var cell in row)
                        Collect(cell);
        }
        references.Sort(static (left, right) => left.RawSpan.Start.CompareTo(right.RawSpan.Start));
        return new MdFootnoteDocument(analysis, definitions, references);

        void Collect(IReadOnlyList<LithoInline> inlines)
        {
            foreach (var (node, exit) in LithoInline.Walk(inlines, cancellationToken: cancellationToken))
                if (!exit && node is LithoFootnoteReference reference)
                    references.Add(new MdFootnoteReferenceFacts(reference, offset));
        }
    }
}

internal static partial class LithoBlockParser
{
    private sealed partial class Parser
    {
        private bool IsFootnoteStart(Line line)
        {
            if (context?.Options.HasFootnotes != true)
                return false;
            var indent = CountIndent(line.Text, 0, context);
            return indent.Columns < 4
                && MdFootnotes.TryMarker(line.Text, indent.Chars, context, out _, out var end)
                && end < line.Text.Length && line.Text[end] == ':';
        }

        private bool TryParseFootnote(int index, int to, int depth, out LithoFootnoteDefinition? footnote, out int next)
        {
            footnote = null;
            next = index;
            var first = Lines[index];
            if (!IsFootnoteStart(first))
                return false;
            var activeContext = context!;
            var indent = CountIndent(first.Text, 0, context);
            MdFootnotes.TryMarker(first.Text, indent.Chars, activeContext, out var label, out var markerEnd);
            var labelSpan = new SourceSpan(first.Offset + indent.Chars + 2, markerEnd - indent.Chars - 3);
            var normalized = NormalizeLabel(LithoInlineParser.DecodeEscapesAndEntities(label, context), context);
            var binding = Resolve ? activeContext.Footnotes.Define(normalized, labelSpan, activeContext)
                : (Binding: new MdFootnoteBinding(), Duplicate: false);
            if (Resolve)
                activeContext.Emit();
            var contentStart = markerEnd + 1;
            while (contentStart < first.Text.Length && first.Text[contentStart] is ' ' or '\t')
            {
                activeContext.Scan();
                contentStart++;
            }
            var content = new List<Line>
            {
                new(first.Text[contentStart..], first.Offset + contentStart, first.Break),
            };
            var cursor = index + 1;
            var last = index;
            while (cursor < to)
            {
                activeContext.Scan();
                var line = Lines[cursor];
                if (IsBlank(line.Text, context))
                {
                    var probe = cursor + 1;
                    while (probe < to && IsBlank(Lines[probe].Text, context))
                    {
                        activeContext.Scan();
                        probe++;
                    }
                    if (probe >= to || CountIndent(Lines[probe].Text, 0, context).Columns < 4)
                        break;
                    while (cursor < probe)
                    {
                        activeContext.Scan();
                        var blank = Lines[cursor++];
                        content.Add(new Line(string.Empty, blank.Offset + blank.Text.Length, blank.Break));
                    }
                    continue;
                }
                if (CountIndent(line.Text, 0, context).Columns < 4)
                    break;
                var stripped = StripColumns(line.Text, 4, context);
                content.Add(new Line(stripped.Text, line.Offset + stripped.Chars, line.Break));
                last = cursor++;
            }
            var children = new Parser(content, cancellationToken, resolveInlines: Resolve, references: References,
                context: context).ParseRange(0, content.Count, depth + 1);
            var end = Lines[last].Offset + Lines[last].Text.Length;
            footnote = new LithoFootnoteDefinition(label, normalized, labelSpan, children,
                new SourceSpan(first.Offset + indent.Chars, end - first.Offset - indent.Chars))
            { Binding = binding.Binding, IsDuplicate = binding.Duplicate };
            next = cursor;
            return true;
        }
    }
}
