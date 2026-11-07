using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

public enum MarkdownFrontMatterScanState { Absent, Closed, Unterminated, Unknown }

/// <summary>Header delimiter evidence only. Complete does not validate YAML or the Markdown body.</summary>
public sealed class MarkdownFrontMatterScanResult
{
    internal MarkdownFrontMatterScanResult(MdFrontMatterScanResult result)
    {
        ContractVersion = result.ContractVersion;
        ParserVersion = result.ParserVersion;
        ProfileId = result.ProfileId;
        OptionsHash = result.OptionsHash;
        Status = (MarkdownParseStatus)result.Status;
        State = (MarkdownFrontMatterScanState)result.State;
        OpeningSpan = global::Syntamark.RawSpan.From(result.OpeningSpan);
        YamlSpan = global::Syntamark.RawSpan.From(result.YamlSpan);
        ClosingSpan = global::Syntamark.RawSpan.From(result.ClosingSpan);
        BodySpan = global::Syntamark.RawSpan.From(result.BodySpan);
        HeaderSpan = global::Syntamark.RawSpan.From(result.HeaderSpan);
        FailureSpan = global::Syntamark.RawSpan.From(result.FailureSpan);
        FailureLine = result.FailureLine;
        Diagnostics = MarkdownCollections.Convert(result.Diagnostics, static diagnostic => new MarkdownDiagnostic(diagnostic));
    }

    public string ContractVersion { get; }
    public string ParserVersion { get; }
    public string ProfileId { get; }
    public string OptionsHash { get; }
    public MarkdownParseStatus Status { get; }
    public MarkdownFrontMatterScanState State { get; }
    public RawSpan? OpeningSpan { get; }
    public RawSpan? YamlSpan { get; }
    public RawSpan? ClosingSpan { get; }
    public RawSpan? BodySpan { get; }
    public RawSpan? HeaderSpan { get; }
    public RawSpan? FailureSpan { get; }
    public int? FailureLine { get; }
    public IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; }
}

public static partial class MarkdownParser
{
    /// <summary>Scans a document header without parsing YAML or inspecting the body. Body input options are rejected.</summary>
    public static MarkdownFrontMatterScanResult ScanFrontMatter(string rawText, MarkdownParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MarkdownFrontMatterScanResult(MdParser.ScanFrontMatter(rawText, options?.Core ?? defaultOptions, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
