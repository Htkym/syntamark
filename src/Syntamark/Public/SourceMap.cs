using System;
using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

/// <summary>An immutable UTF-16 source snapshot, identified by source, version and text hash.</summary>
public sealed class SourceSnapshot
{
    public SourceSnapshot(string sourceId, string version, string text) => Core = new(sourceId, version, text);
    internal SourceSnapshot(MdSourceSnapshot core) => Core = core;
    internal MdSourceSnapshot Core { get; }
    public string SourceId => Core.SourceId;
    public string Version => Core.Version;
    public string Text => Core.Text;
    public string TextHash => Core.TextHash;
}

/// <summary>A range in a specific immutable source snapshot.</summary>
public sealed class SourceRange
{
    public SourceRange(SourceSnapshot source, RawSpan span)
        => Core = new((source ?? throw new ArgumentNullException(nameof(source))).Core, new(span.Start, span.Length));
    internal SourceRange(MdSourceRange core) => Core = core;
    internal MdSourceRange Core { get; }
    public SourceSnapshot Source => new(Core.Source);
    public RawSpan Span => RawSpan.From(Core.Span);
}

/// <summary>One linear, token-covering or generated part of a transformed snapshot.</summary>
public sealed class SourceMapSegment
{
    public SourceMapSegment(RawSpan outputSpan, SourceRange? origin, MarkdownSegmentKind kind)
        => Core = new(new(outputSpan.Start, outputSpan.Length), origin?.Core, (MdSegmentKind)kind);
    internal MdSourceMapSegment Core { get; }
    public RawSpan OutputSpan => RawSpan.From(Core.OutputSpan);
    public SourceRange? Origin => Core.Origin is null ? null : new(Core.Origin);
    public MarkdownSegmentKind Kind => (MarkdownSegmentKind)Core.Kind;
}

/// <summary>Original fragments in output order. Only one exact fragment permits a direct edit.</summary>
public sealed class SourceMapResult
{
    internal SourceMapResult(MdSourceMapResult result)
    {
        Origins = MarkdownCollections.Convert(result.Origins, static range => new SourceRange(range));
        Precision = (MarkdownMappingPrecision)result.Precision;
        CanEdit = result.CanEdit;
    }
    public IReadOnlyList<SourceRange> Origins { get; }
    public MarkdownMappingPrecision Precision { get; }
    public bool CanEdit { get; }
}

/// <summary>Composes position mappings across includes, decoding and generated text without file access.</summary>
public sealed class SourceMap
{
    public SourceMap(SourceSnapshot output, IReadOnlyList<SourceMapSegment> segments)
    {
        if (segments is null)
            throw new ArgumentNullException(nameof(segments));
        var converted = new MdSourceMapSegment[segments.Count];
        for (var index = 0; index < converted.Length; index++)
            converted[index] = (segments[index] ?? throw new ArgumentException("A segment is null.", nameof(segments))).Core;
        Core = new((output ?? throw new ArgumentNullException(nameof(output))).Core, converted);
    }
    private SourceMap(MdSourceMap core) => Core = core;
    internal MdSourceMap Core { get; }
    public SourceSnapshot Output => new(Core.Output);
    public SourceMap Compose(IReadOnlyList<SourceMap> previous)
    {
        if (previous is null)
            throw new ArgumentNullException(nameof(previous));
        var converted = new MdSourceMap[previous.Count];
        for (var index = 0; index < converted.Length; index++)
            converted[index] = (previous[index] ?? throw new ArgumentException("An upstream map is null.", nameof(previous))).Core;
        return new(Core.Compose(converted));
    }
    public SourceMapResult Map(RawSpan outputSpan, CancellationToken cancellationToken = default)
        => new(Core.Map(new(outputSpan.Start, outputSpan.Length), cancellationToken));
}

/// <summary>A proposed edit guarded by source version, text hash and expected original text. Applying it is the caller's responsibility.</summary>
public sealed class SourceEdit
{
    public SourceEdit(SourceRange target, string expectedText, string replacement)
        => Core = new((target ?? throw new ArgumentNullException(nameof(target))).Core, expectedText, replacement);
    private MdSourceEdit Core { get; }
    public SourceRange Target => new(Core.Target);
    public string ExpectedText => Core.ExpectedText;
    public string Replacement => Core.Replacement;
    public bool IsApplicable(SourceSnapshot current)
        => Core.IsApplicable((current ?? throw new ArgumentNullException(nameof(current))).Core);
}
