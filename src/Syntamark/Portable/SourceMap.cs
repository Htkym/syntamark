using System;
using System.Collections.Generic;
using System.Threading;

namespace Syntamark.Compilation;

internal sealed class MdSourceSnapshot
{
    internal MdSourceSnapshot(string sourceId, string version, string text)
    {
        if (string.IsNullOrEmpty(sourceId) || version is null || text is null)
            throw new ArgumentException("A source identifier, version and text are required.");
        if (MdHash.InvalidUnicode(sourceId, CancellationToken.None) >= 0
            || MdHash.InvalidUnicode(version, CancellationToken.None) >= 0
            || MdHash.InvalidUnicode(text, CancellationToken.None) >= 0)
            throw new ArgumentException("Source snapshots require well-formed Unicode.");
        SourceId = sourceId;
        Version = version;
        Text = text;
        TextHash = MdHash.Compute(text);
    }

    internal string SourceId { get; }
    internal string Version { get; }
    internal string Text { get; }
    internal string TextHash { get; }
    internal bool Matches(MdSourceSnapshot other) => other is not null && SourceId == other.SourceId
        && Version == other.Version && TextHash == other.TextHash;
    internal bool IsBoundary(int offset) => offset == 0 || offset == Text.Length
        || !char.IsHighSurrogate(Text[offset - 1]) || !char.IsLowSurrogate(Text[offset]);
    internal void Validate(MdRawRange span)
    {
        if (span.End > Text.Length)
            throw new ArgumentOutOfRangeException(nameof(span));
    }
}

internal sealed class MdSourceRange
{
    internal MdSourceRange(MdSourceSnapshot source, MdRawRange span)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        source.Validate(span);
        Span = span;
    }
    internal MdSourceSnapshot Source { get; }
    internal MdRawRange Span { get; }
}

internal sealed class MdSourceMapSegment
{
    internal MdSourceMapSegment(MdRawRange outputSpan, MdSourceRange? origin, MdSegmentKind kind)
    {
        if (kind is not (MdSegmentKind.Linear or MdSegmentKind.Atomic or MdSegmentKind.Unknown))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == MdSegmentKind.Unknown ? origin is not null : origin is null)
            throw new ArgumentException("Known segments require an origin; generated segments have none.");
        if (kind == MdSegmentKind.Linear && outputSpan.Length != origin!.Span.Length)
            throw new ArgumentException("Linear segment lengths must agree.");
        OutputSpan = outputSpan;
        Origin = origin;
        Kind = kind;
    }
    internal MdRawRange OutputSpan { get; }
    internal MdSourceRange? Origin { get; }
    internal MdSegmentKind Kind { get; }
}

internal sealed class MdSourceMapResult(IReadOnlyList<MdSourceRange> origins, MdMappingPrecision precision)
{
    internal IReadOnlyList<MdSourceRange> Origins { get; } = MdFreeze.Copy(origins);
    internal MdMappingPrecision Precision { get; } = precision;
    internal bool CanEdit => Precision == MdMappingPrecision.Exact && Origins.Count == 1
        && Origins[0].Source.IsBoundary(Origins[0].Span.Start) && Origins[0].Source.IsBoundary(Origins[0].Span.End);
}

internal sealed class MdSourceMap
{
    private readonly IReadOnlyList<MdSourceMap> upstream;

    internal MdSourceMap(MdSourceSnapshot output, IReadOnlyList<MdSourceMapSegment> segments)
        : this(output, segments, Array.Empty<MdSourceMap>()) { }

    private MdSourceMap(MdSourceSnapshot output, IReadOnlyList<MdSourceMapSegment> segments,
        IReadOnlyList<MdSourceMap> upstream)
    {
        Output = output ?? throw new ArgumentNullException(nameof(output));
        if (segments is null)
            throw new ArgumentNullException(nameof(segments));
        var end = 0;
        foreach (var segment in segments)
        {
            if (segment is null || segment.OutputSpan.Start < end || segment.OutputSpan.Length == 0)
                throw new ArgumentException("Segments must be nonempty and ordered without overlap.", nameof(segments));
            output.Validate(segment.OutputSpan);
            end = segment.OutputSpan.End;
            if (segment.Kind == MdSegmentKind.Linear && string.CompareOrdinal(output.Text, segment.OutputSpan.Start,
                segment.Origin!.Source.Text, segment.Origin.Span.Start, segment.OutputSpan.Length) != 0)
                throw new ArgumentException("Linear segments must contain identical UTF-16 text.", nameof(segments));
        }
        Segments = MdFreeze.Copy(segments);
        this.upstream = MdFreeze.Copy(upstream);
    }

    internal MdSourceSnapshot Output { get; }
    internal IReadOnlyList<MdSourceMapSegment> Segments { get; }

    internal MdSourceMap Compose(IReadOnlyList<MdSourceMap> previous)
    {
        if (previous is null)
            throw new ArgumentNullException(nameof(previous));
        var merged = new List<MdSourceMap>(upstream);
        foreach (var map in previous)
        {
            if (map is null || map.Output.Matches(Output))
                throw new ArgumentException("An upstream map must describe a distinct snapshot.", nameof(previous));
            foreach (var existing in merged)
                if (existing.Output.Matches(map.Output))
                    throw new ArgumentException("An intermediate snapshot has more than one map.", nameof(previous));
            merged.Add(map);
        }
        return new MdSourceMap(Output, Segments, merged);
    }

    internal MdSourceMapResult Map(MdRawRange span, CancellationToken cancellationToken = default)
    {
        Output.Validate(span);
        var maps = new Dictionary<(string Id, string Version, string Hash), MdSourceMap>();
        var collect = new Stack<MdSourceMap>();
        collect.Push(this);
        while (collect.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var map = collect.Pop();
            var key = (map.Output.SourceId, map.Output.Version, map.Output.TextHash);
            if (maps.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(map, existing))
                    throw new InvalidOperationException("An intermediate snapshot has conflicting maps.");
                continue;
            }
            maps.Add(key, map);
            foreach (var previous in map.upstream)
                collect.Push(previous);
        }
        var pending = new Stack<(MdSourceMap? Map, MdSourceRange? Origin, MdRawRange Span, bool Atomic, IReadOnlyList<MdSourceSnapshot> Path)>();
        pending.Push((this, null, span, false, new[] { Output }));
        var origins = new List<MdSourceRange>();
        var unknown = false;
        var atomic = false;
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var work = pending.Pop();
            if (work.Map is null)
            {
                atomic |= work.Atomic;
                Append(work.Origin!);
                continue;
            }
            var local = new List<(MdSourceRange Origin, bool Atomic)>();
            var covered = 0;
            foreach (var segment in work.Map.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = Math.Max(work.Span.Start, segment.OutputSpan.Start);
                var to = Math.Min(work.Span.End, segment.OutputSpan.End);
                var point = work.Span.Length == 0 && work.Span.Start >= segment.OutputSpan.Start
                    && work.Span.Start <= segment.OutputSpan.End;
                if (from >= to && !point)
                    continue;
                covered += to - from;
                if (segment.Origin is null)
                {
                    unknown = true;
                    continue;
                }
                var originSpan = segment.Origin.Span;
                var isAtomic = work.Atomic || segment.Kind == MdSegmentKind.Atomic;
                if (segment.Kind == MdSegmentKind.Linear)
                    originSpan = new MdRawRange(originSpan.Start + from - segment.OutputSpan.Start, to - from);
                else if (point)
                {
                    if (from != segment.OutputSpan.Start && from != segment.OutputSpan.End)
                    {
                        unknown = true;
                        continue;
                    }
                    originSpan = new MdRawRange(from == segment.OutputSpan.Start ? originSpan.Start : originSpan.End, 0);
                }
                local.Add((new MdSourceRange(segment.Origin.Source, originSpan), isAtomic));
            }
            if (covered < work.Span.Length || local.Count == 0)
                unknown = true;
            if (work.Span.Length == 0 && local.Count > 1)
            {
                var first = local[0].Origin;
                for (var index = 1; index < local.Count; index++)
                    if (!first.Source.Matches(local[index].Origin.Source) || first.Span != local[index].Origin.Span)
                        unknown = true;
                local.RemoveRange(1, local.Count - 1);
            }
            for (var index = local.Count - 1; index >= 0; index--)
            {
                var item = local[index];
                var source = item.Origin.Source;
                if (maps.TryGetValue((source.SourceId, source.Version, source.TextHash), out var previous))
                {
                    var cyclic = false;
                    foreach (var ancestor in work.Path)
                        cyclic |= ancestor.Matches(source);
                    if (cyclic)
                    {
                        unknown = true;
                        continue;
                    }
                    var path = new List<MdSourceSnapshot>(work.Path) { source };
                    pending.Push((previous, null, item.Origin.Span, item.Atomic, path));
                }
                else
                    pending.Push((null, item.Origin, item.Origin.Span, item.Atomic, work.Path));
            }
        }
        return new MdSourceMapResult(origins, origins.Count == 0 ? MdMappingPrecision.Unknown
            : unknown ? MdMappingPrecision.Partial : atomic ? MdMappingPrecision.CoveringTokens : MdMappingPrecision.Exact);

        void Append(MdSourceRange range)
        {
            if (origins.Count != 0 && origins[origins.Count - 1] is var last && last.Source.Matches(range.Source)
                && last.Span.End == range.Span.Start)
                origins[origins.Count - 1] = new MdSourceRange(range.Source, new MdRawRange(last.Span.Start, range.Span.End - last.Span.Start));
            else
                origins.Add(range);
        }
    }
}

internal sealed class MdSourceEdit
{
    internal MdSourceEdit(MdSourceRange target, string expectedText, string replacement)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ExpectedText = expectedText ?? throw new ArgumentNullException(nameof(expectedText));
        Replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
        if (!target.Source.IsBoundary(target.Span.Start) || !target.Source.IsBoundary(target.Span.End)
            || MdHash.InvalidUnicode(replacement, CancellationToken.None) >= 0)
            throw new ArgumentException("An edit must preserve Unicode scalar boundaries.", nameof(target));
        if (target.Span.Length != expectedText.Length || string.CompareOrdinal(target.Source.Text,
            target.Span.Start, expectedText, 0, expectedText.Length) != 0)
            throw new ArgumentException("Expected text differs from the target snapshot.", nameof(expectedText));
    }
    internal MdSourceRange Target { get; }
    internal string ExpectedText { get; }
    internal string Replacement { get; }
    internal bool IsApplicable(MdSourceSnapshot current) => Target.Source.Matches(current)
        && Target.Span.End <= current.Text.Length && string.CompareOrdinal(current.Text,
            Target.Span.Start, ExpectedText, 0, ExpectedText.Length) == 0;
}
