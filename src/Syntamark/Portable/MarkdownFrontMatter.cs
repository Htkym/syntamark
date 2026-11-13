using System;
using System.Collections.Generic;

namespace Syntamark.Compilation;

internal static class MdFrontMatterParser
{
    internal static (MdFrontMatter Facts, MdRawRange? Body, MdDiagnostic? Diagnostic) Split(string raw, MdParseContext context)
    {
        if (context.Options.InputKind == MdInputKind.Body || context.Options.IsCommonMark)
            return (new MdFrontMatter(MdFrontMatterState.Absent, null, null, null, null), new MdRawRange(0, raw.Length), null);
        var envelope = MdFrontMatterScan.Scan(raw, context.CancellationToken, context);
        if (!envelope.Opening.HasValue)
            return (new MdFrontMatter(MdFrontMatterState.Absent, null, null, null, null), envelope.Body, null);
        if (!envelope.Closing.HasValue)
            return (new MdFrontMatter(MdFrontMatterState.Unterminated, envelope.Opening, envelope.Yaml, null, null,
                    new MdRawRange(raw.Length, 0), envelope.FailureLine),
                context.Options.FrontMatterRecovery == MdFrontMatterRecovery.TreatAsBody ? new MdRawRange(0, raw.Length) : null,
                Diagnostic("LMD004", "Frontmatter has no closing delimiter.", envelope.Opening, "unterminated"));
        var yaml = envelope.Yaml!.Value;
        var yamlText = raw.Substring(yaml.Start, yaml.Length);
        if (context.Inspect(yamlText).Trim().Length == 0)
            return (new MdFrontMatter(MdFrontMatterState.Empty, envelope.Opening, yaml, envelope.Closing, null), envelope.Body, null);
        var parsed = MdYamlParser.ParseFrontMatter(yamlText, context);
        var root = parsed.Root;
        MdDiagnostic? diagnostic = null;
        if (parsed.Diagnostics.Count != 0)
        {
            var first = parsed.Diagnostics[0];
            if (first.Kind == MdYamlDiagnosticKind.ResourceLimit)
                throw new MdResourceLimit(first.Reason == "nodes" ? "output" : first.Reason);
            diagnostic = Diagnostic("LMD005", first.Message,
                new MdRawRange(yaml.Start + first.RawSpan.Start, first.RawSpan.Length), first.Reason);
        }
        if (root is not null && root.Kind != MdYamlKind.Mapping)
            diagnostic = Diagnostic("LMD005", "Frontmatter YAML root must be a mapping.",
                new MdRawRange(yaml.Start + root.RawSpan.Start, root.RawSpan.Length), "invalidRoot");
        if (root is null && diagnostic is null)
            return (new MdFrontMatter(MdFrontMatterState.Empty, envelope.Opening, yaml, envelope.Closing, null) { YamlSyntax = parsed }, envelope.Body, null);
        var projected = root?.Kind == MdYamlKind.Mapping ? Project(root, yaml.Start, context) : null;
        return (new MdFrontMatter(diagnostic is null ? MdFrontMatterState.Parsed : MdFrontMatterState.Invalid,
            envelope.Opening, yaml, envelope.Closing, projected) { YamlSyntax = parsed }, envelope.Body, diagnostic);
    }

    private static MdDiagnostic Diagnostic(string id, string message, MdRawRange? span, string reason) =>
        new(id, MdDiagnosticSeverity.Warning, message, span, "frontmatter", reason);

    private static MdYamlNode Project(MdYamlSyntaxNode node, int offset, MdParseContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var children = new List<MdYamlNode>();
        foreach (var entry in node.Entries)
        {
            context.Scan();
            children.Add(Project(entry.Key, offset, context));
            children.Add(Project(entry.Value, offset, context));
        }
        foreach (var child in node.Children)
        {
            context.Scan();
            children.Add(Project(child, offset, context));
        }
        var segments = new List<MdSourceSegment>();
        foreach (var segment in node.Projection.SourceSegments)
        {
            context.Emit();
            segments.Add(new MdSourceSegment(segment.DecodedSpan,
                segment.RawSpan.HasValue ? new MdRawRange(segment.RawSpan.Value.Start + offset, segment.RawSpan.Value.Length) : null,
                segment.Kind));
        }
        return new MdYamlNode(node.Kind, new MdRawRange(node.RawSpan.Start + offset, node.RawSpan.Length),
            new MdTextProjection(node.Value, segments), children);
    }
}
