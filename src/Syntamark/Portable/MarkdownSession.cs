using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Syntamark.Compilation;

internal enum MdSessionMode { Full, Delta }
internal enum MdSessionFallback
{
    None, InitialParse, PreviousNotComplete, OptionsChanged, UnsupportedSyntax,
    UnsupportedEdit, NoReusableBlocks, ResourceBoundary,
}

internal readonly record struct MdTextEdit(MdRawRange Span, string Replacement);

internal sealed record MdParagraphFragment(LithoParagraph Raw, int LineCount, long ParseScan, int ParseOutput)
{
    internal LithoParagraph? Resolved { get; init; }
    internal long ResolveScan { get; init; }
    internal int ResolveOutput { get; init; }
    internal bool Rebased { get; init; }

    internal MdParagraphFragment? Rebase(int offset, bool isCommonMark, CancellationToken cancellationToken)
    {
        if (offset == 0)
            return this with { Rebased = false };
        if (Raw.Inlines.Count != 1 || Raw.Inlines[0] is not LithoRawText raw || Resolved is null
            || Resolved.Inlines.Count != 1 || Resolved.Inlines[0] is not LithoText text || text.Projection is null
            || text.UnresolvedReference is not null || raw.Map.BodyStarts.Length != 1)
            return null;
        var shiftedRaw = raw with
        {
            Map = raw.Map with
            {
                BodyStarts = new[] { checked(raw.Map.BodyStarts[0] + offset) },
                BodyEnd = checked(raw.Map.BodyEnd + offset),
            },
        };
        var source = text.Projection;
        var segments = new MdSourceSegment[source.SourceSegments.Count];
        for (var i = 0; i < segments.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = source.SourceSegments[i];
            segments[i] = new MdSourceSegment(segment.DecodedSpan,
                segment.RawSpan.HasValue ? new MdRawRange(checked(segment.RawSpan.Value.Start + offset), segment.RawSpan.Value.Length) : null,
                segment.Kind);
        }
        var shiftedText = text with
        {
            // Legacy text spans are paragraph-local; their projections carry the absolute positions.
            Span = isCommonMark ? text.Span.Shift(offset) : text.Span,
            Projection = MdTextProjection.CreateOwned(source.Text, segments),
        };
        return this with
        {
            Raw = Raw with { Span = Raw.Span.Shift(offset), Inlines = new LithoInline[] { shiftedRaw } },
            Resolved = Resolved with { Span = Resolved.Span.Shift(offset), Inlines = new LithoInline[] { shiftedText }, RawContent = shiftedRaw },
            Rebased = true,
        };
    }
}

// Only isolated single-line plain paragraphs retain payloads; other blocks use the canonical parser.
internal sealed class MdParagraphReuse(IReadOnlyDictionary<int, MdParagraphFragment>? previous = null, bool enabled = true)
{
    internal Dictionary<int, MdParagraphFragment> Fragments { get; } = new();
    internal int ReusedBlocks { get; private set; }
    internal int RebasedBlocks { get; private set; }
    internal long ScanUnits { get; private set; }
    internal int OutputItems { get; private set; }

    internal void CaptureTotals(MdParseContext context)
    {
        ScanUnits = context.ScanUnits;
        OutputItems = context.OutputItems;
    }

    internal bool TryParse(int start, MdParseContext context, out LithoBlock? block, out int lineCount)
    {
        block = null;
        lineCount = 0;
        if (previous is null || !previous.TryGetValue(start, out var fragment))
            return false;
        context.Scan(fragment.ParseScan);
        context.Emit(fragment.ParseOutput);
        Fragments.Add(start, fragment);
        block = fragment.Raw;
        lineCount = fragment.LineCount;
        return true;
    }

    internal void CaptureRaw(LithoBlock block, int lineCount, MdParseContext context, long scanBefore, int outputBefore)
    {
        if (enabled && lineCount == 1 && block is LithoParagraph paragraph
            && paragraph.Inlines.Count == 1 && paragraph.Inlines[0] is LithoRawText text
            && text.Map.BodyStarts.Length == 1 && text.Map.BodyStarts[0] == paragraph.Span.Start
            && Plain(text.Text, context.CancellationToken))
            Fragments.Add(block.Span.Start, new MdParagraphFragment(paragraph, lineCount,
                context.ScanUnits - scanBefore, context.OutputItems - outputBefore));
    }

    private static bool Plain(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0 || text[0] == ' ')
            return false;
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var c = text[i];
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or ' '
                || c >= 128 && c != '\uFEFF' && !char.IsWhiteSpace(c)))
                return false;
        }
        return true;
    }

    internal bool TryResolve(LithoBlock block, MdParseContext context, out LithoBlock? resolved)
    {
        resolved = null;
        if (previous is null || !previous.TryGetValue(block.Span.Start, out var fragment)
            || !ReferenceEquals(fragment.Raw, block) || fragment.Resolved is null)
            return false;
        context.Scan(fragment.ResolveScan);
        context.Emit(fragment.ResolveOutput);
        resolved = fragment.Resolved;
        ReusedBlocks++;
        if (fragment.Rebased)
            RebasedBlocks++;
        return true;
    }

    internal void CaptureResolved(LithoBlock block, LithoBlock resolved, MdParseContext context,
        long scanBefore, int outputBefore)
    {
        if (enabled && resolved is LithoParagraph paragraph && Fragments.TryGetValue(block.Span.Start, out var fragment))
            Fragments[block.Span.Start] = fragment with
            {
                Resolved = paragraph,
                ResolveScan = context.ScanUnits - scanBefore,
                ResolveOutput = context.OutputItems - outputBefore,
            };
    }
}

internal sealed class MdParseSession
{
    private readonly MdOptions options;
    private readonly IReadOnlyDictionary<int, MdParagraphFragment>? fragments;

    private MdParseSession(MdSyntaxDocument document, MdOptions options, MdParagraphReuse reuse,
        bool supported, MdSessionMode mode, MdSessionFallback fallback)
    {
        Document = document;
        this.options = options;
        fragments = supported && document.Facts.Status == MdParseStatus.Complete ? reuse.Fragments : null;
        Mode = mode;
        Fallback = fallback;
        ReusedBlocks = mode == MdSessionMode.Delta ? reuse.ReusedBlocks : 0;
        RebasedBlocks = mode == MdSessionMode.Delta ? reuse.RebasedBlocks : 0;
        ParsedBlocks = supported ? reuse.Fragments.Count - ReusedBlocks : 0;
        ScanUnits = reuse.ScanUnits;
        OutputItems = reuse.OutputItems;
    }

    internal MdSyntaxDocument Document { get; }
    internal MdSessionMode Mode { get; }
    internal MdSessionFallback Fallback { get; }
    internal int ReusedBlocks { get; }
    internal int RebasedBlocks { get; }
    internal int ParsedBlocks { get; }
    internal long ScanUnits { get; }
    internal int OutputItems { get; }

    internal static MdParseSession Create(string raw, string scopeId, string sourceId, string? version,
        MdOptions options, CancellationToken cancellationToken)
        => Parse(raw, scopeId, sourceId, version, options, cancellationToken, MdSessionFallback.InitialParse);

    private static MdParseSession Parse(string raw, string scopeId, string sourceId, string? version,
        MdOptions options, CancellationToken cancellationToken, MdSessionFallback fallback,
        IReadOnlyDictionary<int, MdParagraphFragment>? previous = null)
    {
        if (raw is null)
            throw new ArgumentNullException(nameof(raw));
        var supported = raw.Length <= options.MaxInputUtf16;
        var reuse = new MdParagraphReuse(supported ? previous : null, supported);
        // Unsupported input takes the unchanged parser path; only counter totals are retained.
        var document = MdParser.ParseSession(raw, scopeId, sourceId, version, options, cancellationToken, reuse);
        if (previous is not null && document.Facts.Status != MdParseStatus.Complete)
        {
            var reason = MdSessionFallback.UnsupportedSyntax;
            foreach (var diagnostic in document.Facts.Diagnostics)
                if (diagnostic.Origin == "resource" || diagnostic.Reason == "input")
                {
                    reason = MdSessionFallback.ResourceBoundary;
                    break;
                }
            return Parse(raw, scopeId, sourceId, version, options, cancellationToken, reason);
        }
        var delta = previous is not null && reuse.ReusedBlocks > 0;
        return new MdParseSession(document, options, reuse, supported,
            delta ? MdSessionMode.Delta : MdSessionMode.Full,
            delta ? MdSessionFallback.None : previous is not null ? MdSessionFallback.NoReusableBlocks : fallback);
    }

    internal MdParseSession ApplyEdits(string? expectedTextHash, IReadOnlyList<MdTextEdit> edits, string? version,
        MdOptions? newOptions, CancellationToken cancellationToken)
    {
        if (edits is null)
            throw new ArgumentNullException(nameof(edits));
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(expectedTextHash, Document.Facts.TextHash, StringComparison.Ordinal))
            throw new ArgumentException("The expected text hash does not identify this session.", nameof(expectedTextHash));
        var raw = Document.Facts.RawText;
        var bodyStart = Document.Facts.BodySpan?.Start ?? 0;
        var owned = new MdTextEdit[edits.Count];
        var nextLength = raw.Length;
        var lastEnd = 0;
        var lastStart = -1;
        var withinLines = true;
        for (var i = 0; i < owned.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edit = edits[i];
            if (edit.Replacement is null || edit.Span.Start < lastEnd || edit.Span.Start <= lastStart || edit.Span.End > raw.Length)
                throw new ArgumentException("Edits must have non-null replacements and sorted, disjoint source ranges.", nameof(edits));
            owned[i] = edit;
            nextLength = checked(nextLength + edit.Replacement.Length - edit.Span.Length);
            withinLines &= edit.Span.Start >= bodyStart
                && !HasLineBreak(raw, edit.Span.Start, edit.Span.Length, cancellationToken)
                && !HasLineBreak(edit.Replacement, 0, edit.Replacement.Length, cancellationToken);
            lastStart = edit.Span.Start;
            lastEnd = edit.Span.End;
        }
        var builder = new StringBuilder(nextLength);
        var cursor = 0;
        foreach (var edit in owned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(raw, cursor, edit.Span.Start - cursor);
            builder.Append(edit.Replacement);
            cursor = edit.Span.End;
        }
        builder.Append(raw, cursor, raw.Length - cursor);
        var updated = builder.ToString();
        var effective = newOptions ?? options;
        var fallback = Document.Facts.Status != MdParseStatus.Complete ? MdSessionFallback.PreviousNotComplete
            : effective.Hash != options.Hash ? MdSessionFallback.OptionsChanged
            : !withinLines ? MdSessionFallback.UnsupportedEdit
            : fragments is null ? MdSessionFallback.UnsupportedSyntax
            : MdSessionFallback.None;
        if (fallback != MdSessionFallback.None)
            return Parse(updated, Document.Facts.ScopeId, Document.Facts.SourceId, version, effective, cancellationToken, fallback);
        var starts = new int[fragments!.Count];
        var at = 0;
        foreach (var start in fragments.Keys)
            starts[at++] = start;
        Array.Sort(starts);
        var changed = new HashSet<int>();
        var shifts = new int[owned.Length + 1];
        for (var i = 0; i < owned.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edit = owned[i];
            var editStart = edit.Span.Start - bodyStart;
            var found = Array.BinarySearch(starts, editStart);
            var paragraph = found >= 0 ? found : ~found - 1;
            if (paragraph < 0 || edit.Span.End - bodyStart > fragments[starts[paragraph]].Raw.Span.End)
                return Parse(updated, Document.Facts.ScopeId, Document.Facts.SourceId, version, effective,
                    cancellationToken, MdSessionFallback.UnsupportedEdit);
            if (edit.Span.Length != 0 || edit.Replacement.Length != 0)
                changed.Add(starts[paragraph]);
            shifts[i + 1] = checked(shifts[i] + edit.Replacement.Length - edit.Span.Length);
        }
        var reusable = new Dictionary<int, MdParagraphFragment>();
        foreach (var entry in fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (changed.Contains(entry.Key))
                continue;
            var span = entry.Value.Raw.Span;
            if (!Isolated(raw, bodyStart + span.Start, bodyStart + span.End, cancellationToken))
                continue;
            var low = 0;
            var high = owned.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (owned[middle].Span.End <= bodyStart + span.Start)
                    low = middle + 1;
                else
                    high = middle;
            }
            var rebased = entry.Value.Rebase(shifts[low], effective.IsCommonMark, cancellationToken);
            if (rebased is null)
                return Parse(updated, Document.Facts.ScopeId, Document.Facts.SourceId, version, effective,
                    cancellationToken, MdSessionFallback.UnsupportedSyntax);
            if (!Isolated(updated, bodyStart + rebased.Raw.Span.Start, bodyStart + rebased.Raw.Span.End,
                cancellationToken))
                continue;
            reusable.Add(rebased.Raw.Span.Start, rebased);
        }
        return Parse(updated, Document.Facts.ScopeId, Document.Facts.SourceId, version, effective, cancellationToken,
            MdSessionFallback.NoReusableBlocks, reusable.Count == 0 ? null : reusable);
    }

    private static bool HasLineBreak(string value, int start, int length, CancellationToken cancellationToken)
    {
        for (var i = start; i < start + length; i++)
        {
            if ((i & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (value[i] is '\r' or '\n')
                return true;
        }
        return false;
    }

    private static bool Isolated(string raw, int start, int end, CancellationToken cancellationToken)
    {
        // Blank neighboring lines keep paragraph lookahead and canonical resource charges unchanged.
        if (start > 0)
        {
            var previous = start - 1;
            if (raw[previous] is not ('\r' or '\n'))
                return false;
            if (raw[previous] == '\n' && previous > 0 && raw[previous - 1] == '\r')
                previous--;
            previous--;
            while (previous >= 0 && raw[previous] is ' ' or '\t')
            {
                if ((previous & 127) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                previous--;
            }
            if (previous < 0 || raw[previous] is not ('\r' or '\n'))
                return false;
        }
        if (end == raw.Length)
            return true;
        if (raw[end] is not ('\r' or '\n'))
            return false;
        if (raw[end++] == '\r' && end < raw.Length && raw[end] == '\n')
            end++;
        while (end < raw.Length && raw[end] is ' ' or '\t')
        {
            if ((end & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            end++;
        }
        return end == raw.Length || raw[end] is '\r' or '\n';
    }
}
