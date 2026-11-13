using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class FootnoteChecks
{
    internal static void Run(Assembly portable)
    {
        const string raw = "\uFEFF---\r\ntitle: Footnotes\r\n---\r\n\r\nText😀[^Two] + [^One] + [^Two] + [^missing]. `[^code]` \\[^escaped] <span title=\"[^html]\">literal</span>\r\n\r\n"
            + "[^One]: first\r\n\r\n    second *para* [x](doc.md)\r\n\r\n    > quote\r\n\r\n[^Two]: second\r\n[^ONE]: duplicate\r\n\r\n"
            + "```\r\n[^fake]: code\r\n[^fake]\r\n```\r\n";
        var result = MarkdownParser.AnalyzeFootnotes(raw, "footnotes", "doc.md", "v1");
        Check(result.IsEnabled && result.Definitions.Count == 3 && result.References.Count == 4,
            "Only parsed documentation footnotes should be collected; code and escaped markers stay literal.");
        Check(result.References.Select(reference => reference.Number).SequenceEqual(new int?[] { 1, 2, 1, null })
            && result.References.Select(reference => reference.Occurrence).SequenceEqual(new[] { 1, 1, 2, 1 })
            && !result.References[^1].IsResolved,
            "Display numbers follow first use; repeated references keep distinct backlink occurrences and unresolved labels stay unknown.");
        var first = result.Definitions[0];
        Check(first.Label == "One" && first.Number == 2 && !first.IsDuplicate
            && first.ContentRawSpans.Count == 3 && result.Definitions[2].IsDuplicate && result.Definitions[2].Number == 2,
            "Definitions preserve labels and multiple blocks; duplicate definitions remain visible without changing the resolved definition.");
        foreach (var reference in result.References)
            Check(Slice(raw, reference.RawSpan) == "[^" + reference.Label + "]"
                && Slice(raw, reference.LabelRawSpan) == reference.Label,
                "Footnote token and label spans must select original UTF-16 text after BOM/frontmatter/CRLF/emoji.");
        Check(Slice(raw, first.LabelRawSpan) == "One" && Slice(raw, first.RawSpan).EndsWith("> quote", StringComparison.Ordinal)
            && result.Analysis.Links!.Any(link => link.Target == "doc.md"),
            "Footnote blocks keep original positions and their links participate in the regular fact projection.");
        Check(result.Analysis.Diagnostics.Count(diagnostic => diagnostic.Id == "LMD030") == 1
            && result.Analysis.Diagnostics.Count(diagnostic => diagnostic.Id == "LMD031") == 1
            && result.Analysis.Diagnostics.Where(diagnostic => diagnostic.Origin == "footnote")
                .All(diagnostic => diagnostic.RawSpan is { } span && Slice(raw, span) is "ONE" or "missing"),
            "Duplicate and unresolved diagnostics must preserve the original label positions.");
        var syntax = MarkdownParser.Analyze(raw, MarkdownAnalysisParts.Syntax | MarkdownAnalysisParts.UnsupportedSyntax,
            "footnotes", "doc.md", options: new MarkdownParseOptions(profileId: "syntamark-docs/2"));
        Check(syntax.Root!.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.FootnoteDefinition) == 3
            && syntax.Root.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.FootnoteReference) == 4
            && !syntax.UnsupportedSyntax!.Any(candidate => candidate.Kind == MarkdownUnsupportedSyntaxKind.Footnote),
            "Enabled footnotes should be syntax nodes, not unsupported extension candidates.");
        foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
        {
            var unchanged = MarkdownParser.AnalyzeFootnotes(raw, "footnotes", "doc.md",
                options: new MarkdownParseOptions(profileId: profile));
            Check(!unchanged.IsEnabled && unchanged.Definitions.Count == 0 && unchanged.References.Count == 0,
                "Existing profile identifiers must retain their original footnote behavior.");
        }
        var portableResult = portable.GetType("Syntamark.MarkdownParser")!.GetMethod("AnalyzeFootnotes")!
            .Invoke(null, new object?[] { raw, "footnotes", "doc.md", "v1", null, CancellationToken.None })!;
        Check(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(portableResult, portableResult.GetType()),
            "Runtime and compiler-host targets must return identical footnotes, positions, diagnostics and identities.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            MarkdownParser.AnalyzeFootnotes(raw, "footnotes", "doc.md", cancellationToken: cancelled.Token);
            throw new InvalidOperationException("Cancelled footnote parsing unexpectedly returned a result.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("Footnote profile, source positions, repeated backlinks and portable parity: PASS");
    }

    private static string Slice(string text, RawSpan span) => text.Substring(span.Start, span.Length);
    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
