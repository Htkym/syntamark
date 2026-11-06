using System;
using System.Collections.Generic;
using System.Threading;

namespace Syntamark.Compilation;

internal enum MdSyntaxKind
{
    Unknown, Document, Paragraph, Heading, BlockQuote, OrderedList, UnorderedList, ListItem,
    ThematicBreak, FencedCodeBlock, IndentedCodeBlock, HtmlBlock, Text, Emphasis, StrongEmphasis,
    CodeSpan, Link, Image, Autolink, HtmlInline, SoftLineBreak, HardLineBreak,
    Table, Strikethrough, Admonition, Directive, MathBlock, MathInline,
}

internal sealed class MdSyntaxNode
{
    internal MdSyntaxNode(MdSyntaxKind kind, MdRawRange? rawSpan, IReadOnlyList<MdSyntaxNode> children,
        string? text = null, int? headingLevel = null, int? orderedListStart = null, bool? isTightList = null,
        bool? taskChecked = null, string? codeInfo = null, string? target = null, MdRawRange? targetRawSpan = null,
        string? title = null, string? referenceLabel = null)
    {
        Kind = kind;
        RawSpan = rawSpan;
        Children = MdFreeze.Copy(children);
        Text = text;
        HeadingLevel = headingLevel;
        OrderedListStart = orderedListStart;
        IsTightList = isTightList;
        TaskChecked = taskChecked;
        CodeInfo = codeInfo;
        Target = target;
        TargetRawSpan = targetRawSpan;
        Title = title;
        ReferenceLabel = referenceLabel;
    }
    internal MdSyntaxKind Kind { get; }
    internal MdRawRange? RawSpan { get; }
    internal IReadOnlyList<MdSyntaxNode> Children { get; }
    internal string? Text { get; }
    internal int? HeadingLevel { get; }
    internal int? OrderedListStart { get; }
    internal bool? IsTightList { get; }
    internal bool? TaskChecked { get; }
    internal string? CodeInfo { get; }
    internal string? Target { get; }
    internal MdRawRange? TargetRawSpan { get; }
    internal string? Title { get; }
    internal string? ReferenceLabel { get; }
    internal MdSyntaxNode WithoutRawSpan() => new(Kind, null, Children, Text, HeadingLevel, OrderedListStart,
        IsTightList, TaskChecked, CodeInfo, Target, TargetRawSpan, Title, ReferenceLabel);
    internal IEnumerable<MdSyntaxNode> DescendantsAndSelf(CancellationToken cancellationToken = default)
    {
        var pending = new Stack<MdSyntaxNode>();
        pending.Push(this);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = pending.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }
    }
}

internal sealed class MdSyntaxDocument(MdDocumentFacts facts, MdSyntaxNode? root)
{
    internal MdDocumentFacts Facts { get; } = facts;
    internal MdSyntaxNode? Root { get; } = root;
}

internal static class MdSyntaxFactory
{
    internal static MdSyntaxNode Create(IReadOnlyList<LithoBlock> blocks, int rawLength, int bodyStart,
        CancellationToken cancellationToken, IReadOnlyList<MdLink>? links = null, MdParseContext? context = null,
        string? rawText = null)
    {
        var linkFacts = new Dictionary<MdRawRange, MdLink>();
        if (links is not null)
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context?.Scan();
                if (link.Kind != MdLinkKind.ReferenceDefinition)
                    linkFacts[link.RawSpan] = link;
            }
        var pending = new Stack<Frame>();
        pending.Push(new Frame(null, blocks));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context?.Scan();
            var frame = pending.Peek();
            if (frame.Index < frame.SourceChildren.Count)
            {
                var child = frame.SourceChildren[frame.Index++];
                pending.Push(new Frame(child, Children(child, cancellationToken, context)));
                continue;
            }
            pending.Pop();
            context?.Emit();
            var node = frame.Source is null
                ? new MdSyntaxNode(MdSyntaxKind.Document, new MdRawRange(0, rawLength), frame.Children)
                : Convert(frame.Source, frame.Children, bodyStart, rawLength, linkFacts, cancellationToken, context, rawText);
            if (pending.Count == 0)
                return node;
            pending.Peek().Children.Add(node);
        }
        throw new InvalidOperationException("Syntax root was not constructed.");
    }

    private sealed class Frame(LithoNode? source, IReadOnlyList<LithoNode> children)
    {
        internal LithoNode? Source { get; } = source;
        internal IReadOnlyList<LithoNode> SourceChildren { get; } = children;
        internal List<MdSyntaxNode> Children { get; } = new();
        internal int Index { get; set; }
    }

    private static IReadOnlyList<LithoNode> Children(LithoNode node, CancellationToken cancellationToken, MdParseContext? context) => node switch
    {
        LithoParagraph paragraph => paragraph.Inlines,
        LithoHeading heading => heading.Inlines,
        LithoQuote quote => quote.Children,
        LithoList list => list.Items,
        LithoItem item => item.Children,
        LithoEmphasis emphasis => emphasis.Children,
        LithoStrike strike => strike.Children,
        LithoLink link => link.Label,
        LithoAdmonition admonition => admonition.Children,
        LithoDirective directive => directive.Children,
        LithoTable table => TableChildren(table, cancellationToken, context),
        _ => Array.Empty<LithoNode>(),
    };

    private static IReadOnlyList<LithoNode> TableChildren(LithoTable table, CancellationToken cancellationToken, MdParseContext? context)
    {
        var result = new List<LithoNode>();
        foreach (var row in table.Rows)
            foreach (var cell in row)
                foreach (var inline in cell)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context?.Scan();
                    result.Add(inline);
                }
        return result;
    }

    private static MdSyntaxNode Convert(LithoNode node, List<MdSyntaxNode> children,
        int bodyStart, int rawLength, IReadOnlyDictionary<MdRawRange, MdLink> links,
        CancellationToken cancellationToken, MdParseContext? context, string? rawText)
    {
        MdRawRange? Range(SourceSpan? span)
        {
            if (!span.HasValue || span.Value.Start < 0 || span.Value.Length < 0
                || (long)span.Value.Start + span.Value.Length + bodyStart > rawLength)
                return null;
            return new MdRawRange(checked(span.Value.Start + bodyStart), span.Value.Length);
        }
        var raw = node is LithoRawText ? null : Range(node.Span);
        if (node is LithoText textNode && textNode.Text.Length != 0)
        {
            // Legacy AST text spans may count decoded units; projection segments retain the original token ranges.
            var segments = textNode.Projection?.SourceSegments;
            if (segments is null || segments.Count == 0)
                raw = null;
            else
            {
                var start = int.MaxValue;
                var end = 0;
                foreach (var segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context?.Scan();
                    if (!segment.RawSpan.HasValue || segment.Kind == MdSegmentKind.Unknown)
                    {
                        start = int.MaxValue;
                        break;
                    }
                    start = Math.Min(start, segment.RawSpan.Value.Start);
                    end = Math.Max(end, segment.RawSpan.Value.End);
                }
                raw = start == int.MaxValue ? null : Range(new SourceSpan(start, end - start));
            }
        }
        if (node is LithoLineBreak { Hard: true } && raw is { } hardBreakSpan && rawText is not null
            && hardBreakSpan.Start < rawText.Length && (rawText[hardBreakSpan.Start] == '\r' || rawText[hardBreakSpan.Start] == '\n'))
        {
            // The legacy AST preserves its newline-only span for space markers.
            // A syntax envelope can include the contiguous source marker without changing that AST contract.
            var start = hardBreakSpan.Start;
            while (start > bodyStart && rawText[start - 1] == ' ')
            {
                cancellationToken.ThrowIfCancellationRequested();
                context?.Scan();
                start--;
            }
            if (hardBreakSpan.Start - start >= 2)
                raw = new MdRawRange(start, hardBreakSpan.End - start);
        }
        if (context is { Options.IsCommonMark: false })
            raw = CheckLegacyEmphasisRanges(node, raw, children, cancellationToken, context);
        MdSyntaxNode Plain(MdSyntaxKind kind, string? text = null) => new(kind, raw, children, text);
        switch (node)
        {
            case LithoHeading heading:
                return new(MdSyntaxKind.Heading, raw, children, headingLevel: heading.Level);
            case LithoList list:
                return new(list.Ordered ? MdSyntaxKind.OrderedList : MdSyntaxKind.UnorderedList,
                    raw, children, orderedListStart: list.Ordered ? list.Start : null, isTightList: list.Tight);
            case LithoItem item:
                return new(MdSyntaxKind.ListItem, raw, children, taskChecked: item.Checked);
            case LithoCode code:
                return new(code.Fenced ? MdSyntaxKind.FencedCodeBlock : MdSyntaxKind.IndentedCodeBlock,
                    raw, children, code.Text, codeInfo: code.Info);
            case LithoLink link:
                var target = raw.HasValue && links.TryGetValue(raw.Value, out var fact)
                    ? fact.TargetRawSpan : Range(link.LinkSyntax?.Target);
                return new(link.Image ? MdSyntaxKind.Image : MdSyntaxKind.Link, raw, children,
                    target: link.Url, targetRawSpan: target, title: link.Title, referenceLabel: link.LinkSyntax?.ReferenceLabel);
            case LithoAutolink autolink:
                var autolinkTarget = raw.HasValue && links.TryGetValue(raw.Value, out var autolinkFact)
                    ? autolinkFact.TargetRawSpan : null;
                return new(MdSyntaxKind.Autolink, raw, children, autolink.Text,
                    target: autolink.Href, targetRawSpan: autolinkTarget);
            case LithoParagraph:
                return Plain(MdSyntaxKind.Paragraph);
            case LithoQuote:
                return Plain(MdSyntaxKind.BlockQuote);
            case LithoBreak:
                return Plain(MdSyntaxKind.ThematicBreak);
            case LithoHtmlBlock html:
                return Plain(MdSyntaxKind.HtmlBlock, html.Text);
            case LithoText text:
                return Plain(MdSyntaxKind.Text, text.Text);
            case LithoEmphasis emphasis:
                return Plain(emphasis.Level == 1 ? MdSyntaxKind.Emphasis : MdSyntaxKind.StrongEmphasis);
            case LithoCodeSpan code:
                return Plain(MdSyntaxKind.CodeSpan, code.Code);
            case LithoHtmlInline html:
                return Plain(MdSyntaxKind.HtmlInline, html.Text);
            case LithoLineBreak line:
                return Plain(line.Hard ? MdSyntaxKind.HardLineBreak : MdSyntaxKind.SoftLineBreak, "\n");
            case LithoTable:
                return Plain(MdSyntaxKind.Table);
            case LithoStrike:
                return Plain(MdSyntaxKind.Strikethrough);
            case LithoAdmonition:
                return Plain(MdSyntaxKind.Admonition);
            case LithoDirective:
                return Plain(MdSyntaxKind.Directive);
            case LithoMath math:
                return Plain(MdSyntaxKind.MathBlock, math.Content);
            case LithoMathInline math:
                return Plain(MdSyntaxKind.MathInline, math.Content);
            default:
                return Plain(MdSyntaxKind.Unknown);
        }
    }

    private static MdRawRange? CheckLegacyEmphasisRanges(LithoNode source, MdRawRange? raw,
        List<MdSyntaxNode> children, CancellationToken cancellationToken, MdParseContext context)
    {
        // Legacy delimiter arithmetic can produce emphasis ranges that contradict the hierarchy.
        // Retain its grammar and facts; a new view must not assert those conflicting positions as exact.
        var known = new Stack<int>();
        for (var i = 0; i < children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Scan();
            if (children[i].RawSpan is not { } current)
                continue;
            if (raw is { } parent && (current.Start < parent.Start || current.End > parent.End))
            {
                if (source is LithoEmphasis)
                    raw = null;
                else if (IsEmphasis(children[i]))
                {
                    Forget(i);
                    continue;
                }
            }
            var conflict = false;
            while (known.Count != 0 && current.Start < children[known.Peek()].RawSpan!.Value.End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Scan();
                conflict = true;
                if (!IsEmphasis(children[known.Peek()]))
                    break;
                Forget(known.Pop());
            }
            if (conflict && IsEmphasis(children[i]))
                Forget(i);
            else
                known.Push(i);
        }
        return raw;

        void Forget(int index)
        {
            context.Scan(children[index].Children.Count);
            children[index] = children[index].WithoutRawSpan();
        }
        static bool IsEmphasis(MdSyntaxNode node) => node.Kind is MdSyntaxKind.Emphasis or MdSyntaxKind.StrongEmphasis;
    }
}
