using System;
using System.Threading;
using Syntamark.Compilation;

namespace Syntamark;

/// <summary>Stable reasons why a destination edit cannot be proposed.</summary>
public enum MarkdownLinkEditFailure
{
    None, SnapshotMismatch, AnalysisIncomplete, LinksUnavailable, LinkNotFound,
    UnresolvedReference, UnknownDestinationRange, UnsupportedLinkKind, UnsupportedProfile,
    EmptyDestination, InvalidDestination,
}

/// <summary>Creates guarded destination-only edits without file access, URI normalization or another parse.</summary>
public static class MarkdownLinkEdits
{
    /// <summary>Selects a link by index in this analysis, preserving its label, title and delimiters.</summary>
    /// <remarks>Requires an explicit matching source version and complete Links. Reference uses edit the shared definition.
    /// Autolinks, empty new destinations, control characters and unknown ranges are rejected. Apply shared edits once.</remarks>
    public static bool TryCreateDestinationEdit(SourceSnapshot source, MarkdownAnalysisResult analysis,
        int linkIndex, string destination, out SourceEdit? edit, out MarkdownLinkEditFailure failure,
        CancellationToken cancellationToken = default)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (analysis is null)
            throw new ArgumentNullException(nameof(analysis));
        if (destination is null)
            throw new ArgumentNullException(nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        edit = null;
        failure = (MarkdownLinkEditFailure)MdLinkEdits.ValidateAnalysis(source.Core, analysis.SourceId,
            analysis.SourceVersion, analysis.TextHash, analysis.ParserVersion, (MdParseStatus)analysis.Status,
            (MdAnalysisAvailability)analysis.GetAvailability(MarkdownAnalysisParts.Links));
        if (failure != MarkdownLinkEditFailure.None)
            return false;
        if (linkIndex < 0 || linkIndex >= analysis.Links!.Count)
        {
            failure = MarkdownLinkEditFailure.LinkNotFound;
            return false;
        }
        var link = analysis.Links[linkIndex];
        var success = MdLinkEdits.TryCreate(source.Core, analysis.ProfileId, (MdLinkKind)link.Kind,
            (MdLinkResolution)link.Resolution, link.TargetRawSpan is { } span ? new MdRawRange(span.Start, span.Length) : null,
            destination, out var proposed, out var reason, cancellationToken);
        failure = (MarkdownLinkEditFailure)reason;
        edit = proposed is null ? null : new SourceEdit(proposed);
        return success;
    }
}
