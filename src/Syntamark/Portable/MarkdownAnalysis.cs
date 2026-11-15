using System;
using System.Collections.Generic;
using System.Globalization;

namespace Syntamark.Compilation;

[Flags]
internal enum MdAnalysisParts
{
    None = 0,
    FrontMatter = 1,
    Headings = 2,
    Sections = 4,
    Links = 8,
    Fences = 16,
    TextRegions = 32,
    Syntax = 64,
    UnsupportedSyntax = 128,
    AllFacts = 63,
    All = 127,
}

internal enum MdAnalysisAvailability
{
    NotRequested,
    Complete,
    Partial,
    Unavailable,
}

internal sealed class MdAnalysisResult
{
    private readonly MdDocumentFacts facts;

    internal MdAnalysisResult(MdDocumentFacts facts, MdSyntaxNode? root, MdAnalysisParts requestedParts)
        : this(facts, root, requestedParts, MdFreeze.Empty<MdUnsupportedSyntaxCandidate>(), null)
    {
    }

    internal MdAnalysisResult(MdDocumentFacts facts, MdSyntaxNode? root, MdAnalysisParts requestedParts,
        IReadOnlyList<MdUnsupportedSyntaxCandidate> unsupportedSyntax, IReadOnlyList<LithoBlock>? blocks)
    {
        if (facts is null)
            throw new ArgumentNullException(nameof(facts));
        Validate(requestedParts);
        this.facts = facts;
        Root = root;
        RequestedParts = requestedParts;
        UnsupportedSyntax = HasOutput(MdAnalysisParts.UnsupportedSyntax) ? MdFreeze.Copy(unsupportedSyntax) : null;
        Blocks = blocks is null ? null : MdFreeze.Copy(blocks);
        AnalysisHash = MdHash.Compute("{\"analysisSchemaVersion\":1,\"optionsHash\":\"" + facts.OptionsHash
            + "\",\"requestedParts\":" + ((int)requestedParts).ToString(CultureInfo.InvariantCulture) + "}");
    }

    internal int AnalysisSchemaVersion => 1;
    internal string RawText => facts.RawText;
    internal string ScopeId => facts.ScopeId;
    internal string SourceId => facts.SourceId;
    internal string? SourceVersion => facts.SourceVersion;
    internal string ParserVersion => facts.ParserVersion;
    internal string ProfileId => facts.ProfileId;
    internal string YamlProfileId => facts.YamlProfileId;
    internal string ContractVersion => facts.ContractVersion;
    internal string OptionsHash => facts.OptionsHash;
    internal string? TextHash => facts.TextHash;
    internal MdParseStatus Status => facts.Status;
    internal MdRawRange? BodySpan => facts.BodySpan;
    internal IReadOnlyList<MdDiagnostic> Diagnostics => facts.Diagnostics;
    internal IReadOnlyList<MdCapability> Capabilities => facts.Capabilities;
    internal MdCoverage Coverage => facts.Coverage;
    internal MdFrontMatter? FrontMatter => HasOutput(MdAnalysisParts.FrontMatter) ? facts.FrontMatter : null;
    internal IReadOnlyList<MdHeading>? Headings => HasOutput(MdAnalysisParts.Headings) ? facts.Headings : null;
    internal IReadOnlyList<MdSection>? Sections => HasOutput(MdAnalysisParts.Sections) ? facts.Sections : null;
    internal IReadOnlyList<MdLink>? Links => HasOutput(MdAnalysisParts.Links) ? facts.Links : null;
    internal IReadOnlyList<MdFence>? Fences => HasOutput(MdAnalysisParts.Fences) ? facts.Fences : null;
    internal IReadOnlyList<MdTextRegion>? TextRegions => HasOutput(MdAnalysisParts.TextRegions) ? facts.TextRegions : null;
    internal MdSyntaxNode? Root { get; }
    internal IReadOnlyList<MdUnsupportedSyntaxCandidate>? UnsupportedSyntax { get; }
    // Body-relative legacy rendering blocks are retained by AnalyzeWithBlocks and analysis sessions.
    internal IReadOnlyList<LithoBlock>? Blocks { get; }
    internal MdAnalysisParts RequestedParts { get; }
    internal string AnalysisHash { get; }

    // Selected publication data must not enter a cache for complete facts.
    internal TResult PublishSelected<TResult>(Func<MdDocumentFacts, TResult> publish) => publish(facts);

    internal static void Validate(MdAnalysisParts parts)
    {
        if ((parts & ~(MdAnalysisParts.All | MdAnalysisParts.UnsupportedSyntax)) != 0)
            throw new ArgumentOutOfRangeException(nameof(parts));
    }

    internal MdAnalysisAvailability GetAvailability(MdAnalysisParts part)
    {
        var value = (int)part;
        if (value <= 0 || (part & ~(MdAnalysisParts.All | MdAnalysisParts.UnsupportedSyntax)) != 0 || (value & (value - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(part));
        if ((RequestedParts & part) == 0)
            return MdAnalysisAvailability.NotRequested;
        if (facts.Status == MdParseStatus.Failed || (part == MdAnalysisParts.Syntax && Root is null))
            return MdAnalysisAvailability.Unavailable;
        return facts.Status == MdParseStatus.Partial
            ? MdAnalysisAvailability.Partial : MdAnalysisAvailability.Complete;
    }

    private bool HasOutput(MdAnalysisParts part) => GetAvailability(part) is
        MdAnalysisAvailability.Complete or MdAnalysisAvailability.Partial;
}
