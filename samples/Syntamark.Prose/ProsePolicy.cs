using Syntamark;

namespace Syntamark.Prose.Sample;

// This is a caller's editorial policy, not a new definition of Markdown prose.
internal sealed class ProseSegment(string kind, MarkdownTextProjection projection, RawSpan decodedSpan)
{
    internal string Kind { get; } = kind;
    internal MarkdownTextProjection Projection { get; } = projection;
    internal RawSpan DecodedSpan { get; } = decodedSpan;
    internal string Text => Projection.Text!.Substring(DecodedSpan.Start, DecodedSpan.Length);

    internal MarkdownMappingResult Map(RawSpan localDecodedSpan)
    {
        if (localDecodedSpan.End > DecodedSpan.Length)
            throw new ArgumentOutOfRangeException(nameof(localDecodedSpan));
        return Projection.Map(new RawSpan(checked(DecodedSpan.Start + localDecodedSpan.Start), localDecodedSpan.Length));
    }
}

internal sealed record ProseExclusion(string Reason, RawSpan? RawSpan);
internal sealed record ProseSelection(MarkdownDocument Facts, bool HasSyntax,
    IReadOnlyList<ProseSegment> Segments, IReadOnlyList<ProseExclusion> Exclusions);

internal static class ProsePolicy
{
    internal static ProseSelection Select(MarkdownSyntaxDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exclusions = new List<ProseExclusion>();
        if (document.Root is null)
            return new(document.Facts, false, Array.Empty<ProseSegment>(),
                Array.AsReadOnly(new[] { new ProseExclusion("syntax-unavailable", null) }));

        var pending = new Stack<MarkdownSyntaxNode>();
        pending.Push(document.Root);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = pending.Pop();
            var reason = node.Kind switch
            {
                MarkdownSyntaxKind.FencedCodeBlock or MarkdownSyntaxKind.IndentedCodeBlock => "code-block",
                MarkdownSyntaxKind.CodeSpan => "inline-code",
                MarkdownSyntaxKind.Image => "image-alt",
                MarkdownSyntaxKind.Autolink => "autolink",
                MarkdownSyntaxKind.HtmlBlock => "html-block",
                MarkdownSyntaxKind.HtmlInline => "html-token",
                MarkdownSyntaxKind.MathBlock or MarkdownSyntaxKind.MathInline => "math-policy",
                _ => null,
            };
            if (reason is not null)
                exclusions.Add(new(reason, node.RawSpan));
            else
                for (var i = node.Children.Count - 1; i >= 0; i--)
                    pending.Push(node.Children[i]);
        }
        var frontMatter = document.Facts.FrontMatter;
        if (frontMatter.OpeningSpan is { } opening && frontMatter.ClosingSpan is { } closing)
            exclusions.Add(new("frontmatter", new RawSpan(opening.Start, closing.End - opening.Start)));
        foreach (var link in document.Facts.Links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.Kind == MarkdownLinkKind.ReferenceDefinition)
                exclusions.Add(new("reference-definition", link.RawSpan));
        }

        if (exclusions.Any(item => !item.RawSpan.HasValue))
            return new(document.Facts, true, Array.Empty<ProseSegment>(), exclusions.AsReadOnly());
        var blocked = exclusions.Where(item => item.RawSpan is { Length: > 0 }).Select(item => item.RawSpan!.Value)
            .OrderBy(span => span.Start).ToArray();
        var sources = document.Facts.Headings.Select(heading => ("heading", (RawSpan?)heading.RawSpan, heading.Text))
            .Concat(document.Facts.TextRegions.Where(region => region.Kind is "paragraph" or "tableCell")
                .Select(region => (region.Kind, region.RawSpan, region.Content)))
            .OrderBy(source => source.Item2?.Start ?? int.MaxValue);
        var segments = new List<ProseSegment>();
        foreach (var (kind, _, projection) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (projection.Text is null)
                continue;
            var allowed = new List<RawSpan>();
            int? previousRawEnd = null;
            foreach (var source in projection.SourceSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source.RawSpan is not { } raw || source.Kind == MarkdownSegmentKind.Unknown)
                {
                    exclusions.Add(new("unknown-projection", source.RawSpan));
                    continue;
                }
                var cursor = source.DecodedSpan.Start;
                var end = source.DecodedSpan.End;
                foreach (var mask in blocked)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (mask.End <= raw.Start)
                        continue;
                    if (mask.Start >= raw.End)
                        break;
                    if (source.Kind == MarkdownSegmentKind.Atomic)
                    {
                        cursor = end;
                        break;
                    }
                    var left = source.DecodedSpan.Start + Math.Max(raw.Start, mask.Start) - raw.Start;
                    var right = source.DecodedSpan.Start + Math.Min(raw.End, mask.End) - raw.Start;
                    if (left > cursor)
                        Add(cursor, left, raw.Start + cursor - source.DecodedSpan.Start,
                            raw.Start + left - source.DecodedSpan.Start);
                    cursor = Math.Max(cursor, right);
                }
                if (cursor < end)
                    Add(cursor, end, source.Kind == MarkdownSegmentKind.Atomic ? raw.Start
                        : raw.Start + cursor - source.DecodedSpan.Start, raw.End);
            }
            foreach (var span in allowed)
                if (!string.IsNullOrWhiteSpace(projection.Text.Substring(span.Start, span.Length)))
                    segments.Add(new(kind, projection, span));

            void Add(int start, int end, int rawStart, int rawEnd)
            {
                var crossesExclusion = false;
                if (previousRawEnd is { } previousEnd)
                    foreach (var mask in blocked)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mask.Start >= rawStart)
                            break;
                        if (mask.End > previousEnd)
                        {
                            crossesExclusion = true;
                            break;
                        }
                    }
                if (allowed.Count != 0 && allowed[^1].End == start && !crossesExclusion)
                {
                    var previous = allowed[^1];
                    allowed[^1] = new(previous.Start, end - previous.Start);
                }
                else
                    allowed.Add(new(start, end - start));
                previousRawEnd = rawEnd;
            }
        }
        return new(document.Facts, true, segments.AsReadOnly(), exclusions.AsReadOnly());
    }
}
