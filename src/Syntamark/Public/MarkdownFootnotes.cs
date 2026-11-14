using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

/// <summary>A footnote definition with its original label, position and first-reference display number.</summary>
public sealed class MarkdownFootnoteDefinition
{
    internal MarkdownFootnoteDefinition(MdFootnoteDefinitionFacts facts)
    {
        Label = facts.Label;
        NormalizedLabel = facts.NormalizedLabel;
        Number = facts.Number;
        IsDuplicate = facts.IsDuplicate;
        RawSpan = global::Syntamark.RawSpan.From(facts.RawSpan);
        LabelRawSpan = global::Syntamark.RawSpan.From(facts.LabelRawSpan);
        ContentRawSpans = MarkdownCollections.Convert(facts.ContentRawSpans, static span => global::Syntamark.RawSpan.From(span));
    }
    public string Label { get; }
    public string NormalizedLabel { get; }
    /// <summary>Null when no resolved reference uses this label.</summary>
    public int? Number { get; }
    public bool IsDuplicate { get; }
    public RawSpan RawSpan { get; }
    public RawSpan LabelRawSpan { get; }
    /// <summary>The marker-inclusive envelope of each definition block, preserving gaps between blocks.</summary>
    public IReadOnlyList<RawSpan> ContentRawSpans { get; }
}

/// <summary>A footnote use. Number and occurrence allow consumers to produce repeated-reference backlinks.</summary>
public sealed class MarkdownFootnoteReference
{
    internal MarkdownFootnoteReference(MdFootnoteReferenceFacts facts)
    {
        Label = facts.Label;
        NormalizedLabel = facts.NormalizedLabel;
        Number = facts.Number;
        Occurrence = facts.Occurrence;
        RawSpan = global::Syntamark.RawSpan.From(facts.RawSpan);
        LabelRawSpan = global::Syntamark.RawSpan.From(facts.LabelRawSpan);
    }
    public string Label { get; }
    public string NormalizedLabel { get; }
    public int? Number { get; }
    public int Occurrence { get; }
    public bool IsResolved => Number.HasValue;
    public RawSpan RawSpan { get; }
    public RawSpan LabelRawSpan { get; }
}

/// <summary>Footnotes and regular facts produced from the same snapshot without retaining a public syntax tree.</summary>
public sealed class MarkdownFootnoteDocument
{
    internal MarkdownFootnoteDocument(MdFootnoteDocument document, CancellationToken cancellationToken)
    {
        Analysis = new MarkdownAnalysisResult(document.Analysis, cancellationToken);
        Definitions = MarkdownCollections.Convert(document.Definitions, static definition => new MarkdownFootnoteDefinition(definition));
        References = MarkdownCollections.Convert(document.References, static reference => new MarkdownFootnoteReference(reference));
        cancellationToken.ThrowIfCancellationRequested();
    }
    public MarkdownAnalysisResult Analysis { get; }
    public IReadOnlyList<MarkdownFootnoteDefinition> Definitions { get; }
    public IReadOnlyList<MarkdownFootnoteReference> References { get; }
    public bool IsEnabled => Analysis.ProfileId == "syntamark-docs/2";
}

public static partial class MarkdownParser
{
    /// <summary>Analyzes footnotes using syntamark-docs/2 by default. Other profiles keep their existing literal behavior.</summary>
    public static MarkdownFootnoteDocument AnalyzeFootnotes(string rawText, string scopeId, string sourceId,
        string? sourceVersion = null, MarkdownParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = MdFootnotes.Analyze(rawText, scopeId, sourceId, sourceVersion,
            options?.Core ?? new MdOptions(profileId: "syntamark-docs/2"), cancellationToken);
        return new MarkdownFootnoteDocument(result, cancellationToken);
    }
}
