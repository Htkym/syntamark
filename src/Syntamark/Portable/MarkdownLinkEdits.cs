using System;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Syntamark.Compilation;

internal enum MdLinkEditFailure
{
    None, SnapshotMismatch, AnalysisIncomplete, LinksUnavailable, LinkNotFound,
    UnresolvedReference, UnknownDestinationRange, UnsupportedLinkKind, UnsupportedProfile,
    EmptyDestination, InvalidDestination,
}

internal static class MdLinkEdits
{
    internal static bool TryCreateDestinationEdit(MdSourceSnapshot source, MdAnalysisResult analysis,
        int linkIndex, string destination, out MdSourceEdit? edit, out MdLinkEditFailure failure,
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
        failure = ValidateAnalysis(source, analysis.SourceId, analysis.SourceVersion, analysis.TextHash,
            analysis.ParserVersion, analysis.Status, analysis.GetAvailability(MdAnalysisParts.Links));
        if (failure != MdLinkEditFailure.None)
            return false;
        if (linkIndex < 0 || linkIndex >= analysis.Links!.Count)
        {
            failure = MdLinkEditFailure.LinkNotFound;
            return false;
        }
        var link = analysis.Links[linkIndex];
        return TryCreate(source, analysis.ProfileId, link.Kind, link.Resolution, link.TargetRawSpan,
            destination, out edit, out failure, cancellationToken);
    }

    internal static MdLinkEditFailure ValidateAnalysis(MdSourceSnapshot source, string sourceId,
        string? sourceVersion, string? textHash, string parserVersion, MdParseStatus status,
        MdAnalysisAvailability linksAvailability)
    {
        if (source.SourceId != sourceId || source.Version != sourceVersion || source.TextHash != textHash
            || parserVersion != MdParserVersion.Value)
            return MdLinkEditFailure.SnapshotMismatch;
        if (status != MdParseStatus.Complete)
            return MdLinkEditFailure.AnalysisIncomplete;
        return linksAvailability == MdAnalysisAvailability.Complete
            ? MdLinkEditFailure.None : MdLinkEditFailure.LinksUnavailable;
    }

    // Both facades select the link from the supplied analysis before entering this shared encoder.
    internal static bool TryCreate(MdSourceSnapshot source, string profileId, MdLinkKind kind,
        MdLinkResolution resolution, MdRawRange? target, string destination, out MdSourceEdit? edit,
        out MdLinkEditFailure failure, CancellationToken cancellationToken)
    {
        edit = null;
        failure = MdLinkEditFailure.None;
        if (profileId is not ("lithosharp-markdown/1" or "commonmark/0.31.2" or "syntamark-docs/2"))
            failure = MdLinkEditFailure.UnsupportedProfile;
        else if (resolution == MdLinkResolution.UnresolvedReference)
            failure = MdLinkEditFailure.UnresolvedReference;
        else if (kind is not (MdLinkKind.Inline or MdLinkKind.ReferenceUse or MdLinkKind.ReferenceDefinition))
            failure = MdLinkEditFailure.UnsupportedLinkKind;
        else if (target is not { } span || span.End > source.Text.Length
            || !source.IsBoundary(span.Start) || !source.IsBoundary(span.End))
            failure = MdLinkEditFailure.UnknownDestinationRange;
        else if (destination.Length == 0)
            failure = MdLinkEditFailure.EmptyDestination;
        else if (MdHash.InvalidUnicode(destination, cancellationToken) >= 0)
            failure = MdLinkEditFailure.InvalidDestination;
        if (failure != MdLinkEditFailure.None)
            return false;

        var replacement = new StringBuilder(destination.Length);
        for (var index = 0; index < destination.Length; index++)
        {
            if ((index & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var value = destination[index];
            if (char.IsControl(value))
            {
                failure = MdLinkEditFailure.InvalidDestination;
                return false;
            }
            // Character references work in bare/angle destinations and survive table splitting.
            if (value is '&' or '\\' or '(' or ')' or '<' or '>' or '"' or '\'' or '`' or '[' or ']' or '|'
                || char.IsWhiteSpace(value))
                replacement.Append("&#").Append(((int)value).ToString(CultureInfo.InvariantCulture)).Append(';');
            else
                replacement.Append(value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var range = target!.Value;
        edit = new MdSourceEdit(new MdSourceRange(source, range),
            source.Text.Substring(range.Start, range.Length), replacement.ToString());
        return true;
    }
}
