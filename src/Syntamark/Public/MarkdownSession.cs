using System;
using System.Collections.Generic;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

/// <summary>An edit anchored to the previous session's complete UTF-16 source.</summary>
public readonly record struct MarkdownTextEdit(RawSpan Span, string Replacement);

/// <summary>Indicates actual paragraph reuse or canonical full parsing.</summary>
public enum MarkdownSessionMode { Full, Delta }

/// <summary>Stable reasons for taking the canonical full parser path.</summary>
public enum MarkdownSessionFallback
{
    None, InitialParse, PreviousNotComplete, OptionsChanged, UnsupportedSyntax,
    UnsupportedEdit, NoReusableBlocks, ResourceBoundary,
}

/// <summary>An opt-in, immutable parser session. Facts and syntax are rebuilt for each source version.</summary>
public sealed class MarkdownParseSession
{
    private readonly MdParseSession core;

    internal MarkdownParseSession(MdParseSession core, CancellationToken cancellationToken)
    {
        this.core = core;
        Document = new MarkdownDocument(core.Document.Facts);
        Root = core.Document.Root is null ? null : MarkdownSyntaxNode.From(core.Document.Root, core.Document.Facts.RawText, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public MarkdownDocument Document { get; }
    public MarkdownSyntaxNode? Root { get; }
    public MarkdownSessionMode Mode => (MarkdownSessionMode)core.Mode;
    public MarkdownSessionFallback Fallback => (MarkdownSessionFallback)core.Fallback;
    public int ReusedBlocks => core.ReusedBlocks;
    /// <summary>Reused paragraph payloads whose source positions were rebased after a length change.</summary>
    public int RebasedBlocks => core.RebasedBlocks;
    /// <summary>Parsed eligible plain paragraphs; other blocks and document-wide fact/syntax projection are not counted.</summary>
    public int ParsedBlocks => core.ParsedBlocks;
    /// <summary>Logical canonical parser debits, including replayed work, not executed work or timing.</summary>
    public long ScanUnits => core.ScanUnits;
    public int OutputItems => core.OutputItems;

    /// <summary>Creates a new session from sorted disjoint edits, retaining this session on failure or cancellation.</summary>
    /// <remarks>Null options retain the previous options. Hash mismatch and invalid edit ranges are rejected.</remarks>
    public MarkdownParseSession ApplyEdits(string? expectedTextHash, IReadOnlyList<MarkdownTextEdit> edits,
        string? sourceVersion = null, MarkdownParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (edits is null)
            throw new ArgumentNullException(nameof(edits));
        var converted = new MdTextEdit[edits.Count];
        for (var i = 0; i < converted.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edit = edits[i];
            converted[i] = new MdTextEdit(new MdRawRange(edit.Span.Start, edit.Span.Length), edit.Replacement);
        }
        return new MarkdownParseSession(core.ApplyEdits(expectedTextHash, converted, sourceVersion, options?.Core,
            cancellationToken), cancellationToken);
    }
}

/// <summary>An immutable editing session that retains the requested analysis outputs from the same parse.</summary>
public sealed class MarkdownAnalysisSession
{
    private readonly MdAnalysisSession core;

    internal MarkdownAnalysisSession(MdAnalysisSession core, CancellationToken cancellationToken)
    {
        this.core = core;
        Analysis = new MarkdownAnalysisResult(core.Analysis, cancellationToken);
    }

    /// <summary>Inspect availability before drawing conclusions from missing selected outputs.</summary>
    public MarkdownAnalysisResult Analysis { get; }
    public MarkdownSessionMode Mode => (MarkdownSessionMode)core.Mode;
    public MarkdownSessionFallback Fallback => (MarkdownSessionFallback)core.Fallback;
    public int ReusedBlocks => core.ReusedBlocks;
    public int RebasedBlocks => core.RebasedBlocks;
    /// <summary>Parsed eligible plain paragraphs, excluding other blocks and document-wide projection.</summary>
    public int ParsedBlocks => core.ParsedBlocks;
    /// <summary>Canonical logical debits including replayed work, not timing or executed work.</summary>
    public long ScanUnits => core.ScanUnits;
    public int OutputItems => core.OutputItems;

    /// <summary>Applies edits in the previous input's coordinates, preserving the original selection on reuse and fallback.</summary>
    public MarkdownAnalysisSession ApplyEdits(string? expectedTextHash, IReadOnlyList<MarkdownTextEdit> edits,
        string? sourceVersion = null, MarkdownParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (edits is null)
            throw new ArgumentNullException(nameof(edits));
        var converted = new MdTextEdit[edits.Count];
        for (var i = 0; i < converted.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edit = edits[i];
            converted[i] = new MdTextEdit(new MdRawRange(edit.Span.Start, edit.Span.Length), edit.Replacement);
        }
        return new MarkdownAnalysisSession(core.ApplyEdits(expectedTextHash, converted, sourceVersion,
            options?.Core, cancellationToken), cancellationToken);
    }
}

public static partial class MarkdownParser
{
    /// <summary>Creates a selected-analysis session. Include UnsupportedSyntax explicitly when candidate diagnostics are needed.</summary>
    /// <remarks>Legacy rendering blocks remain available through the portable/assembly-friend analysis result; no second public AST is exposed.</remarks>
    public static MarkdownAnalysisSession CreateAnalysisSession(string rawText, MarkdownAnalysisParts parts,
        string scopeId, string sourceId, string? sourceVersion = null, MarkdownParseOptions? options = null,
        CancellationToken cancellationToken = default)
        => new(MdParseSession.CreateForAnalysis(rawText, (MdAnalysisParts)parts, scopeId, sourceId, sourceVersion,
            options?.Core ?? defaultOptions, cancellationToken), cancellationToken);

    /// <summary>Creates an opt-in session without changing the existing Parse or ParseWithSyntax contracts.</summary>
    public static MarkdownParseSession CreateSession(string rawText, string scopeId, string sourceId,
        string? sourceVersion = null, MarkdownParseOptions? options = null, CancellationToken cancellationToken = default)
        => new(MdParseSession.Create(rawText, scopeId, sourceId, sourceVersion, options?.Core ?? defaultOptions,
            cancellationToken), cancellationToken);
}
