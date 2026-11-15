using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class SyntaxPositionChecks
{
    internal static void Run(Assembly portable)
    {
        var docs = new MarkdownParseOptions(profileId: "syntamark-docs/2");
        const string input = "\uFEFFintro\r\n\r\n# A &amp; 😀 ###\r\n\r\n[label `]`][ref]\r\n\r\n[ref]: /destination\r\n\r\n:::Card title=\"A \\\"quote\\\" 😀\" count=2\r\nbody\r\n:::\r\n\r\n```cs\r\na😀\r\n```\r\n\r\n| A | B |\r\n| --- | --- |\r\n| x\\|y | |\r\n| z |\r\n";
        var parsed = MarkdownParser.ParseWithSyntax(input, "syntax", "positions", "r1", docs);
        var nodes = parsed.Root!.DescendantsAndSelf().ToArray();
        var heading = nodes.Single(node => node.Kind == MarkdownSyntaxKind.Heading);
        Check(Slice(heading.ContentRawSpan) == "A &amp; 😀", "Heading content excludes ATX and closing markers.");
        Check(heading.Content!.Text == "A & 😀" && heading.Content.Map(new RawSpan(2, 1)).Precision == MarkdownMappingPrecision.CoveringTokens,
            "Decoded heading content retains the entity token's source mapping.");
        var link = nodes.Single(node => node.Kind == MarkdownSyntaxKind.Link);
        Check(Slice(link.ContentRawSpan) == "label `]`" && Slice(link.ReferenceLabelRawSpan) == "ref"
            && Slice(link.TargetRawSpan) == "/destination", "Link label, code-span bracket, reference label and destination differ.");
        var directive = nodes.Single(node => node.Kind == MarkdownSyntaxKind.Directive).Directive!;
        Check(directive.Name == "Card" && Slice(directive.NameRawSpan) == "Card"
            && Slice(directive.OpeningMarkerRawSpan) == ":::" && Slice(directive.ClosingMarkerRawSpan) == ":::"
            && Slice(directive.ContentRawSpan) == "body\r\n" && directive.IsComplete,
            "Directive name, fence and body positions use original UTF-16 offsets.");
        var title = directive.Attributes[0];
        Check(title.Name == "title" && Slice(title.NameRawSpan) == "title" && title.Value!.Text == "A \"quote\" 😀"
            && Slice(title.ValueRawSpan) == "A \\\"quote\\\" 😀" && Slice(title.OpeningQuoteRawSpan) == "\""
            && Slice(title.ClosingQuoteRawSpan) == "\""
            && title.Value.Map(new RawSpan(2, 1)).Precision == MarkdownMappingPrecision.CoveringTokens,
            "Escaped attribute quotes retain token mapping without changing raw quote positions.");
        var code = nodes.Single(node => node.Kind == MarkdownSyntaxKind.FencedCodeBlock);
        Check(Slice(code.ContentRawSpan) == "a😀\r\n" && code.Content!.Text == "a😀\n"
            && code.Content.Map(new RawSpan(0, 3)).RawFragments.Single() == new RawSpan(input.IndexOf("a😀", StringComparison.Ordinal), 3),
            "Code content mapping distinguishes CRLF from decoded LF.");
        var table = nodes.Single(node => node.Kind == MarkdownSyntaxKind.Table);
        Check(table.TableCells.Count == 6 && Slice(table.TableCells[2].RawSpan) == "x\\|y"
            && table.TableCells[2].Content.Text == "x|y" && table.TableCells[3].RawSpan is { Length: 0 }
            && table.TableCells[5].RawSpan is null,
            "Table cells keep escaped pipes, real empty cells and synthesized missing cells distinct.");

        const string broken = ":::Card key=one key='two' missing empty=\r\n:::";
        var partialDocument = MarkdownParser.ParseWithSyntax(broken, "syntax", "partial", options: docs);
        var partial = partialDocument.Root!
            .DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Directive).Directive!;
        Check(!partial.IsComplete && partial.Attributes.Count == 4
            && partial.Diagnostics.Any(diagnostic => diagnostic.Reason == "duplicateAttribute")
            && partial.Diagnostics.Count(diagnostic => diagnostic.Reason == "missingValue") == 2,
            "Duplicate and missing attributes remain available with diagnostics.");
        CheckDirectiveDiagnostics(broken, partialDocument);
        var mixedDiagnostics = broken + "\r\n\r\n    indented code\r\n";
        CheckDirectiveDiagnostics(mixedDiagnostics,
            MarkdownParser.ParseWithSyntax(mixedDiagnostics, "syntax", "partial", options: docs));
        const string offsetBroken = "\uFEFF---\r\ntitle: test\r\n---\r\n> :::Card x='unfinished\r\n> body";
        var offsetDocument = MarkdownParser.ParseWithSyntax(offsetBroken, "syntax", "partial", options: docs);
        CheckDirectiveDiagnostics(offsetBroken, offsetDocument);
        Check(offsetDocument.Facts.Diagnostics.Single(diagnostic => diagnostic.Reason == "unclosedQuote").RawSpan
            == new RawSpan(offsetBroken.IndexOf('\''), 1), "Directive diagnostics use complete-input offsets after BOM/frontmatter/container stripping.");
        var unclosed = MarkdownParser.ParseWithSyntax(":::Card title=\"unfinished\r\nbody", "syntax", "unclosed", options: docs)
            .Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Directive).Directive!;
        Check(!unclosed.IsComplete && unclosed.ClosingMarkerRawSpan is null
            && unclosed.Attributes.Single().ClosingQuoteRawSpan is null
            && unclosed.Diagnostics.Any(diagnostic => diagnostic.Reason == "unclosedQuote")
            && unclosed.Diagnostics.Any(diagnostic => diagnostic.Reason == "unterminated"),
            "Unclosed quote and fence do not become exact, complete editing input.");
        var legacy = MarkdownParser.ParseWithSyntax(":::Card title=\"literal title\"\nbody\n:::", "syntax", "legacy")
            .Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Directive).Directive!;
        Check(legacy.Attributes.Count == 0 && legacy.Diagnostics.Count == 0, "Existing profile header interpretation is unchanged.");
        var opaque = MarkdownParser.ParseWithSyntax("```\n:::Card title=x\n```\n", "syntax", "opaque", options: docs);
        Check(!opaque.Root!.DescendantsAndSelf().Any(node => node.Directive is not null), "Code content is not reinterpreted as a directive.");
        var nested = MarkdownParser.ParseWithSyntax("> :::Card title='nested'\r\n> body\r\n> :::\r\n", "syntax", "nested", options: docs);
        var nestedDirective = nested.Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Directive).Directive!;
        Check(nestedDirective.NameRawSpan.Start == 5 && nestedDirective.Attributes.Single().Value!.Text == "nested"
            && nestedDirective.IsComplete, "Nested directives keep raw container offsets.");
        var splitHeading = MarkdownParser.ParseWithSyntax("> first\r\n> second\r\n> ===\r\n", "syntax", "split-heading",
            options: new MarkdownParseOptions(profileId: "commonmark/0.31.2"))
            .Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Heading);
        Check(splitHeading.ContentRawSpan is null
            && splitHeading.Content!.Map(new RawSpan(0, splitHeading.Content.Text!.Length)).RawFragments.Count > 1,
            "Removed container prefixes do not produce a false contiguous editing range.");

        var portableOptions = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
            [1048576, 131072, 200, 16777216, "syntamark-docs/2", 1]);
        var counterpart = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("ParseWithSyntax")!
            .Invoke(null, [input, "syntax", "positions", "r1", portableOptions, CancellationToken.None])!;
        Check(JsonSerializer.Serialize(parsed) == JsonSerializer.Serialize(counterpart, counterpart.GetType()),
            "Runtime and netstandard syntax positions and directive facts agree.");
        Console.WriteLine("Syntax position and directive checks passed.");

        string? Slice(RawSpan? span) => span.HasValue ? input.Substring(span.Value.Start, span.Value.Length) : null;

        void CheckDirectiveDiagnostics(string source, MarkdownSyntaxDocument withSyntax)
        {
            var factsOnly = MarkdownParser.Parse(source, "syntax", "partial", options: docs);
            var syntaxDiagnostics = withSyntax.Facts.Diagnostics.Where(diagnostic => diagnostic.Origin == "directive").ToArray();
            var ordinaryDiagnostics = factsOnly.Diagnostics.Where(diagnostic => diagnostic.Origin == "directive").ToArray();
            Check(factsOnly.Status == MarkdownParseStatus.Partial && withSyntax.Facts.Status == MarkdownParseStatus.Partial
                && syntaxDiagnostics.Length != 0
                && syntaxDiagnostics.Select(diagnostic => (diagnostic.Id, diagnostic.RawSpan, diagnostic.Reason)).Distinct().Count()
                    == syntaxDiagnostics.Length
                && JsonSerializer.Serialize(syntaxDiagnostics) == JsonSerializer.Serialize(ordinaryDiagnostics),
                "Facts-only and syntax analysis return the same non-duplicated directive diagnostics.");
            Check(JsonSerializer.Serialize(withSyntax.Facts.Diagnostics) == JsonSerializer.Serialize(factsOnly.Diagnostics),
                "Directive and mapping diagnostics retain the same ordering across analysis entry points.");
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}
