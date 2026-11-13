using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Syntamark.Compilation;

internal enum MdYamlScalarStyle { Plain, SingleQuoted, DoubleQuoted, Literal, Folded }
internal enum MdYamlScalarKind { String, Null, Boolean, Integer, Float, Unknown }
internal enum MdYamlDiagnosticKind { Syntax, Unsupported, ResourceLimit, Canceled, InvalidEncoding }

internal sealed class MdYamlOptions
{
    internal MdYamlOptions(int maxInputBytes = 4194304, int maxInputUtf16 = 1048576,
        int maxNestingDepth = 128, int maxNodes = 131072, int maxScalarUtf16 = 1048576,
        int maxScanUnits = 16777216)
    {
        if (maxInputBytes < 1 || maxInputUtf16 < 1 || maxNestingDepth < 1 || maxNestingDepth > 256
            || maxNodes < 1 || maxScalarUtf16 < 1 || maxScanUnits < 1)
            throw new ArgumentOutOfRangeException(nameof(maxInputBytes), "Limits must be positive; nesting must not exceed 256.");
        MaxInputBytes = maxInputBytes;
        MaxInputUtf16 = maxInputUtf16;
        MaxNestingDepth = maxNestingDepth;
        MaxNodes = maxNodes;
        MaxScalarUtf16 = maxScalarUtf16;
        MaxScanUnits = maxScanUnits;
    }
    internal int MaxInputBytes { get; }
    internal int MaxInputUtf16 { get; }
    internal int MaxNestingDepth { get; }
    internal int MaxNodes { get; }
    internal int MaxScalarUtf16 { get; }
    internal int MaxScanUnits { get; }
    internal string Hash => MdHash.Compute(string.Join("/", "native-yaml/1",
        MaxInputBytes.ToString(CultureInfo.InvariantCulture), MaxInputUtf16.ToString(CultureInfo.InvariantCulture),
        MaxNestingDepth.ToString(CultureInfo.InvariantCulture), MaxNodes.ToString(CultureInfo.InvariantCulture),
        MaxScalarUtf16.ToString(CultureInfo.InvariantCulture), MaxScanUnits.ToString(CultureInfo.InvariantCulture)));
}

internal sealed class MdYamlDiagnostic(MdYamlDiagnosticKind kind, string reason, string message, MdRawRange rawSpan)
{
    internal MdYamlDiagnosticKind Kind { get; } = kind;
    internal string Reason { get; } = reason;
    internal string Message { get; } = message;
    internal MdRawRange RawSpan { get; } = rawSpan;
}

internal sealed class MdYamlEntry(MdYamlSyntaxNode key, MdYamlSyntaxNode value)
{
    internal MdYamlSyntaxNode Key { get; } = key;
    internal MdYamlSyntaxNode Value { get; } = value;
}

internal sealed class MdYamlSyntaxNode
{
    internal MdYamlSyntaxNode(MdYamlKind kind, MdRawRange rawSpan, MdYamlScalarStyle scalarStyle,
        MdYamlScalarKind scalarKind, MdTextProjection projection, IReadOnlyList<MdYamlSyntaxNode> children,
        IReadOnlyList<MdYamlEntry> entries, bool isComplete)
    {
        Kind = kind;
        RawSpan = rawSpan;
        ScalarStyle = scalarStyle;
        ScalarKind = scalarKind;
        Projection = projection;
        Children = MdFreeze.Copy(children);
        Entries = MdFreeze.Copy(entries);
        IsComplete = isComplete;
    }
    internal MdYamlKind Kind { get; }
    internal MdRawRange RawSpan { get; }
    internal MdYamlScalarStyle ScalarStyle { get; }
    internal MdYamlScalarKind ScalarKind { get; }
    internal string? Value => Projection.Text;
    internal MdTextProjection Projection { get; }
    internal IReadOnlyList<MdYamlSyntaxNode> Children { get; }
    internal IReadOnlyList<MdYamlEntry> Entries { get; }
    internal bool IsComplete { get; }
}

internal sealed class MdYamlDocument
{
    private readonly byte[]? originalUtf8;
    internal MdYamlDocument(string rawText, byte[]? originalUtf8, MdYamlOptions options,
        MdYamlSyntaxNode? root, IReadOnlyList<MdYamlDiagnostic> diagnostics)
    {
        RawText = rawText;
        this.originalUtf8 = originalUtf8;
        OptionsHash = options.Hash;
        Root = root;
        Diagnostics = MdFreeze.Copy(diagnostics);
        Status = diagnostics.Count == 0 ? MdParseStatus.Complete
            : root is null ? MdParseStatus.Failed : MdParseStatus.Partial;
    }
    internal string RawText { get; }
    internal string ProfileId => "native-yaml/1";
    internal string ParserVersion => MdParserVersion.Value;
    internal string OptionsHash { get; }
    internal MdParseStatus Status { get; }
    internal MdYamlSyntaxNode? Root { get; }
    internal IReadOnlyList<MdYamlDiagnostic> Diagnostics { get; }
    internal bool HasOriginalUtf8 => originalUtf8 is not null;
    internal byte[]? GetOriginalUtf8() => originalUtf8 is null ? null : (byte[])originalUtf8.Clone();
}

internal static class MdYamlParser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly MdYamlOptions Defaults = new();

    internal static MdYamlDocument Parse(string rawText, MdYamlOptions? options = null,
        CancellationToken cancellationToken = default) => Parse(rawText, options ?? Defaults, cancellationToken, null, null);

    internal static MdYamlDocument ParseUtf8(byte[] rawUtf8, MdYamlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (rawUtf8 is null)
            throw new ArgumentNullException(nameof(rawUtf8));
        options ??= Defaults;
        if (cancellationToken.IsCancellationRequested)
            return Rejected("", null, options, MdYamlDiagnosticKind.Canceled, "canceled", "YAML parsing was canceled.");
        if (rawUtf8.Length > options.MaxInputBytes)
            return Rejected("", null, options, MdYamlDiagnosticKind.ResourceLimit, "bytes", "YAML input byte limit exceeded.");
        var snapshot = (byte[])rawUtf8.Clone();
        try
        {
            return Parse(Utf8.GetString(snapshot), options, cancellationToken, null, snapshot);
        }
        catch (DecoderFallbackException)
        {
            return Rejected("", snapshot, options, MdYamlDiagnosticKind.InvalidEncoding, "utf8", "YAML input is not valid UTF-8.");
        }
    }

    internal static MdYamlDocument ParseFrontMatter(string rawText, MdParseContext context)
    {
        var options = new MdYamlOptions(int.MaxValue, context.Options.MaxInputUtf16,
            Math.Min(context.Options.MaxNestingDepth, 256), context.Options.MaxOutputItems,
            context.Options.MaxInputUtf16, context.Options.MaxScanUnits);
        return Parse(rawText, options, context.CancellationToken, context, null);
    }

    private static MdYamlDocument Parse(string rawText, MdYamlOptions options, CancellationToken token,
        MdParseContext? context, byte[]? bytes)
    {
        if (rawText is null)
            throw new ArgumentNullException(nameof(rawText));
        if (rawText.Length > options.MaxInputUtf16)
            return Rejected(rawText, bytes, options, MdYamlDiagnosticKind.ResourceLimit, "characters", "YAML input character limit exceeded.");
        try
        {
            var invalid = MdHash.InvalidUnicode(rawText, token);
            if (invalid >= 0)
                return new MdYamlDocument(rawText, bytes, options, null,
                    new[] { new MdYamlDiagnostic(MdYamlDiagnosticKind.InvalidEncoding, "utf16", "YAML input contains an unpaired surrogate.", new MdRawRange(invalid, 1)) });
            if (Utf8.GetByteCount(rawText) > options.MaxInputBytes)
                return Rejected(rawText, bytes, options, MdYamlDiagnosticKind.ResourceLimit, "bytes", "YAML input byte limit exceeded.");
            // A frontmatter consumer already owns the complete Markdown snapshot.
            bytes ??= context is null ? Utf8.GetBytes(rawText) : null;
            return new Reader(rawText, bytes, options, token, context).Parse();
        }
        catch (OperationCanceledException) when (context is null)
        {
            return Rejected(rawText, bytes, options, MdYamlDiagnosticKind.Canceled, "canceled", "YAML parsing was canceled.");
        }
    }

    private static MdYamlDocument Rejected(string raw, byte[]? bytes, MdYamlOptions options,
        MdYamlDiagnosticKind kind, string reason, string message) => new(raw, bytes, options, null,
            new[] { new MdYamlDiagnostic(kind, reason, message, new MdRawRange(0, 0)) });

    private sealed class Stop(string reason, string message, int at) : Exception(message)
    {
        internal string Reason { get; } = reason;
        internal int At { get; } = at;
        internal MdYamlSyntaxNode? Partial { get; set; }
    }

    private readonly struct Line(int start, int contentEnd, int end, int content)
    {
        internal int Start { get; } = start;
        internal int ContentEnd { get; } = contentEnd;
        internal int End { get; } = end;
        internal int Content { get; } = content;
        internal int Indent => Content - Start;
    }

    private sealed class Reader(string raw, byte[]? bytes, MdYamlOptions options, CancellationToken token, MdParseContext? context)
    {
        private readonly List<Line> lines = new();
        private readonly List<MdYamlDiagnostic> diagnostics = new();
        private long units;
        private int nodes;
        private int cursor;

        internal MdYamlDocument Parse()
        {
            MdYamlSyntaxNode? root = null;
            try
            {
                BuildLines();
                var line = 0;
                SkipTrivia(ref line);
                while (line < lines.Count && raw[lines[line].Content] == '%')
                {
                    Diagnostic(MdYamlDiagnosticKind.Unsupported, "directive", "YAML directives are not supported.", lines[line].Content, lines[line].ContentEnd);
                    line++;
                    SkipTrivia(ref line);
                }
                var explicitRoot = false;
                if (line < lines.Count && Marker(line, "---"))
                {
                    var afterMarker = SkipSpaces(lines[line].Content + 3, lines[line].ContentEnd);
                    if (afterMarker < lines[line].ContentEnd && raw[afterMarker] != '#')
                    {
                        root = Block(ref line, 0, 1, afterMarker);
                        explicitRoot = true;
                    }
                    else
                    {
                        line++;
                        SkipTrivia(ref line);
                    }
                }
                if (!explicitRoot && line < lines.Count && !Marker(line, "..."))
                    root = Block(ref line, lines[line].Indent, 1);
                SkipTrivia(ref line);
                if (line < lines.Count && Marker(line, "..."))
                {
                    line++;
                    SkipTrivia(ref line);
                }
                if (line < lines.Count)
                    Diagnostic(MdYamlDiagnosticKind.Syntax, Marker(line, "---") ? "multipleDocument" : "syntax",
                        "YAML must contain exactly one complete document.", lines[line].Content, raw.Length);
            }
            catch (Stop stop)
            {
                root = stop.Partial ?? root;
                diagnostics.Add(new MdYamlDiagnostic(MdYamlDiagnosticKind.ResourceLimit, stop.Reason, stop.Message,
                    new MdRawRange(Math.Min(stop.At, raw.Length), 0)));
            }
            catch (OperationCanceledException) when (context is null)
            {
                root = null;
                diagnostics.Clear();
                diagnostics.Add(new MdYamlDiagnostic(MdYamlDiagnosticKind.Canceled, "canceled", "YAML parsing was canceled.",
                    new MdRawRange(Math.Min(cursor, raw.Length), 0)));
            }
            return new MdYamlDocument(raw, bytes, options, root, diagnostics);
        }

        private void Scan(int count = 1)
        {
            token.ThrowIfCancellationRequested();
            context?.Scan(count);
            if (count > options.MaxScanUnits - units)
                throw new Stop("scan", "YAML processing budget exceeded.", cursor);
            units += count;
        }

        private void CountNode(int depth, int at)
        {
            cursor = at;
            token.ThrowIfCancellationRequested();
            context?.Depth(depth);
            context?.Emit();
            if (depth > options.MaxNestingDepth)
                throw new Stop("depth", "YAML nesting depth limit exceeded.", at);
            if (nodes++ >= options.MaxNodes)
                throw new Stop("nodes", "YAML node limit exceeded.", at);
        }

        private void BuildLines()
        {
            var at = 0;
            while (at < raw.Length)
            {
                var start = at;
                var content = at;
                if (start == 0 && raw[at] == '\uFEFF')
                    start = content = ++at;
                while (at < raw.Length && raw[at] is not ('\r' or '\n'))
                {
                    cursor = at;
                    Scan();
                    var character = raw[at];
                    if (character < 0x20 && character != '\t' || character is >= '\u007F' and <= '\u009F' && character != '\u0085'
                        || character is '\uFFFE' or '\uFFFF')
                        Diagnostic(MdYamlDiagnosticKind.Syntax, "character", "YAML source contains a non-printable character.", at, at + 1);
                    at++;
                }
                var end = at;
                cursor = at;
                Scan();
                if (at < raw.Length && raw[at++] == '\r' && at < raw.Length && raw[at] == '\n')
                    at++;
                while (content < end && raw[content] == ' ')
                    content++;
                lines.Add(new Line(start, end, at, content));
            }
        }

        private bool Blank(int line) => lines[line].Content == lines[line].ContentEnd || raw[lines[line].Content] == '#';
        private void SkipTrivia(ref int line)
        {
            while (line < lines.Count && Blank(line))
            {
                Scan();
                line++;
            }
        }

        private bool Marker(int line, string marker)
        {
            var value = lines[line];
            if (value.Indent != 0 || value.ContentEnd - value.Content < marker.Length
                || string.CompareOrdinal(raw, value.Content, marker, 0, marker.Length) != 0)
                return false;
            var rest = value.Content + marker.Length;
            return rest == value.ContentEnd || raw[rest] is ' ' or '\t' or '#';
        }

        private bool SequenceAt(int at, int end) => at < end && raw[at] == '-'
            && (at + 1 == end || raw[at + 1] is ' ' or '\t');

        private MdYamlSyntaxNode Block(ref int line, int indent, int depth, int? firstContent = null)
        {
            SkipTrivia(ref line);
            var start = firstContent ?? lines[line].Content;
            if (raw[start] == '\t')
                Diagnostic(MdYamlDiagnosticKind.Syntax, "indentation", "Tabs cannot be used for YAML indentation.", start, start + 1);
            if (SequenceAt(start, lines[line].ContentEnd))
                return Sequence(ref line, indent, depth, firstContent);
            if (Colon(start, lines[line].ContentEnd) >= 0)
                return Mapping(ref line, indent, depth, firstContent);
            return BlockValue(ref line, start, indent - 1, depth);
        }

        private MdYamlSyntaxNode Mapping(ref int line, int indent, int depth, int? firstContent = null)
        {
            var start = firstContent ?? lines[line].Content;
            CountNode(depth, start);
            var entries = new List<MdYamlEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var end = start;
            try
            {
                var first = true;
                while (line < lines.Count)
                {
                    SkipTrivia(ref line);
                    if (line == lines.Count || Marker(line, "---") || Marker(line, "..."))
                        break;
                    var current = lines[line];
                    var at = first && firstContent.HasValue ? firstContent.Value : current.Content;
                    if ((!first || !firstContent.HasValue) && current.Indent != indent)
                        break;
                    first = false;
                    var colon = Colon(at, current.ContentEnd);
                    if (colon < 0)
                        break;
                    var keyAt = at;
                    var key = Inline(ref keyAt, TrimEnd(at, colon), depth + 1, false, true);
                    CheckKey(key, keys);
                    at = SkipSpaces(colon + 1, current.ContentEnd);
                    MdYamlSyntaxNode value;
                    if (at == current.ContentEnd || raw[at] == '#')
                    {
                        line++;
                        SkipTrivia(ref line);
                        if (line < lines.Count && !Marker(line, "---") && !Marker(line, "...")
                            && (lines[line].Indent > indent || (lines[line].Indent == indent && SequenceAt(lines[line].Content, lines[line].ContentEnd))))
                            value = Block(ref line, lines[line].Indent, depth + 1);
                        else
                            value = Empty(colon + 1, depth + 1);
                    }
                    else
                        value = BlockValue(ref line, at, indent, depth + 1);
                    entries.Add(new MdYamlEntry(key, value));
                    end = line == 0 ? current.End : lines[Math.Min(line, lines.Count) - 1].End;
                }
            }
            catch (Stop stop)
            {
                stop.Partial = Collection(MdYamlKind.Mapping, start, Math.Max(end, cursor), null, entries, false);
                throw;
            }
            return Collection(MdYamlKind.Mapping, start, end, null, entries, true);
        }

        private MdYamlSyntaxNode Sequence(ref int line, int indent, int depth, int? firstContent = null)
        {
            var start = firstContent ?? lines[line].Content;
            CountNode(depth, start);
            var children = new List<MdYamlSyntaxNode>();
            var end = start;
            try
            {
                var first = true;
                while (line < lines.Count)
                {
                    SkipTrivia(ref line);
                    if (line == lines.Count || Marker(line, "---") || Marker(line, "..."))
                        break;
                    var current = lines[line];
                    var at = first && firstContent.HasValue ? firstContent.Value : current.Content;
                    if ((!first || !firstContent.HasValue) && current.Indent != indent || !SequenceAt(at, current.ContentEnd))
                        break;
                    first = false;
                    at = SkipSpaces(at + 1, current.ContentEnd);
                    MdYamlSyntaxNode value;
                    if (at == current.ContentEnd || raw[at] == '#')
                    {
                        line++;
                        SkipTrivia(ref line);
                        value = line < lines.Count && !Marker(line, "---") && !Marker(line, "...") && lines[line].Indent > indent
                            ? Block(ref line, lines[line].Indent, depth + 1) : Empty(at, depth + 1);
                    }
                    else if (SequenceAt(at, current.ContentEnd) || Colon(at, current.ContentEnd) >= 0)
                        value = Block(ref line, at - current.Start, depth + 1, at);
                    else
                        value = BlockValue(ref line, at, indent, depth + 1);
                    children.Add(value);
                    end = lines[Math.Min(line, lines.Count) - 1].End;
                }
            }
            catch (Stop stop)
            {
                stop.Partial = Collection(MdYamlKind.Sequence, start, Math.Max(end, cursor), children, null, false);
                throw;
            }
            return Collection(MdYamlKind.Sequence, start, end, children, null, true);
        }

        private MdYamlSyntaxNode BlockValue(ref int line, int at, int parentIndent, int depth)
        {
            var current = lines[line];
            if (raw[at] is '|' or '>')
                return BlockScalar(ref line, at, parentIndent, depth);
            var start = at;
            var node = Inline(ref at, current.ContentEnd, depth, false);
            var endingLine = FindLine(Math.Max(start, at - 1));
            if (endingLine < lines.Count)
            {
                var extra = SkipSpaces(at, lines[endingLine].ContentEnd);
                if (extra < lines[endingLine].ContentEnd && raw[extra] != '#')
                    Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Unexpected content after a YAML value.", extra, lines[endingLine].ContentEnd);
            }
            line = endingLine + 1;
            if (node.Kind != MdYamlKind.Scalar || node.ScalarStyle != MdYamlScalarStyle.Plain || !node.IsComplete)
                return node;
            var continued = line;
            var value = new StringBuilder(node.Value);
            var end = node.RawSpan.End;
            var blanks = 0;
            while (continued < lines.Count)
            {
                Scan();
                var next = lines[continued];
                if (Blank(continued))
                {
                    blanks++;
                    continued++;
                    continue;
                }
                if (next.Indent <= parentIndent || Marker(continued, "---") || Marker(continued, "..."))
                    break;
                if (Colon(next.Content, next.ContentEnd) >= 0 || SequenceAt(next.Content, next.ContentEnd))
                {
                    Diagnostic(MdYamlDiagnosticKind.Syntax, "indentation", "A plain scalar cannot contain an indented collection.", next.Content, next.ContentEnd);
                    break;
                }
                var part = Plain(next.Content, next.ContentEnd, false, false, out var partEnd);
                value.Append(blanks == 0 ? " " : new string('\n', blanks));
                value.Append(part);
                CheckScalar(value.Length, start);
                end = partEnd;
                line = ++continued;
                blanks = 0;
            }
            if (end == node.RawSpan.End)
                return node;
            return Scalar(start, end, value.ToString(), MdYamlScalarStyle.Plain, false, node.IsComplete);
        }

        private MdYamlSyntaxNode BlockScalar(ref int line, int at, int parentIndent, int depth)
        {
            CountNode(depth, at);
            var start = at;
            var folded = raw[at++] == '>';
            var chomping = '\0';
            var explicitIndent = 0;
            var headerEnd = lines[line].ContentEnd;
            while (at < headerEnd && raw[at] is not (' ' or '\t' or '#'))
            {
                Scan();
                var c = raw[at++];
                if (c is '+' or '-' && chomping == '\0')
                    chomping = c;
                else if (c is >= '1' and <= '9' && explicitIndent == 0)
                    explicitIndent = c - '0';
                else
                    Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Invalid block scalar header.", at - 1, at);
            }
            at = SkipSpaces(at, headerEnd);
            if (at < headerEnd && raw[at] != '#')
                Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Unexpected block scalar header content.", at, headerEnd);
            var firstLine = ++line;
            var contentIndent = explicitIndent == 0 ? -1 : Math.Max(0, parentIndent) + explicitIndent;
            var probe = line;
            while (probe < lines.Count && lines[probe].Content == lines[probe].ContentEnd)
                probe++;
            if (contentIndent < 0)
                contentIndent = probe < lines.Count && lines[probe].Indent > parentIndent ? lines[probe].Indent : parentIndent + 1;
            var pieces = new List<(string Text, bool MoreIndented, bool HasBreak)>();
            var contentUnits = 0;
            var end = lines[firstLine - 1].End;
            while (line < lines.Count)
            {
                Scan();
                var current = lines[line];
                var blank = current.Content == current.ContentEnd;
                if (!blank && (current.Indent < contentIndent || Marker(line, "---") || Marker(line, "...")))
                    break;
                var from = Math.Min(current.Start + contentIndent, current.ContentEnd);
                CheckScalar(checked(contentUnits + current.ContentEnd - from), start);
                if (pieces.Count >= options.MaxNodes)
                    throw new Stop("nodes", "YAML block scalar line limit exceeded.", current.Start);
                var text = raw.Substring(from, current.ContentEnd - from);
                contentUnits += text.Length;
                pieces.Add((text, !blank && current.Indent > contentIndent, current.End > current.ContentEnd));
                end = current.End;
                line++;
            }
            var value = new StringBuilder();
            for (var i = 0; i < pieces.Count; i++)
            {
                Scan(pieces[i].Text.Length);
                value.Append(pieces[i].Text);
                if (pieces[i].HasBreak)
                {
                    var fold = folded && i + 1 < pieces.Count && pieces[i].Text.Length != 0
                        && pieces[i + 1].Text.Length != 0 && !pieces[i].MoreIndented && !pieces[i + 1].MoreIndented;
                    // A blank line between ordinary folded lines represents one retained line break.
                    var beforeBlank = false;
                    if (folded && pieces[i].Text.Length != 0 && i + 1 < pieces.Count
                        && pieces[i + 1].Text.Length == 0 && !pieces[i].MoreIndented)
                    {
                        var nextText = i + 2;
                        while (nextText < pieces.Count && pieces[nextText].Text.Length == 0)
                            nextText++;
                        beforeBlank = nextText < pieces.Count && !pieces[nextText].MoreIndented;
                    }
                    if (fold)
                        value.Append(' ');
                    else if (!beforeBlank)
                        value.Append('\n');
                }
                CheckScalar(value.Length, start);
            }
            if (chomping != '+')
            {
                while (value.Length > 0 && value[value.Length - 1] == '\n')
                    value.Length--;
                if (chomping != '-' && pieces.Count != 0 && pieces[pieces.Count - 1].HasBreak && value.Length != 0)
                    value.Append('\n');
            }
            return Scalar(start, end, value.ToString(), folded ? MdYamlScalarStyle.Folded : MdYamlScalarStyle.Literal, false, true);
        }

        private MdYamlSyntaxNode Inline(ref int at, int end, int depth, bool flow, bool key = false)
        {
            at = SkipSpaces(at, end);
            if (at >= end || raw[at] == '#')
                return Empty(at, depth);
            CountNode(depth, at);
            var start = at;
            if (raw[at] is '\'' or '"')
                return Quoted(ref at);
            if (raw[at] is '[' or '{')
                return Flow(ref at, depth);
            if (raw[at] is '&' or '*' or '!' or '%' || raw[at] == '?' && (at + 1 == end || char.IsWhiteSpace(raw[at + 1])))
            {
                var reason = raw[at] is '&' or '*' ? "alias" : raw[at] == '!' ? "tag" : raw[at] == '%' ? "directive" : "complexKey";
                Diagnostic(MdYamlDiagnosticKind.Unsupported, reason, "This YAML construct is not supported by native-yaml/1.", start, end);
                at = end;
                return Scalar(start, end, raw.Substring(start, end - start), MdYamlScalarStyle.Plain, false, false, MdYamlScalarKind.Unknown);
            }
            var value = Plain(at, end, flow, key, out at);
            if (at == start)
            {
                Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Expected a YAML scalar or collection.", at, Math.Min(at + 1, raw.Length));
                if (at < end)
                    at++;
                return Scalar(start, at, "", MdYamlScalarStyle.Plain, false, false, MdYamlScalarKind.Unknown);
            }
            if (raw[start] is '@' or '`' or ']' or '}' or ',' || raw[start] is '|' or '>')
                Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Invalid plain scalar indicator.", start, at);
            return Scalar(start, at, value, MdYamlScalarStyle.Plain, true, true);
        }

        private MdYamlSyntaxNode Quoted(ref int at)
        {
            var start = at;
            var quote = raw[at++];
            var value = new StringBuilder();
            var complete = false;
            var linear = true;
            while (at < raw.Length)
            {
                cursor = at;
                Scan();
                var c = raw[at++];
                if (c == quote)
                {
                    if (quote == '\'' && at < raw.Length && raw[at] == '\'')
                    {
                        at++;
                        value.Append('\'');
                        linear = false;
                    }
                    else
                    {
                        complete = true;
                        break;
                    }
                }
                else if (c == '\\' && quote == '"')
                {
                    linear = false;
                    if (at == raw.Length)
                        break;
                    c = raw[at++];
                    var escape = "0abtnvfre \"/\\N_LP";
                    var decoded = "\0\a\b\t\n\v\f\r\u001B \"/\\\u0085\u00A0\u2028\u2029";
                    var index = escape.IndexOf(c);
                    if (index >= 0)
                        value.Append(decoded[index]);
                    else if (c is 'x' or 'u' or 'U')
                    {
                        var digits = c == 'x' ? 2 : c == 'u' ? 4 : 8;
                        long codepoint = 0;
                        var valid = at + digits <= raw.Length;
                        for (var i = 0; valid && i < digits; i++)
                        {
                            var hex = Hex(raw[at + i]);
                            valid = hex >= 0;
                            codepoint = (codepoint << 4) + Math.Max(0, hex);
                        }
                        if (!valid || codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF)
                            Diagnostic(MdYamlDiagnosticKind.Syntax, "escape", "Invalid Unicode escape in YAML scalar.", at - 2, Math.Min(at + digits, raw.Length));
                        else
                        {
                            value.Append(char.ConvertFromUtf32((int)codepoint));
                            at += digits;
                        }
                    }
                    else if (c is '\r' or '\n')
                    {
                        if (c == '\r' && at < raw.Length && raw[at] == '\n')
                            at++;
                        at = SkipSpaces(at, raw.Length);
                    }
                    else
                        Diagnostic(MdYamlDiagnosticKind.Syntax, "escape", "Unknown YAML double-quoted escape.", at - 2, at);
                }
                else if (c is '\r' or '\n')
                {
                    linear = false;
                    if (c == '\r' && at < raw.Length && raw[at] == '\n')
                        at++;
                    while (value.Length > 0 && value[value.Length - 1] is ' ' or '\t')
                        value.Length--;
                    at = SkipSpaces(at, raw.Length);
                    var breaks = 0;
                    while (at < raw.Length && raw[at] is '\r' or '\n')
                    {
                        if (raw[at++] == '\r' && at < raw.Length && raw[at] == '\n')
                            at++;
                        breaks++;
                        at = SkipSpaces(at, raw.Length);
                    }
                    value.Append(breaks == 0 ? " " : new string('\n', breaks));
                }
                else
                    value.Append(c);
                CheckScalar(value.Length, start);
            }
            if (!complete)
                Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Unterminated YAML quoted scalar.", start, at);
            var text = value.ToString();
            var span = new MdRawRange(start, at - start);
            MdTextProjection projection;
            if (text.Length == 0)
                projection = new MdTextProjection(text, MdFreeze.Empty<MdSourceSegment>());
            else if (linear && complete)
                projection = new MdTextProjection(text, new[] { new MdSourceSegment(new MdRawRange(0, text.Length), new MdRawRange(start + 1, text.Length), MdSegmentKind.Linear) });
            else
                projection = new MdTextProjection(text, new[] { new MdSourceSegment(new MdRawRange(0, text.Length), span, MdSegmentKind.Atomic) });
            return new MdYamlSyntaxNode(MdYamlKind.Scalar, span, quote == '\'' ? MdYamlScalarStyle.SingleQuoted : MdYamlScalarStyle.DoubleQuoted,
                MdYamlScalarKind.String, projection, MdFreeze.Empty<MdYamlSyntaxNode>(), MdFreeze.Empty<MdYamlEntry>(), complete);
        }

        private MdYamlSyntaxNode Flow(ref int at, int depth)
        {
            var start = at;
            var mapping = raw[at++] == '{';
            var close = mapping ? '}' : ']';
            var children = new List<MdYamlSyntaxNode>();
            var entries = new List<MdYamlEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var complete = false;
            try
            {
                while (true)
                {
                    FlowSpace(ref at);
                    if (at == raw.Length)
                        break;
                    if (raw[at] == close)
                    {
                        at++;
                        complete = true;
                        break;
                    }
                    var before = at;
                    var key = Inline(ref at, raw.Length, depth + 1, true, true);
                    FlowSpace(ref at);
                    if (mapping || at < raw.Length && raw[at] == ':')
                    {
                        CheckKey(key, mapping ? keys : new HashSet<string>(StringComparer.Ordinal));
                        var valueDepth = depth + 1;
                        if (!mapping)
                        {
                            valueDepth++;
                            context?.Depth(valueDepth);
                            if (valueDepth > options.MaxNestingDepth)
                                throw new Stop("depth", "YAML nesting depth limit exceeded.", key.RawSpan.Start);
                        }
                        MdYamlSyntaxNode value;
                        if (at < raw.Length && raw[at] == ':')
                        {
                            at++;
                            FlowSpace(ref at);
                            value = at == raw.Length || raw[at] is ',' or '}' or ']'
                                ? Empty(at, valueDepth) : Inline(ref at, raw.Length, valueDepth, true);
                        }
                        else
                            value = Empty(at, valueDepth);
                        var entry = new MdYamlEntry(key, value);
                        if (mapping)
                            entries.Add(entry);
                        else
                        {
                            CountNode(depth + 1, key.RawSpan.Start);
                            children.Add(Collection(MdYamlKind.Mapping, key.RawSpan.Start, value.RawSpan.End, null, new[] { entry }, true));
                        }
                    }
                    else
                        children.Add(key);
                    FlowSpace(ref at);
                    if (at < raw.Length && raw[at] == ',')
                    {
                        at++;
                        continue;
                    }
                    if (at < raw.Length && raw[at] == close)
                        continue;
                    if (at <= before)
                        at++;
                    Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Expected a comma or the end of a flow collection.", Math.Min(at, raw.Length), Math.Min(at + 1, raw.Length));
                    break;
                }
            }
            catch (Stop stop)
            {
                stop.Partial = Collection(mapping ? MdYamlKind.Mapping : MdYamlKind.Sequence, start, Math.Min(at, raw.Length), children, entries, false);
                throw;
            }
            if (!complete)
                Diagnostic(MdYamlDiagnosticKind.Syntax, "syntax", "Unterminated YAML flow collection.", start, Math.Min(at, raw.Length));
            return Collection(mapping ? MdYamlKind.Mapping : MdYamlKind.Sequence, start, at, children, entries, complete);
        }

        private void FlowSpace(ref int at)
        {
            while (at < raw.Length)
            {
                cursor = at;
                Scan();
                if (char.IsWhiteSpace(raw[at]))
                    at++;
                else if (raw[at] == '#')
                    while (at < raw.Length && raw[at] is not ('\r' or '\n'))
                    {
                        Scan();
                        at++;
                    }
                else
                    break;
            }
        }

        private string Plain(int at, int end, bool flow, bool key, out int to)
        {
            var start = at;
            while (at < end)
            {
                cursor = at;
                Scan();
                var c = raw[at];
                if (c is '\r' or '\n' || flow && c is ',' or '[' or ']' or '{' or '}'
                    || c == '#' && (at == start || char.IsWhiteSpace(raw[at - 1]))
                    || c == ':' && (at + 1 == end || char.IsWhiteSpace(raw[at + 1]) || flow && raw[at + 1] is ',' or ']' or '}'))
                    break;
                at++;
            }
            to = TrimEnd(start, at);
            CheckScalar(to - start, start);
            return raw.Substring(start, to - start);
        }

        private int Colon(int start, int end)
        {
            var quote = '\0';
            var nested = 0;
            for (var at = start; at < end; at++)
            {
                Scan();
                var c = raw[at];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\')
                        at++;
                    else if (c == quote)
                    {
                        if (quote == '\'' && at + 1 < end && raw[at + 1] == '\'')
                            at++;
                        else
                            quote = '\0';
                    }
                }
                else if (at == start && c is '\'' or '"')
                    quote = c;
                else if (c is '[' or '{')
                    nested++;
                else if (c is ']' or '}')
                    nested--;
                else if (c == '#' && (at == start || char.IsWhiteSpace(raw[at - 1])))
                    return -1;
                else if (c == ':' && nested == 0 && (at + 1 == end || raw[at + 1] is ' ' or '\t'))
                    return at;
            }
            return -1;
        }

        private int SkipSpaces(int at, int end)
        {
            while (at < end && raw[at] is ' ' or '\t')
            {
                Scan();
                at++;
            }
            return at;
        }

        private int TrimEnd(int start, int end)
        {
            while (end > start && raw[end - 1] is ' ' or '\t')
                end--;
            return end;
        }

        private int FindLine(int at)
        {
            var low = 0;
            var high = lines.Count - 1;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (lines[middle].End <= at)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private void CheckKey(MdYamlSyntaxNode key, HashSet<string> keys)
        {
            if (key.Kind != MdYamlKind.Scalar)
                Diagnostic(MdYamlDiagnosticKind.Unsupported, "complexKey", "Complex YAML mapping keys are not supported.", key.RawSpan.Start, key.RawSpan.End);
            else if (key.ScalarKind == MdYamlScalarKind.Null)
                Diagnostic(MdYamlDiagnosticKind.Syntax, "invalidKey", "Mapping keys must be non-null scalars.", key.RawSpan.Start, key.RawSpan.End);
            else if (key.Value is not null && !keys.Add(CanonicalKey(key)))
                Diagnostic(MdYamlDiagnosticKind.Syntax, "duplicateKey", "A mapping key is duplicated; both entries are retained.", key.RawSpan.Start, key.RawSpan.End);
        }

        private string CanonicalKey(MdYamlSyntaxNode key)
        {
            var value = key.Value!;
            switch (key.ScalarKind)
            {
                case MdYamlScalarKind.Boolean:
                    return "bool/" + value.ToLowerInvariant();
                case MdYamlScalarKind.Integer:
                {
                    ChargeNumber(value.Length, key.RawSpan.Start);
                    BigInteger number;
                    if (value.StartsWith("0x", StringComparison.Ordinal))
                        number = BigInteger.Parse("0" + value.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    else if (value.StartsWith("0o", StringComparison.Ordinal))
                    {
                        number = BigInteger.Zero;
                        for (var i = 2; i < value.Length; i++)
                        {
                            if ((i & 127) == 0)
                                token.ThrowIfCancellationRequested();
                            number = number * 8 + (value[i] - '0');
                        }
                    }
                    else
                        number = BigInteger.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return "int/" + number.ToString(CultureInfo.InvariantCulture);
                }
                case MdYamlScalarKind.Float:
                {
                    ChargeNumber(value.Length, key.RawSpan.Start);
                    var lower = value.ToLowerInvariant();
                    if (lower.EndsWith(".inf", StringComparison.Ordinal))
                        return "float/" + (lower[0] == '-' ? "-inf" : "inf");
                    if (lower == ".nan")
                        return "float/nan";
                    var negative = value[0] == '-';
                    var unsigned = value[0] is '+' or '-' ? value.Substring(1) : value;
                    var e = unsigned.IndexOfAny(new[] { 'e', 'E' });
                    var exponent = e < 0 ? BigInteger.Zero : BigInteger.Parse(unsigned.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    var coefficient = e < 0 ? unsigned : unsigned.Substring(0, e);
                    var point = coefficient.IndexOf('.');
                    if (point >= 0)
                    {
                        exponent -= coefficient.Length - point - 1;
                        coefficient = coefficient.Remove(point, 1);
                    }
                    coefficient = coefficient.TrimStart('0');
                    if (coefficient.Length == 0)
                        return "float/0";
                    var last = coefficient.Length;
                    while (last > 0 && coefficient[last - 1] == '0')
                    {
                        exponent++;
                        last--;
                    }
                    return "float/" + (negative ? "-" : "") + coefficient.Substring(0, last) + "e" + exponent.ToString(CultureInfo.InvariantCulture);
                }
                default:
                    return "str/" + value;
            }
        }

        private void ChargeNumber(int length, int at)
        {
            cursor = at;
            var cost = (long)length * length;
            if (cost > int.MaxValue)
                throw new Stop("scan", "YAML numeric key normalization exceeds the processing budget.", at);
            Scan((int)cost);
        }

        private void CheckScalar(int length, int start)
        {
            if (length > options.MaxScalarUtf16)
                throw new Stop("scalar", "YAML scalar length limit exceeded.", start);
        }

        private void Diagnostic(MdYamlDiagnosticKind kind, string reason, string message, int start, int end)
        {
            token.ThrowIfCancellationRequested();
            if (diagnostics.Count >= options.MaxNodes)
                throw new Stop("nodes", "YAML diagnostic limit exceeded.", start);
            diagnostics.Add(new MdYamlDiagnostic(kind, reason, message, new MdRawRange(start, Math.Max(0, end - start))));
        }

        private MdYamlSyntaxNode Empty(int at, int depth)
        {
            CountNode(depth, at);
            return Scalar(at, at, "", MdYamlScalarStyle.Plain, true, true);
        }

        private MdYamlSyntaxNode Scalar(int start, int end, string value, MdYamlScalarStyle style, bool linear,
            bool complete, MdYamlScalarKind? scalarKind = null)
        {
            CheckScalar(value.Length, start);
            var span = new MdRawRange(start, end - start);
            var projection = linear && value.Length != 0
                ? new MdTextProjection(value, new[] { new MdSourceSegment(new MdRawRange(0, value.Length), span, MdSegmentKind.Linear) })
                : value.Length == 0 ? new MdTextProjection(value, MdFreeze.Empty<MdSourceSegment>()) : MdSourceMapping.Unknown(value);
            return new MdYamlSyntaxNode(MdYamlKind.Scalar, span, style,
                scalarKind ?? (style == MdYamlScalarStyle.Plain ? Resolve(value) : MdYamlScalarKind.String),
                projection, MdFreeze.Empty<MdYamlSyntaxNode>(), MdFreeze.Empty<MdYamlEntry>(), complete);
        }

        private static MdYamlSyntaxNode Collection(MdYamlKind kind, int start, int end,
            IReadOnlyList<MdYamlSyntaxNode>? children, IReadOnlyList<MdYamlEntry>? entries, bool complete) => new(kind,
                new MdRawRange(start, Math.Max(0, end - start)), MdYamlScalarStyle.Plain, MdYamlScalarKind.Unknown,
                MdSourceMapping.Unknown(null), children ?? MdFreeze.Empty<MdYamlSyntaxNode>(), entries ?? MdFreeze.Empty<MdYamlEntry>(), complete);
    }

    private static int Hex(char c) => c is >= '0' and <= '9' ? c - '0'
        : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    private static MdYamlScalarKind Resolve(string value)
    {
        if (value is "" or "~" or "null" or "Null" or "NULL")
            return MdYamlScalarKind.Null;
        if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
            return MdYamlScalarKind.Boolean;
        var at = value.Length > 0 && value[0] is '+' or '-' ? 1 : 0;
        if (at == value.Length)
            return MdYamlScalarKind.String;
        var from = at;
        if (at == 0 && value.Length > 2 && value[at] == '0' && value[at + 1] is 'x' or 'o')
        {
            var hex = value[at + 1] == 'x';
            at += 2;
            for (; at < value.Length; at++)
                if (hex ? Hex(value[at]) < 0 : value[at] is < '0' or > '7')
                    return MdYamlScalarKind.String;
            return MdYamlScalarKind.Integer;
        }
        for (; at < value.Length && value[at] is >= '0' and <= '9'; at++) { }
        if (at == value.Length)
            return MdYamlScalarKind.Integer;
        if (value is ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF"
            or "-.inf" or "-.Inf" or "-.INF" or ".nan" or ".NaN" or ".NAN")
            return MdYamlScalarKind.Float;
        var digits = at > from;
        if (value[at] == '.')
        {
            at++;
            for (; at < value.Length && value[at] is >= '0' and <= '9'; at++)
                digits = true;
        }
        if (!digits)
            return MdYamlScalarKind.String;
        if (at < value.Length && value[at] is 'e' or 'E')
        {
            at++;
            if (at < value.Length && value[at] is '+' or '-')
                at++;
            var exponent = at;
            for (; at < value.Length && value[at] is >= '0' and <= '9'; at++) { }
            if (at == exponent)
                return MdYamlScalarKind.String;
        }
        return at == value.Length ? MdYamlScalarKind.Float : MdYamlScalarKind.String;
    }
}
