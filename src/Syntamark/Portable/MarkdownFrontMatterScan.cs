using System;
using System.Collections.Generic;
using System.Threading;

namespace Syntamark.Compilation;

/// <summary>Delimiter facts shared by strict optional parsing and the site compatibility adapter.</summary>
internal readonly record struct MdFrontMatterEnvelope(
    MdRawRange? Opening, MdRawRange? Yaml, MdRawRange? Closing, MdRawRange? Body, int FailureLine);

internal enum MdFrontMatterScanState { Absent, Closed, Unterminated, Unknown }

internal sealed class MdFrontMatterScanResult
{
    internal MdFrontMatterScanResult(int rawLength, MdOptions options, MdParseStatus status,
        MdFrontMatterScanState state, MdFrontMatterEnvelope? envelope, MdDiagnostic? diagnostic)
    {
        ParserVersion = MdParserVersion.Value;
        ProfileId = options.ProfileId;
        OptionsHash = options.Hash;
        Status = status;
        State = state;
        OpeningSpan = envelope?.Opening;
        YamlSpan = envelope?.Yaml;
        ClosingSpan = envelope?.Closing;
        BodySpan = envelope?.Body;
        HeaderSpan = envelope.HasValue ? new MdRawRange(0, envelope.Value.Body?.Start ?? rawLength) : null;
        FailureSpan = state == MdFrontMatterScanState.Unterminated ? new MdRawRange(rawLength, 0) : null;
        FailureLine = state == MdFrontMatterScanState.Unterminated ? envelope?.FailureLine : null;
        Diagnostics = diagnostic is null ? MdFreeze.Empty<MdDiagnostic>() : MdFreeze.Owned(new[] { diagnostic });
    }

    internal string ContractVersion => "frontmatter-scan/1";
    internal string ParserVersion { get; }
    internal string ProfileId { get; }
    internal string OptionsHash { get; }
    internal MdParseStatus Status { get; }
    internal MdFrontMatterScanState State { get; }
    internal MdRawRange? OpeningSpan { get; }
    internal MdRawRange? YamlSpan { get; }
    internal MdRawRange? ClosingSpan { get; }
    internal MdRawRange? BodySpan { get; }
    internal MdRawRange? HeaderSpan { get; }
    internal MdRawRange? FailureSpan { get; }
    internal int? FailureLine { get; }
    internal IReadOnlyList<MdDiagnostic> Diagnostics { get; }
}

internal static class MdFrontMatterScan
{
    internal static MdFrontMatterEnvelope Scan(string raw, CancellationToken cancellationToken, MdParseContext? context = null)
        => ScanCore(raw, cancellationToken, context, null);

    internal static MdFrontMatterScanResult AnalyzeHeader(string raw, MdOptions options, CancellationToken cancellationToken)
    {
        if (raw is null)
            throw new ArgumentNullException(nameof(raw));
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (options.InputKind != MdInputKind.Document)
            throw new ArgumentException("Front matter scanning requires document input.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        var context = new MdParseContext(options, cancellationToken);
        try
        {
            var envelope = options.IsCommonMark
                ? new MdFrontMatterEnvelope(null, null, null, new MdRawRange(0, raw.Length), 1)
                : ScanCore(raw, cancellationToken, context, options.MaxInputUtf16);
            var headerLength = envelope.Body?.Start ?? raw.Length;
            context.Scan(headerLength);
            var invalid = MdHash.InvalidUnicode(raw.Substring(0, headerLength), cancellationToken);
            if (invalid >= 0)
                return Finish(MdParseStatus.Failed, MdFrontMatterScanState.Unknown, null,
                    new MdDiagnostic("LMD001", MdDiagnosticSeverity.Error, "Front matter prefix contains an unpaired surrogate.",
                        new MdRawRange(invalid, 1), "input", "invalidUnicode"));
            var state = !envelope.Opening.HasValue ? MdFrontMatterScanState.Absent
                : envelope.Closing.HasValue ? MdFrontMatterScanState.Closed : MdFrontMatterScanState.Unterminated;
            var diagnostic = state == MdFrontMatterScanState.Unterminated
                ? new MdDiagnostic("LMD004", MdDiagnosticSeverity.Warning, "Frontmatter has no closing delimiter.",
                    envelope.Opening, "frontmatter", "unterminated") : null;
            return Finish(diagnostic is null ? MdParseStatus.Complete : MdParseStatus.Partial,
                state, envelope, diagnostic);
        }
        catch (MdResourceLimit limit)
        {
            var input = limit.Reason == "input";
            return Finish(input ? MdParseStatus.Failed : MdParseStatus.Partial,
                MdFrontMatterScanState.Unknown, null, new MdDiagnostic(input ? "LMD002" : "LMD003",
                    input ? MdDiagnosticSeverity.Error : MdDiagnosticSeverity.Warning,
                    input ? "Front matter prefix exceeds maxInputUtf16." : "Front matter scanning stopped at a resource limit.",
                    null, input ? "input" : "resource", limit.Reason));
        }

        MdFrontMatterScanResult Finish(MdParseStatus status, MdFrontMatterScanState state,
            MdFrontMatterEnvelope? envelope, MdDiagnostic? diagnostic)
        {
            var result = new MdFrontMatterScanResult(raw.Length, options, status, state, envelope, diagnostic);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static MdFrontMatterEnvelope ScanCore(string raw, CancellationToken cancellationToken, MdParseContext? context,
        int? maxHeaderUtf16)
    {
        if (raw is null)
            throw new ArgumentNullException(nameof(raw));
        cancellationToken.ThrowIfCancellationRequested();
        var first = raw.Length != 0 && raw[0] == '\uFEFF' ? 1 : 0;
        if (maxHeaderUtf16.HasValue)
        {
            context?.Scan(first);
            if (!OpeningCandidate(first))
                return new MdFrontMatterEnvelope(null, null, null, new MdRawRange(first, raw.Length - first), 1);
        }
        var openingEnd = LineEnd(first);
        if (!Marker(first, openingEnd))
            return new MdFrontMatterEnvelope(null, null, null, new MdRawRange(first, raw.Length - first), 1);
        var opening = new MdRawRange(first, 3);
        var yamlStart = AfterBreak(openingEnd);
        var lineStart = yamlStart;
        var line = 2;
        while (lineStart < raw.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = LineEnd(lineStart);
            if (Marker(lineStart, end))
            {
                var bodyStart = AfterBreak(end);
                return new MdFrontMatterEnvelope(opening, new MdRawRange(yamlStart, lineStart - yamlStart),
                    new MdRawRange(lineStart, 3), new MdRawRange(bodyStart, raw.Length - bodyStart), 1);
            }
            lineStart = AfterBreak(end);
            line++;
        }
        return new MdFrontMatterEnvelope(opening, new MdRawRange(yamlStart, raw.Length - yamlStart), null, null, line);

        int LineEnd(int cursor)
        {
            while (cursor < raw.Length)
            {
                HeaderBoundary(cursor + 1);
                if (context is not null)
                    context.Scan();
                else if ((cursor & 127) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (raw[cursor] is '\r' or '\n')
                    break;
                cursor++;
            }
            return cursor;
        }
        bool Marker(int start, int end)
        {
            if (end - start != 3)
                return false;
            context?.Scan(3);
            return string.CompareOrdinal(raw, start, "---", 0, 3) == 0;
        }
        int AfterBreak(int end)
        {
            if (end >= raw.Length)
                return end;
            HeaderBoundary(end + 1);
            context?.Scan();
            if (raw[end] != '\r' || end + 1 >= raw.Length)
                return end + 1;
            var hasLf = raw[end + 1] == '\n';
            if (!maxHeaderUtf16.HasValue || hasLf)
                context?.Scan();
            var next = end + (hasLf ? 2 : 1);
            HeaderBoundary(next);
            return next;
        }

        void HeaderBoundary(int end)
        {
            if (maxHeaderUtf16.HasValue && end > maxHeaderUtf16.Value)
                throw new MdResourceLimit("input");
        }

        bool OpeningCandidate(int start)
        {
            for (var index = 0; index < 3; index++)
            {
                if (start + index >= raw.Length)
                    return false;
                context?.Scan();
                if (raw[start + index] != '-')
                    return false;
            }
            if (start + 3 == raw.Length)
                return true;
            context?.Scan();
            return raw[start + 3] is '\r' or '\n';
        }
    }
}
