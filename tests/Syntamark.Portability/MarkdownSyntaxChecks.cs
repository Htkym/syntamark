using System.Reflection;
using System.Text.Json;
using Syntamark;

// Uses only the public runtime API; no friend assembly grants are needed by this consumer.
internal static class MarkdownSyntaxChecks
{
    internal static void Run(Assembly portable)
    {
        const string input = "\uFEFFintro\r\n\r\n# A &amp; 😀\r\n\r\n> paragraph [Guide](/docs)\r\n\r\n3. item\r\n\r\n   ```cs\r\n   x\r\n   ```\r\n\r\n[use][r]\r\n\r\n[r]: /ref\r\n";
        foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
        {
            var options = new MarkdownParseOptions(profileId: profile);
            var syntax = MarkdownParser.ParseWithSyntax(input, "syntax", "three-cases", options: options);
            var oldEntry = MarkdownParser.Parse(input, "syntax", "three-cases", options: options);
            Check(JsonSerializer.Serialize(syntax.Facts) == JsonSerializer.Serialize(oldEntry), profile + ": facts from the same parse contract");
            var root = syntax.Root ?? throw new InvalidOperationException("Expected a syntax root.");
            Check(root.Kind == MarkdownSyntaxKind.Document && root.RawSpan == new RawSpan(0, input.Length)
                && root.GetRawText() == input, profile + ": owned full-input root");
            var heading = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Heading);
            Check(heading.HeadingLevel == 1 && heading.RawSpan == syntax.Facts.Headings.Single().RawSpan
                && heading.GetRawText() == "# A &amp; 😀", profile + ": heading markers and facts position");
            Check(heading.Children.Single(node => node.Text == "&").GetRawText() == "&amp;", profile + ": entity raw envelope uses token units");
            var quote = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.BlockQuote);
            var link = quote.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Link);
            Check(link.GetRawText() == "[Guide](/docs)" && link.Target == "/docs"
                && link.RawSpan == syntax.Facts.Links.Single(fact => fact.Target == "/docs").RawSpan,
                profile + ": quote/link exploration");
            var list = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.OrderedList);
            var code = list.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.FencedCodeBlock);
            Check(list.OrderedListStart == 3 && list.IsTightList == false && code.CodeInfo == "cs"
                && code.Text == "x\n" && code.RawSpan == syntax.Facts.Fences.Single().RawSpan,
                profile + ": list/code details");
            var reference = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Link && node.ReferenceLabel == "r");
            var referenceFact = syntax.Facts.Links.Single(fact => fact.Kind == MarkdownLinkKind.ReferenceUse);
            Check(reference.TargetRawSpan == referenceFact.TargetRawSpan && reference.TargetRawSpan!.Value.Start > reference.RawSpan!.Value.End
                && reference.Children.All(child => child.RawSpan!.Value.End <= reference.RawSpan.Value.End),
                profile + ": definition is a semantic edge outside syntax children");
            Check(root.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.Link) == 2,
                profile + ": reference definition is separate facts");
            Invariants(root, input);

            var portableOptions = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
                [1048576, 131072, 200, 16777216, profile, 1]);
            var counterpart = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("ParseWithSyntax")!
                .Invoke(null, [input, "syntax", "three-cases", null, portableOptions, CancellationToken.None])!;
            Check(JsonSerializer.Serialize(syntax) == JsonSerializer.Serialize(counterpart, counterpart.GetType()),
                profile + ": runtime/portable syntax and facts identity");
            var expected = root.DescendantsAndSelf().Select(node => (node.Kind, node.RawSpan)).ToArray();
            Parallel.For(0, 8, _ => Check(expected.SequenceEqual(root.DescendantsAndSelf().Select(node => (node.Kind, node.RawSpan))),
                profile + ": concurrent immutable traversal"));
            using var canceled = new CancellationTokenSource();
            using var traversal = root.DescendantsAndSelf(canceled.Token).GetEnumerator();
            Check(traversal.MoveNext(), "Root is visible before cancellation");
            canceled.Cancel();
            ThrowsCanceled(() => traversal.MoveNext());
            ThrowsCanceled(() => MarkdownParser.ParseWithSyntax(input, "syntax", "cancel", cancellationToken: canceled.Token));
            foreach (var newline in new[] { "\n", "\r\n" })
                foreach (var marker in new[] { "  ", "   ", "\\" })
                {
                    var breakInput = "> a" + marker + newline + "> b" + newline;
                    var breaks = MarkdownParser.ParseWithSyntax(breakInput, "syntax", "hard-break", options: options);
                    var hard = breaks.Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.HardLineBreak);
                    Check(hard.Text == "\n" && hard.GetRawText() == marker + newline,
                        profile + ": hard break envelope includes raw marker and source line ending");
                    Invariants(breaks.Root, breakInput);
                }
        }

        var standard = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
        var unmatchedBracket = MarkdownParser.ParseWithSyntax("*foo [bar* baz", "syntax", "unmatched-bracket", options: standard);
        Check(unmatchedBracket.Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Emphasis).GetRawText() == "*foo [bar*",
            "An unmatched bracket does not block the source envelope of emphasis.");
        var innerAutolink = MarkdownParser.ParseWithSyntax("[outer <https://example.test>](target)", "syntax", "inner-autolink", options: standard);
        Check(innerAutolink.Root!.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.Autolink) == 1
            && !innerAutolink.Root.DescendantsAndSelf().Any(node => node.Kind == MarkdownSyntaxKind.Link),
            "An inner autolink prevents a containing link syntax node.");
        foreach (var prefix in new[] { "", "> " })
        {
            var markerInput = prefix + "***x***\r\n";
            var nested = MarkdownParser.ParseWithSyntax(markerInput, "syntax", "nested-emphasis", options: standard);
            var emphasis = nested.Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Emphasis);
            var strong = emphasis.Children.Single(node => node.Kind == MarkdownSyntaxKind.StrongEmphasis);
            Check(emphasis.RawSpan == new RawSpan(prefix.Length, 7) && strong.RawSpan == new RawSpan(prefix.Length + 1, 5)
                && emphasis.GetRawText() == "***x***" && strong.GetRawText() == "**x**",
                "CommonMark nested marker ranges include exactly their own delimiter tokens");
            Invariants(nested.Root, markerInput);
        }
        foreach (var markerInput in new[] { "**x*", "*x**" })
        {
            var literal = MarkdownParser.ParseWithSyntax(markerInput, "syntax", "literal-delimiter", options: standard);
            Check(literal.Root!.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Text && node.Text == "*")
                .GetRawText() == "*", "CommonMark unmatched delimiter retains its original single marker position");
            Invariants(literal.Root, markerInput);
        }
        foreach (var legacyInput in new[] { "*foo **bar***\n", "**foo*\n", "foo******bar*********baz\n" })
        {
            var legacy = MarkdownParser.ParseWithSyntax(legacyInput, "syntax", "legacy-emphasis");
            var ordinaryLegacy = MarkdownParser.Parse(legacyInput, "syntax", "legacy-emphasis");
            Check(JsonSerializer.Serialize(legacy.Facts) == JsonSerializer.Serialize(ordinaryLegacy)
                && legacy.Root!.DescendantsAndSelf().Any(node => node.RawSpan is null
                    && node.Kind is MarkdownSyntaxKind.Emphasis or MarkdownSyntaxKind.StrongEmphasis),
                "Contradictory legacy emphasis positions are explicitly unknown without changing facts");
            Invariants(legacy.Root!, legacyInput);
            var commonMark = MarkdownParser.ParseWithSyntax(legacyInput, "syntax", "standard-emphasis", options: standard);
            Check(commonMark.Root!.DescendantsAndSelf().All(node => node.RawSpan is not null),
                "CommonMark emphasis positions are known; legacy accommodation does not mask standard ranges");
            Invariants(commonMark.Root, legacyInput);
        }

        var html = MarkdownParser.ParseWithSyntax("<div>\n# hidden\n</div>\n\na <span>**b** [x](/u)</span>\n", "syntax", "html",
            options: new MarkdownParseOptions(profileId: "commonmark/0.31.2"));
        Check(html.Root!.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.HtmlBlock) == 1
            && html.Root.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.HtmlInline) == 2
            && !html.Root.DescendantsAndSelf().Any(node => node.Kind == MarkdownSyntaxKind.Heading)
            && html.Root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.StrongEmphasis).GetRawText() == "**b**",
            "Native HTML block/inline keeps Markdown between inline tags");
        Invariants(html.Root, html.Root.GetRawText()!);

        var early = MarkdownParser.ParseWithSyntax("# A", "syntax", "scan", options: new MarkdownParseOptions(maxScanUnits: 1));
        Check(early.Root is null && early.Facts.Status == MarkdownParseStatus.Partial
            && early.Facts.Diagnostics.Any(diagnostic => diagnostic.Id == "LMD003"), "Early resource stop is not an empty successful tree");
        var failed = MarkdownParser.ParseWithSyntax("# A", "syntax", "input", options: new MarkdownParseOptions(maxInputUtf16: 1));
        Check(failed.Root is null && failed.Facts.Status == MarkdownParseStatus.Failed, "Rejected input has no fabricated root");
        const string budgetInput = "plain";
        var ordinary = MarkdownParser.Parse(budgetInput, "syntax", "output", options: new MarkdownParseOptions(maxOutputItems: 5));
        var limited = MarkdownParser.ParseWithSyntax(budgetInput, "syntax", "output", options: new MarkdownParseOptions(maxOutputItems: 5));
        Check(ordinary.Status == MarkdownParseStatus.Complete && limited.Root is null && limited.Facts.Status == MarkdownParseStatus.Partial
            && limited.Facts.Diagnostics.Any(diagnostic => diagnostic.Id == "LMD003" && diagnostic.Reason == "output")
            && limited.Facts.TextRegions.Single().Content.Text == "plain", "Syntax output charges preserve published facts on stop");
        var deep = MarkdownParser.ParseWithSyntax(new string('>', 80) + " # Deep\n", "syntax", "deep");
        Check(deep.Root is not null && deep.Root.DescendantsAndSelf().Count(node => node.Kind == MarkdownSyntaxKind.BlockQuote) == 80,
            "Deep syntax construction and traversal use explicit stacks");
        Console.WriteLine("PASS syntax: public consumer, raw hierarchy/facts, semantic references, HTML, runtime/source, concurrency/cancel and budget.");
    }

    private static void Invariants(MarkdownSyntaxNode root, string raw)
    {
        foreach (var node in root.DescendantsAndSelf())
        {
            if (node.RawSpan is not { } parent)
                continue;
            Check(parent.End <= raw.Length && node.GetRawText() == raw.Substring(parent.Start, parent.Length), "Syntax raw slice");
            var previous = parent.Start;
            foreach (var child in node.Children)
                if (child.RawSpan is { } span)
                {
                    Check(span.Start >= previous && span.Start >= parent.Start && span.End <= parent.End,
                        "Ordered contained child: " + node.Kind + " " + parent + " / " + child.Kind + " " + span);
                    previous = span.End;
                }
        }
    }
    private static void ThrowsCanceled(Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Cancellation returned a successful result.");
        }
        catch (OperationCanceledException) { }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
