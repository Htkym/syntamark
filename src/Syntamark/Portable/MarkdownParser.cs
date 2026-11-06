using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Syntamark.Compilation;

internal static class MdInlineFacts
{
    internal static string? LeafText(LithoInline node) => node switch
    {
        LithoText text => text.Text,
        LithoCodeSpan code => code.Code,
        LithoAutolink link => link.Text,
        LithoMathInline math => math.Content,
        LithoLineBreak => "\n",
        LithoRawText => throw new InvalidOperationException("Unresolved raw text reached text projection."),
        _ => null,
    };

    internal static MdTextProjection Text(IReadOnlyList<LithoInline> inlines, CancellationToken cancellationToken, bool includeImageLabels = true)
    {
        var pieces = new List<MdTextProjection>();
        foreach (var (node, exit) in LithoInline.Walk(inlines, includeImageLabels, cancellationToken))
        {
            if (exit)
                continue;
            if (LeafText(node) is not { } value)
                continue;
            var piece = node.Projection ?? MdSourceMapping.Unknown(value);
            if (piece is not null)
                pieces.Add(piece);
        }
        return MdSourceMapping.Join(pieces);
    }
}

internal static class MdParser
{
    internal static MdDocumentFacts Parse(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken)
        => ParseCore(raw, scopeId, sourceId, sourceVersion, options, cancellationToken, false, out _);

    internal static MdSyntaxDocument ParseWithSyntax(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken)
    {
        var facts = ParseCore(raw, scopeId, sourceId, sourceVersion, options, cancellationToken, true, out var root);
        return new MdSyntaxDocument(facts, root);
    }

    internal static MdAnalysisResult Analyze(string raw, MdAnalysisParts parts, string scopeId, string sourceId,
        string? sourceVersion, MdOptions options, CancellationToken cancellationToken)
    {
        MdAnalysisResult.Validate(parts);
        var facts = ParseCore(raw, scopeId, sourceId, sourceVersion, options, cancellationToken,
            (parts & MdAnalysisParts.Syntax) != 0, out var root, parts);
        return new MdAnalysisResult(facts, root, parts);
    }

    private static MdDocumentFacts ParseCore(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken, bool includeSyntax, out MdSyntaxNode? syntaxRoot,
        MdAnalysisParts requestedParts = MdAnalysisParts.AllFacts)
    {
        if (raw is null)
            throw new ArgumentNullException(nameof(raw));
        ValidateId(scopeId, nameof(scopeId));
        ValidateId(sourceId, nameof(sourceId));
        if (sourceVersion is not null && MdHash.InvalidUnicode(sourceVersion, cancellationToken) >= 0)
            throw new ArgumentException("SourceVersion must be well-formed Unicode.", nameof(sourceVersion));
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new FactsBuilder(raw, scopeId, sourceId, sourceVersion, options, cancellationToken, includeSyntax)
        {
            RequestedParts = requestedParts,
        };
        MdDocumentFacts facts;
        if (raw.Length > options.MaxInputUtf16)
            facts = builder.Failed(new MdDiagnostic("LMD002", MdDiagnosticSeverity.Error,
                "Input exceeds maxInputUtf16.", null, "input", "input"));
        else
        {
            var invalid = MdHash.InvalidUnicode(raw, cancellationToken);
            facts = invalid >= 0
                ? builder.Failed(new MdDiagnostic("LMD001", MdDiagnosticSeverity.Error,
                    "Input contains an unpaired surrogate.", new MdRawRange(invalid, 1), "input", "invalidUnicode"))
                : builder.Parse();
        }
        syntaxRoot = builder.SyntaxRoot;
        return facts;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || MdHash.InvalidUnicode(value, CancellationToken.None) >= 0)
            throw new ArgumentException("Identifiers must be non-empty, well-formed Unicode.", name);
    }

    private sealed class FactsBuilder(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken, bool includeSyntax)
    {
        internal MdAnalysisParts RequestedParts { get; init; } = MdAnalysisParts.AllFacts;
        internal MdSyntaxNode? SyntaxRoot { get; private set; }
        private IReadOnlyList<LithoBlock>? syntaxBlocks;
        private readonly MdParseContext context = new(options, cancellationToken);
        private MdFrontMatter frontmatter = new(MdFrontMatterState.Unknown, null, null, null, null);
        private MdRawRange? body;
        private string? textHash;
        private MdParseStatus status = MdParseStatus.Complete;
        private readonly List<MdHeading> headings = new();
        private readonly List<MdSection> sections = new();
        private readonly List<MdLink> links = new();
        private readonly Dictionary<string, MdRawRange?> referenceTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MdFence> fences = new();
        private readonly List<MdTextRegion> regions = new();
        private readonly List<MdDiagnostic> diagnostics = new();
        private readonly List<MdTextRegion> unprojected = new();
        private readonly HashSet<string> reasons = new(StringComparer.Ordinal);
        private LithoAnchorIds? anchorIds;
        private MdRawRange[] opaqueRanges = Array.Empty<MdRawRange>();
        private List<(string Kind, SourceSpan Span)>? htmlTokens;
        private bool unknownMapping;
        private bool incompleteRaw;
        private List<SectionHeading>? sectionHeadings;
        private Dictionary<MdRawRange, MdRawRange?>? syntaxTargets;
        private static readonly MdFrontMatter omittedFrontMatter = new(MdFrontMatterState.Unknown, null, null, null, null);

        private bool Wants(MdAnalysisParts part) => (RequestedParts & part) != 0;
        private bool WantsInlineTargets => Wants(MdAnalysisParts.Links) || includeSyntax;
        private bool WantsMappedFacts => Wants(MdAnalysisParts.FrontMatter | MdAnalysisParts.Headings
            | MdAnalysisParts.Links | MdAnalysisParts.Fences | MdAnalysisParts.TextRegions);
        private readonly record struct SectionHeading(int RawLevel, MdRawRange RawSpan, string LocalKey);

        internal MdDocumentFacts Failed(MdDiagnostic diagnostic)
        {
            status = MdParseStatus.Failed;
            incompleteRaw = true;
            unknownMapping = true;
            diagnostics.Add(diagnostic);
            reasons.Add(diagnostic.Reason);
            return Finish();
        }

        internal MdDocumentFacts Parse()
        {
            textHash = MdHash.Compute(raw);
            try
            {
                var split = MdFrontMatterParser.Split(raw, context);
                frontmatter = split.Facts;
                body = split.Body;
                if (split.Diagnostic is not null)
                {
                    context.Emit();
                    diagnostics.Add(split.Diagnostic);
                    Partial(split.Diagnostic.Reason);
                }
                if (Wants(MdAnalysisParts.FrontMatter))
                    CheckYaml(frontmatter.Root);
                if (body.HasValue)
                {
                    var bodyText = raw.Substring(body.Value.Start, body.Value.Length);
                    var parsed = LithoBlockParser.ParseFactsBlocks(bodyText, context);
                    if (includeSyntax)
                        syntaxBlocks = parsed.Blocks;
                    if (WantsInlineTargets)
                    {
                        foreach (var definition in context.Definitions)
                        {
                            MdRawRange? target;
                            if (Wants(MdAnalysisParts.Links))
                            {
                                var shifted = ShiftLink(definition);
                                links.Add(shifted);
                                target = shifted.TargetRawSpan;
                            }
                            else
                                target = ShiftRaw(definition.TargetRawSpan, body!.Value.Start);
                            var key = LithoBlockParser.NormalizeLabel(definition.ReferenceLabel!, context);
                            if (!referenceTargets.ContainsKey(key))
                                referenceTargets.Add(key, target);
                        }
                    }
                    PrepareOpaque(parsed.Blocks);
                    if (Wants(MdAnalysisParts.Headings | MdAnalysisParts.Sections | MdAnalysisParts.Links
                        | MdAnalysisParts.Fences | MdAnalysisParts.TextRegions) || includeSyntax)
                    {
                        foreach (var block in Walk(parsed.Blocks))
                            Project(block);
                    }
                    if (Wants(MdAnalysisParts.TextRegions))
                    {
                        if (options.IsCommonMark && htmlTokens is not null)
                        {
                            foreach (var token in htmlTokens.OrderBy(token => token.Span.Start))
                                Opaque(token.Kind, Range(token.Span));
                        }
                        else if (!options.IsCommonMark)
                        {
                            foreach (var opaque in opaqueRanges)
                                Opaque("rawHtmlOrMdx", Range(new SourceSpan(opaque.Start, opaque.Length)));
                        }
                    }
                    if (Wants(MdAnalysisParts.Sections))
                        MakeSections();
                }
                else
                {
                    incompleteRaw = true;
                    if (Wants(MdAnalysisParts.Sections))
                    {
                        context.Emit();
                        sections.Add(new MdSection("preamble", null, null, null, null));
                    }
                }
                if (unknownMapping)
                {
                    context.Emit();
                    diagnostics.Add(new MdDiagnostic("LMD006", MdDiagnosticSeverity.Information,
                        "Some decoded text cannot be mapped to raw tokens.", null, "mapping", "decodedMapping"));
                    if (options.IsCommonMark)
                        reasons.Add("decodedMapping");
                    else
                        Partial("decodedMapping");
                }
                if (syntaxBlocks is not null)
                    SyntaxRoot = Wants(MdAnalysisParts.Links)
                        ? MdSyntaxFactory.Create(syntaxBlocks, raw.Length, body!.Value.Start, cancellationToken, links, context, raw)
                        : MdSyntaxFactory.CreateWithTargets(syntaxBlocks, raw.Length, body!.Value.Start, cancellationToken,
                            syntaxTargets ?? new Dictionary<MdRawRange, MdRawRange?>(), context, raw);
            }
            catch (MdResourceLimit limit)
            {
                // Validated, already-published facts survive; incomplete collections remain explicit.
                Partial(limit.Reason);
                incompleteRaw = true;
                diagnostics.Add(new MdDiagnostic("LMD003", MdDiagnosticSeverity.Warning,
                    "Parsing stopped at a resource limit.", null, "resource", limit.Reason));
                if (body.HasValue && Wants(MdAnalysisParts.Links))
                {
                    var retained = new HashSet<int>(links.Where(link => link.Kind == MdLinkKind.ReferenceDefinition).Select(link => link.RawSpan.Start));
                    foreach (var definition in context.Definitions)
                        if (retained.Add(definition.RawSpan.Start + body.Value.Start))
                            links.Add(ShiftLink(definition));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Finish();
        }

        private void Partial(string reason)
        {
            status = MdParseStatus.Partial;
            reasons.Add(reason);
        }
        private MdRawRange Range(SourceSpan span) => new(checked((body?.Start ?? 0) + span.Start), span.Length);
        private MdRawRange? Range(SourceSpan? span) => span.HasValue ? Range(span.Value) : null;

        private MdTextProjection PublishText(MdTextProjection source, bool shift = true)
        {
            var masked = shift ? MaskOpaque(source) : null;
            var rawOffset = shift ? body!.Value.Start : 0;
            var count = masked?.Count ?? source.SourceSegments.Count;
            CheckProjectionShift(source, masked, rawOffset, count);
            MdSourceSegment[]? materialized = masked is null ? null : new MdSourceSegment[count];
            var decodedEnd = 0;
            var rawEnd = -1;
            for (var index = 0; index < count; index++)
            {
                var segment = ProjectionSegment(source, masked, index);
                segment = segment with { RawSpan = ShiftRaw(segment.RawSpan, rawOffset) };
                context.Scan(segment.DecodedSpan.Length);
                if (segment.DecodedSpan.Start != decodedEnd || segment.DecodedSpan.Length == 0)
                    throw new InvalidOperationException("Decoded mapping must cover the projected string in order.");
                decodedEnd = segment.DecodedSpan.End;
                var converted = segment;
                if (segment.RawSpan.HasValue)
                {
                    var rawSpan = segment.RawSpan.Value;
                    if (rawSpan.End > raw.Length)
                        throw new InvalidOperationException("Projected raw range is outside the source.");
                    if (rawSpan.Start < rawEnd)
                        converted = new MdMappingSegment(segment.DecodedSpan, null, MdSegmentKind.Unknown);
                    else
                        rawEnd = rawSpan.End;
                    if (segment.Kind == MdSegmentKind.Linear && converted.Kind != MdSegmentKind.Unknown)
                    {
                        if (rawSpan.Length != segment.DecodedSpan.Length)
                            throw new InvalidOperationException("Linear lengths differ.");
                        if (string.CompareOrdinal(raw, rawSpan.Start, source.Text!, segment.DecodedSpan.Start, rawSpan.Length) != 0)
                            converted = new MdMappingSegment(segment.DecodedSpan, rawSpan, MdSegmentKind.Atomic);
                    }
                }
                else if (segment.Kind != MdSegmentKind.Unknown)
                    throw new InvalidOperationException("Known mapping has no raw range.");
                if (converted.Kind == MdSegmentKind.Unknown)
                    unknownMapping = true;
                StoreProjectionSegment(source, masked, ref materialized, index, converted);
            }
            if (source.Text is not null && decodedEnd != source.Text.Length)
                throw new InvalidOperationException("Incomplete decoded mapping.");
            context.Emit(count);
            return materialized is null ? source : MdTextProjection.CreateOwned(source.Text, materialized);
        }

        private static MdMappingSegment ProjectionSegment(MdTextProjection source, List<MdMappingSegment>? masked, int index)
        {
            if (masked is not null)
                return masked[index];
            var segment = source.SourceSegments[index];
            return new MdMappingSegment(segment.DecodedSpan, segment.RawSpan, segment.Kind);
        }

        private static MdRawRange? ShiftRaw(MdRawRange? span, int rawOffset) => span.HasValue
            ? new MdRawRange(checked(span.Value.Start + rawOffset), span.Value.Length) : null;

        private static void CheckProjectionShift(MdTextProjection source, List<MdMappingSegment>? masked, int rawOffset, int count)
        {
            if (rawOffset == 0)
                return;
            // Shifting used to complete before publication Scan/Emit. Keep its checked
            // arithmetic at that barrier without materializing an intermediate projection.
            for (var index = 0; index < count; index++)
                _ = ShiftRaw(ProjectionSegment(source, masked, index).RawSpan, rawOffset);
        }

        private static void StoreProjectionSegment(MdTextProjection source, List<MdMappingSegment>? masked,
            ref MdSourceSegment[]? materialized, int index, MdMappingSegment value)
        {
            if (masked is null)
            {
                var original = source.SourceSegments[index];
                if (original.DecodedSpan == value.DecodedSpan && original.RawSpan == value.RawSpan && original.Kind == value.Kind)
                    return;
                materialized ??= source.SourceSegments.ToArray();
            }
            materialized![index] = new MdSourceSegment(value.DecodedSpan, value.RawSpan, value.Kind);
        }

        private void CheckYaml(MdYamlNode? node)
        {
            if (node is null)
                return;
            var pending = new Stack<MdYamlNode>();
            pending.Push(node);
            while (pending.Count != 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (current.Scalar.SourceSegments.Any(s => s.Kind == MdSegmentKind.Unknown))
                    unknownMapping = true;
                foreach (var child in current.Children)
                    pending.Push(child);
            }
        }

        private IEnumerable<LithoBlock> Walk(IReadOnlyList<LithoBlock> nodes)
        {
            var pending = new Stack<LithoBlock>();
            for (var i = nodes.Count - 1; i >= 0; i--)
                pending.Push(nodes[i]);
            while (pending.Count != 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var node = pending.Pop();
                yield return node;
                if (node is LithoList list)
                {
                    for (var item = list.Items.Count - 1; item >= 0; item--)
                        for (var child = list.Items[item].Children.Count - 1; child >= 0; child--)
                            pending.Push(list.Items[item].Children[child]);
                    continue;
                }
                var children = node switch
                {
                    LithoQuote quote => quote.Children,
                    LithoAdmonition admonition => admonition.Children,
                    LithoDirective directive => directive.Children,
                    _ => null,
                };
                if (children is not null)
                    for (var i = children.Count - 1; i >= 0; i--)
                        pending.Push(children[i]);
            }
        }

        private void Project(LithoBlock block)
        {
            switch (block)
            {
                case LithoHeading heading:
                    {
                        if (Wants(MdAnalysisParts.Headings))
                        {
                            var text = PublishText(MdInlineFacts.Text(heading.Inlines, cancellationToken, includeImageLabels: false));
                            context.Scan((long)text.Text!.Length * 8);
                            var anchor = (anchorIds ??= new LithoAnchorIds(context)).Next(LithoSlug.Slugify(text.Text!));
                            MdRawRange? textSpan = null;
                            if (heading.RawContent is not null)
                            {
                                var content = heading.RawContent;
                                var from = content.Map.Global(0);
                                var to = content.Text.Length == 0 ? from : content.Map.Global(content.Text.Length - 1) + 1;
                                textSpan = Range(new SourceSpan(from, to - from));
                            }
                            context.Emit();
                            headings.Add(new MdHeading("heading-" + headings.Count.ToString(CultureInfo.InvariantCulture),
                            heading.Level, text, Range(heading.Span), textSpan, anchor));
                        }
                        else if (Wants(MdAnalysisParts.Sections))
                        {
                            context.Emit();
                            var boundaries = sectionHeadings ??= new List<SectionHeading>();
                            boundaries.Add(new SectionHeading(heading.Level, Range(heading.Span),
                                "heading-" + boundaries.Count.ToString(CultureInfo.InvariantCulture)));
                        }
                        if (WantsInlineTargets)
                            AddInlineLinks(heading.Inlines);
                        break;
                    }
                case LithoParagraph paragraph:
                    {
                        if (Wants(MdAnalysisParts.TextRegions))
                        {
                            var text = PublishText(MdInlineFacts.Text(paragraph.Inlines, cancellationToken));
                            context.Emit();
                            regions.Add(new MdTextRegion("paragraph", Range(paragraph.Span), text));
                        }
                        if (WantsInlineTargets)
                            AddInlineLinks(paragraph.Inlines);
                        break;
                    }
                case LithoCode code when code.Fenced && code.FenceSyntax is not null:
                    {
                        if (Wants(MdAnalysisParts.Fences))
                        {
                            var fence = code.FenceSyntax;
                            var text = PublishText(fence.Text);
                            context.Emit();
                            fences.Add(new MdFence(Range(code.Span), Range(fence.Opening), Range(fence.Info),
                            Range(fence.Content), Range(fence.Closing), fence.Closing.HasValue, fence.InfoText, text));
                        }
                        break;
                    }
                case LithoCode code:
                    {
                        if (Wants(MdAnalysisParts.TextRegions))
                        {
                            var text = PublishText(MdSourceMapping.Unknown(code.Text));
                            context.Emit();
                            regions.Add(new MdTextRegion("indentedCode", Range(code.Span), text));
                        }
                        break;
                    }
                case LithoMath math:
                    {
                        if (Wants(MdAnalysisParts.TextRegions))
                        {
                            var text = PublishText(MdSourceMapping.Unknown(math.Content));
                            context.Emit();
                            regions.Add(new MdTextRegion("blockMath", Range(math.Span), text));
                        }
                        break;
                    }
                case LithoTable table:
                    foreach (var row in table.Rows)
                        foreach (var cell in row)
                        {
                            if (Wants(MdAnalysisParts.TextRegions))
                            {
                                var text = PublishText(MdInlineFacts.Text(cell, cancellationToken));
                                context.Emit();
                                regions.Add(new MdTextRegion("tableCell", Range(table.Span), text));
                            }
                            if (WantsInlineTargets)
                                AddInlineLinks(cell);
                        }
                    break;
            }
        }

        private void PrepareOpaque(IReadOnlyList<LithoBlock> blocks)
        {
            var candidates = options.IsCommonMark ? new List<MdRawRange>() : new List<MdRawRange>(context.OpaqueTokens);
            foreach (var block in Walk(blocks))
            {
                if (options.IsCommonMark)
                {
                    if (block is LithoHtmlBlock html)
                        AddHtml("rawHtmlBlock", html.Span);
                    var inlines = block switch
                    {
                        LithoParagraph paragraph => paragraph.Inlines,
                        LithoHeading heading => heading.Inlines,
                        _ => null,
                    };
                    if (inlines is not null)
                        foreach (var (inline, exit) in LithoInline.Walk(inlines, cancellationToken: cancellationToken))
                            if (!exit && inline is LithoHtmlInline token)
                                AddHtml("rawHtmlInline", token.Span);
                }
                else if (block is LithoParagraph { RawContent: { } content }
                    && MdOpaqueSyntax.TryEsm(content.Text, context, out var end) && end != 0)
                {
                    var start = content.Map.Global(0);
                    candidates.Add(new MdRawRange(start, content.Map.Global(end - 1) + 1 - start));
                }
            }
            void AddHtml(string kind, SourceSpan span)
            {
                context.Scan();
                (htmlTokens ??= new List<(string Kind, SourceSpan Span)>()).Add((kind, span));
                candidates.Add(new MdRawRange(span.Start, span.Length));
            }
            var merged = new List<MdRawRange>();
            foreach (var span in candidates.OrderBy(s => s.Start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (merged.Count != 0 && span.Start <= merged[merged.Count - 1].End)
                {
                    var previous = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new MdRawRange(previous.Start, Math.Max(previous.End, span.End) - previous.Start);
                }
                else
                    merged.Add(span);
            }
            opaqueRanges = merged.ToArray();
        }

        private int FirstOpaque(int start)
        {
            var low = 0;
            var high = opaqueRanges.Length;
            while (low < high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = low + (high - low) / 2;
                if (opaqueRanges[middle].End <= start)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private bool InsideOpaque(SourceSpan span)
        {
            var index = FirstOpaque(span.Start);
            return index < opaqueRanges.Length && opaqueRanges[index].Start <= span.Start && span.End <= opaqueRanges[index].End;
        }

        private List<MdMappingSegment>? MaskOpaque(MdTextProjection text)
        {
            if (opaqueRanges.Length == 0)
                return null;
            List<MdMappingSegment>? segments = null;
            for (var segmentIndex = 0; segmentIndex < text.SourceSegments.Count; segmentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = text.SourceSegments[segmentIndex];
                if (!segment.RawSpan.HasValue)
                {
                    segments?.Add(new MdMappingSegment(segment.DecodedSpan, segment.RawSpan, segment.Kind));
                    continue;
                }
                var raw = segment.RawSpan.Value;
                var index = FirstOpaque(raw.Start);
                if (index == opaqueRanges.Length || opaqueRanges[index].Start >= raw.End)
                {
                    segments?.Add(new MdMappingSegment(segment.DecodedSpan, segment.RawSpan, segment.Kind));
                    continue;
                }
                if (segments is null)
                {
                    segments = new List<MdMappingSegment>(text.SourceSegments.Count);
                    for (var previous = 0; previous < segmentIndex; previous++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var retained = text.SourceSegments[previous];
                        segments.Add(new MdMappingSegment(retained.DecodedSpan, retained.RawSpan, retained.Kind));
                    }
                }
                if (segment.Kind != MdSegmentKind.Linear)
                {
                    segments.Add(new MdMappingSegment(segment.DecodedSpan, null, MdSegmentKind.Unknown));
                    continue;
                }
                var cursor = raw.Start;
                while (index < opaqueRanges.Length && opaqueRanges[index].Start < raw.End)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var opaque = opaqueRanges[index++];
                    var start = Math.Max(cursor, opaque.Start);
                    var end = Math.Min(raw.End, opaque.End);
                    if (cursor < start)
                        Add(cursor, start, known: true);
                    if (start < end)
                        Add(start, end, known: false);
                    cursor = end;
                }
                if (cursor < raw.End)
                    Add(cursor, raw.End, known: true);
                void Add(int start, int end, bool known) => segments!.Add(new MdMappingSegment(
                    new MdRawRange(segment.DecodedSpan.Start + start - raw.Start, end - start),
                    known ? new MdRawRange(start, end - start) : null, known ? MdSegmentKind.Linear : MdSegmentKind.Unknown));
            }
            return segments;
        }

        private void Opaque(string kind, MdRawRange span)
        {
            context.Emit();
            var region = new MdTextRegion(kind, span, MdSourceMapping.Unknown(null));
            regions.Add(region);
            unprojected.Add(region);
            unknownMapping = true;
            if (options.IsCommonMark)
                reasons.Add(kind);
            else
                Partial(kind);
        }

        private MdLink ShiftLink(MdLink link, bool maskLabel = false)
        {
            var source = link.Label;
            var masked = maskLabel ? MaskOpaque(source) : null;
            var count = masked?.Count ?? source.SourceSegments.Count;
            if (maskLabel)
            {
                // The scanner already charged the original unresolved label segments.
                context.Emit(count - source.SourceSegments.Count);
                for (var index = 0; index < count; index++)
                    if (ProjectionSegment(source, masked, index).Kind == MdSegmentKind.Unknown)
                    {
                        unknownMapping = true;
                        break;
                    }
            }
            var rawOffset = body!.Value.Start;
            CheckProjectionShift(source, masked, rawOffset, count);
            MdSourceSegment[]? materialized = masked is null ? null : new MdSourceSegment[count];
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = ProjectionSegment(source, masked, index);
                segment = segment with { RawSpan = ShiftRaw(segment.RawSpan, rawOffset) };
                if (segment.Kind == MdSegmentKind.Linear && segment.RawSpan.HasValue)
                {
                    var span = segment.RawSpan.Value;
                    if (span.End > raw.Length || source.Text is null || segment.DecodedSpan.End > source.Text.Length)
                        throw new InvalidOperationException("Link label mapping is outside its source.");
                    if (span.Length != segment.DecodedSpan.Length
                        || string.CompareOrdinal(raw, span.Start, source.Text, segment.DecodedSpan.Start, span.Length) != 0)
                    {
                        // These segments were already charged before reference resolution or resource-stop retention.
                        segment = segment with { Kind = MdSegmentKind.Atomic };
                    }
                }
                StoreProjectionSegment(source, masked, ref materialized, index, segment);
            }
            var label = materialized is null ? source : MdTextProjection.CreateOwned(source.Text, materialized);
            return new MdLink(link.Kind, link.Resolution, link.Image,
                new MdRawRange(link.RawSpan.Start + body!.Value.Start, link.RawSpan.Length),
                label, link.Target, link.Title,
                link.TargetRawSpan.HasValue ? new MdRawRange(link.TargetRawSpan.Value.Start + body.Value.Start, link.TargetRawSpan.Value.Length) : null,
                link.ReferenceLabel);
        }

        private void AddInlineLinks(IReadOnlyList<LithoInline> nodes)
        {
            foreach (var (node, exit) in LithoInline.Walk(nodes, cancellationToken: cancellationToken))
            {
                if (exit)
                    continue;
                if (node.UnresolvedReference is not null)
                {
                    var unresolved = node.UnresolvedReference;
                    if (Wants(MdAnalysisParts.Links)
                        && !InsideOpaque(new SourceSpan(unresolved.RawSpan.Start, unresolved.RawSpan.Length)))
                        links.Add(ShiftLink(unresolved, maskLabel: true));
                    continue;
                }
                if (node is LithoLink link)
                {
                    if (InsideOpaque(link.Span))
                        continue;
                    var syntax = link.LinkSyntax ?? throw new InvalidOperationException("Missing link syntax facts.");
                    var label = Wants(MdAnalysisParts.Links)
                        ? PublishText(MdInlineFacts.Text(link.Label, cancellationToken)) : null;
                    var targetSpan = Range(syntax.Target);
                    if (syntax.Kind == MdLinkKind.ReferenceUse)
                    {
                        var normalized = LithoBlockParser.NormalizeLabel(syntax.ReferenceLabel!, context);
                        referenceTargets.TryGetValue(normalized, out targetSpan);
                    }
                    if (Wants(MdAnalysisParts.Links))
                    {
                        context.Emit();
                        links.Add(new MdLink(syntax.Kind,
                            syntax.Kind == MdLinkKind.ReferenceUse ? MdLinkResolution.ResolvedReference : MdLinkResolution.Inline,
                            link.Image, Range(link.Span), label!, link.Url, link.Title, targetSpan, syntax.ReferenceLabel));
                    }
                    else
                        AddSyntaxTarget(Range(link.Span), targetSpan);
                }
                else if (node is LithoAutolink autolink)
                {
                    if (InsideOpaque(autolink.Span))
                        continue;
                    var label = PublishText(node.Projection ?? MdSourceMapping.Unknown(autolink.Text));
                    var mapped = MdSourceMapping.Map(label, new MdRawRange(0, label.Text!.Length));
                    var target = mapped.RawFragments.Count == 1 ? mapped.RawFragments[0] : (MdRawRange?)null;
                    if (Wants(MdAnalysisParts.Links))
                    {
                        context.Emit();
                        links.Add(new MdLink(MdLinkKind.Autolink, MdLinkResolution.Inline, false,
                            Range(autolink.Span), label, autolink.Href, null, target, null));
                    }
                    else
                        AddSyntaxTarget(Range(autolink.Span), target);
                }
            }
        }

        private void AddSyntaxTarget(MdRawRange use, MdRawRange? target)
        {
            // The links path already charges its MdLink and feeds the original factory
            // adapter. Syntax-only retains just this semantic target edge instead.
            context.Scan();
            context.Emit();
            (syntaxTargets ??= new Dictionary<MdRawRange, MdRawRange?>())[use] = target;
        }

        private SectionHeading SectionHeadingAt(int index)
        {
            if (!Wants(MdAnalysisParts.Headings))
                return sectionHeadings![index];
            var heading = headings[index];
            return new SectionHeading(heading.RawLevel, heading.RawSpan, heading.LocalKey);
        }

        private void MakeSections()
        {
            var source = body!.Value;
            context.Emit();
            var count = Wants(MdAnalysisParts.Headings) ? headings.Count : sectionHeadings?.Count ?? 0;
            var firstStart = count == 0 ? source.End : SectionHeadingAt(0).RawSpan.Start;
            sections.Add(new MdSection("preamble", null, null,
                new MdRawRange(source.Start, firstStart - source.Start),
                new MdRawRange(source.Start, firstStart - source.Start)));
            if (count == 0)
                return;
            var parents = new Stack<int>();
            var ends = new int[count];
            var unfinished = new Stack<int>();
            for (var i = 0; i < count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var heading = SectionHeadingAt(i);
                while (unfinished.Count != 0 && SectionHeadingAt(unfinished.Peek()).RawLevel >= heading.RawLevel)
                    ends[unfinished.Pop()] = heading.RawSpan.Start;
                unfinished.Push(i);
            }
            while (unfinished.Count != 0)
                ends[unfinished.Pop()] = source.End;
            for (var i = 0; i < count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var heading = SectionHeadingAt(i);
                while (parents.Count != 0 && SectionHeadingAt(parents.Peek()).RawLevel >= heading.RawLevel)
                    parents.Pop();
                var parent = parents.Count == 0 ? null : "section-" + parents.Peek().ToString(CultureInfo.InvariantCulture);
                var end = ends[i];
                var directEnd = i + 1 < count ? Math.Min(end, SectionHeadingAt(i + 1).RawSpan.Start) : end;
                var directStart = heading.RawSpan.End;
                if (directStart < raw.Length && raw[directStart] == '\r')
                    directStart++;
                if (directStart < raw.Length && raw[directStart] == '\n')
                    directStart++;
                context.Emit();
                sections.Add(new MdSection("section-" + i.ToString(CultureInfo.InvariantCulture), parent, heading.LocalKey,
                    new MdRawRange(directStart, Math.Max(0, directEnd - directStart)), new MdRawRange(heading.RawSpan.Start, end - heading.RawSpan.Start)));
                parents.Push(i);
            }
        }

        private MdDocumentFacts Finish()
        {
            var coverage = new MdCoverage(incompleteRaw ? MdCoverageState.Partial : MdCoverageState.Complete,
                status == MdParseStatus.Failed || !WantsMappedFacts ? MdCoverageState.Unknown
                    : unknownMapping || incompleteRaw ? MdCoverageState.Partial : MdCoverageState.Complete,
                unprojected, reasons.OrderBy(value => value, StringComparer.Ordinal).ToArray());
            IReadOnlyList<MdLink> orderedLinks = Wants(MdAnalysisParts.Links)
                ? links.OrderBy(link => link.RawSpan.Start).ToArray() : MdFreeze.Empty<MdLink>();
            return new MdDocumentFacts(raw, scopeId, sourceId, sourceVersion, MdParserVersion.Value, options.Hash, textHash,
                status, body, Wants(MdAnalysisParts.FrontMatter) ? frontmatter : omittedFrontMatter,
                headings, sections, orderedLinks, fences, regions, diagnostics,
                new[] {
                    new MdCapability("markdownSyntax", options.IsCommonMark ? MdFeatureState.Partial : MdFeatureState.Supported),
                    new MdCapability("frontmatterSyntax", options.IsCommonMark ? MdFeatureState.Unsupported : MdFeatureState.Supported),
                    new MdCapability("decodedSourceMapping", MdFeatureState.Partial),
                    new MdCapability("rawHtmlMdxProjection", MdFeatureState.Unsupported),
                    new MdCapability("siteResolution", MdFeatureState.Unsupported),
                }, coverage, (RequestedParts & MdAnalysisParts.AllFacts) == MdAnalysisParts.AllFacts
                    ? "1.0" : "analysis/1", options.ProfileId);
        }
    }
}
