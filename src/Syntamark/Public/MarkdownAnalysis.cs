using System;
using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

[Flags]
public enum MarkdownAnalysisParts
{
    None = 0,
    FrontMatter = 1,
    Headings = 2,
    Sections = 4,
    Links = 8,
    Fences = 16,
    TextRegions = 32,
    Syntax = 64,
    AllFacts = 63,
    All = 127,
}

public enum MarkdownAnalysisAvailability { NotRequested, Complete, Partial, Unavailable }

/// <summary>Immutable selected outputs. Null outputs are not requested or unavailable; use GetAvailability to distinguish them.</summary>
public sealed class MarkdownAnalysisResult
{
    private readonly MarkdownDocument document;
    private readonly MarkdownAnalysisAvailability[] availability;

    internal MarkdownAnalysisResult(MdAnalysisResult result, CancellationToken cancellationToken)
    {
        RequestedParts = (MarkdownAnalysisParts)result.RequestedParts;
        AnalysisHash = result.AnalysisHash;
        availability = new MarkdownAnalysisAvailability[7];
        for (var i = 0; i < availability.Length; i++)
            availability[i] = (MarkdownAnalysisAvailability)result.GetAvailability((MdAnalysisParts)(1 << i));
        document = result.PublishSelected(static facts => new MarkdownDocument(facts));
        FrontMatter = HasOutput(MarkdownAnalysisParts.FrontMatter) ? document.FrontMatter : null;
        Headings = HasOutput(MarkdownAnalysisParts.Headings) ? document.Headings : null;
        Sections = HasOutput(MarkdownAnalysisParts.Sections) ? document.Sections : null;
        Links = HasOutput(MarkdownAnalysisParts.Links) ? document.Links : null;
        Fences = HasOutput(MarkdownAnalysisParts.Fences) ? document.Fences : null;
        TextRegions = HasOutput(MarkdownAnalysisParts.TextRegions) ? document.TextRegions : null;
        Root = HasOutput(MarkdownAnalysisParts.Syntax) && result.Root is { } root
            ? MarkdownSyntaxNode.From(root, result.RawText, cancellationToken) : null;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public string ScopeId => document.ScopeId;
    public string SourceId => document.SourceId;
    public string? SourceVersion => document.SourceVersion;
    public string ParserVersion => document.ParserVersion;
    public string ProfileId => document.ProfileId;
    public string ContractVersion => document.ContractVersion;
    public string OptionsHash => document.OptionsHash;
    public string? TextHash => document.TextHash;
    public MarkdownParseStatus Status => document.Status;
    public RawSpan? BodySpan => document.BodySpan;
    public MarkdownAnalysisParts RequestedParts { get; }
    public int AnalysisSchemaVersion => 1;
    public string AnalysisHash { get; }
    public MarkdownFrontMatter? FrontMatter { get; }
    public IReadOnlyList<MarkdownHeading>? Headings { get; }
    public IReadOnlyList<MarkdownSection>? Sections { get; }
    public IReadOnlyList<MarkdownLink>? Links { get; }
    public IReadOnlyList<MarkdownFence>? Fences { get; }
    public IReadOnlyList<MarkdownTextRegion>? TextRegions { get; }
    public MarkdownSyntaxNode? Root { get; }
    public IReadOnlyList<MarkdownDiagnostic> Diagnostics => document.Diagnostics;
    public IReadOnlyList<MarkdownCapability> Capabilities => document.Capabilities;
    /// <summary>Fact projection coverage and mandatory validation. Check each syntax node's nullable RawSpan separately.</summary>
    public MarkdownCoverage Coverage => document.Coverage;
    public MarkdownLinePosition GetLinePosition(int rawOffset) => document.GetLinePosition(rawOffset);

    /// <summary>Accepts exactly one primitive part, excluding None, AllFacts and All.</summary>
    public MarkdownAnalysisAvailability GetAvailability(MarkdownAnalysisParts part) => availability[part switch
    {
        MarkdownAnalysisParts.FrontMatter => 0,
        MarkdownAnalysisParts.Headings => 1,
        MarkdownAnalysisParts.Sections => 2,
        MarkdownAnalysisParts.Links => 3,
        MarkdownAnalysisParts.Fences => 4,
        MarkdownAnalysisParts.TextRegions => 5,
        MarkdownAnalysisParts.Syntax => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(part), "Select exactly one analysis part."),
    }];

    private bool HasOutput(MarkdownAnalysisParts part) => GetAvailability(part) is
        MarkdownAnalysisAvailability.Complete or MarkdownAnalysisAvailability.Partial;
}

public static partial class MarkdownParser
{
    /// <summary>Analyzes the selected outputs eagerly. Inspect availability before using absence-based conclusions.</summary>
    public static MarkdownAnalysisResult Analyze(string rawText, MarkdownAnalysisParts parts, string scopeId, string sourceId,
        string? sourceVersion = null, MarkdownParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = MdParser.Analyze(rawText, (MdAnalysisParts)parts, scopeId, sourceId, sourceVersion,
            options?.Core ?? defaultOptions, cancellationToken);
        return new MarkdownAnalysisResult(result, cancellationToken);
    }
}
