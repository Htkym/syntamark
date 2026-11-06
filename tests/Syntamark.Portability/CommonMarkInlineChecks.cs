using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class CommonMarkInlineChecks
{
    internal static void Run(Assembly portable)
    {
        var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
        const string raw = "before <i title=\"[fake](/bad) *\">[Real](/ok)</i> &ngE; &#x1F600; &#0; \\* ~~x~~ $x$ https://example.test <foo+bar@example.test>\r\n";
        var document = MarkdownParser.Parse(raw, "commonmark", "inline-source", options: options);
        var portableOptions = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
            new object[] { 1048576, 131072, 200, 16777216, "commonmark/0.31.2", 1 });
        var portableDocument = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Parse")!
            .Invoke(null, new object?[] { raw, "commonmark", "inline-source", null, portableOptions, CancellationToken.None })!;
        Check(JsonSerializer.Serialize(document) == JsonSerializer.Serialize(portableDocument, portableDocument.GetType()),
            "CommonMark inline runtime/source facts differ.");
        Check(document.Status == MarkdownParseStatus.Complete, "Standard inline parsing must complete.");
        var links = document.Links.Where(link => link.Resolution == MarkdownLinkResolution.Inline).ToArray();
        Check(links.Length == 2 && links[0].Target == "/ok" && links[1].Target == "mailto:foo+bar@example.test",
            "HTML attributes and bare URLs must not create standard-profile links.");
        Check(Slice(raw, links[0].RawSpan) == "[Real](/ok)" && Slice(raw, links[0].TargetRawSpan!.Value) == "/ok",
            "Inline link positions must identify the original complete token and destination.");
        Check(Slice(raw, links[1].RawSpan) == "<foo+bar@example.test>", "Email autolink position must include its delimiters.");
        var text = document.TextRegions.Single(region => region.Kind == "paragraph").Content;
        Check(text.Text == "before Real ≧̸ 😀 � * ~~x~~ $x$ https://example.test foo+bar@example.test",
            "Raw HTML tokens must be excluded while their Markdown content and literal extensions remain.");
        AssertAtomic(raw, text, "≧̸", "&ngE;");
        AssertAtomic(raw, text, "😀", "&#x1F600;");
        AssertAtomic(raw, text, "�", "&#0;");
        var real = text.Map(new RawSpan(text.Text!.IndexOf("Real", StringComparison.Ordinal), 4));
        Check(real.Precision == MarkdownMappingPrecision.Exact && Slice(raw, real.RawFragments.Single()) == "Real",
            "Normal Markdown text between HTML tags must retain exact positions.");

        const string referenceRaw = "[ẞ ﬃ ς İ 𐐀]: /docs?a=&ngE; \"T &acE;\"\r\n\r\n[ss FFI σ i̇ 𐐨]\r\n";
        var reference = MarkdownParser.Parse(referenceRaw, "commonmark", "unicode-reference", options: options).Links;
        var definition = reference.Single(link => link.Kind == MarkdownLinkKind.ReferenceDefinition);
        var use = reference.Single(link => link.Kind == MarkdownLinkKind.ReferenceUse);
        Check(use.Resolution == MarkdownLinkResolution.ResolvedReference && use.Target == "/docs?a=≧̸" && use.Title == "T ∾̳",
            "Full Unicode case folding and HTML5 decoding must apply to reference definitions and uses.");
        Check(Slice(referenceRaw, definition.TargetRawSpan!.Value) == "/docs?a=&ngE;"
            && Slice(referenceRaw, use.RawSpan) == "[ss FFI σ i̇ 𐐨]" && use.TargetRawSpan == definition.TargetRawSpan,
            "Reference use and definition positions must remain distinct.");

        var scalarLabel = string.Concat(Enumerable.Repeat("😀", 999));
        var scalarReference = MarkdownParser.Parse("[" + scalarLabel + "]: /ok\n\n[" + scalarLabel + "]\n",
            "commonmark", "scalar-reference", options: options);
        Check(scalarReference.Links.Any(link => link.Resolution == MarkdownLinkResolution.ResolvedReference),
            "The 999-character reference-label limit must count Unicode characters, not UTF-16 units.");
        var tooLong = new string('a', 1000);
        Check(!MarkdownParser.Parse("[" + tooLong + "]: /bad\n\n[x][" + tooLong + "]\n", "commonmark", "long-reference", options: options)
            .Links.Any(link => link.Kind == MarkdownLinkKind.ReferenceDefinition || link.Resolution == MarkdownLinkResolution.ResolvedReference),
            "A 1000-character label must not become a reference definition or resolved reference.");

        const string referenceWhitespace = "[a\u00A0b]: /nbsp\n[\u00A0]: /space\n\n[a b] [a\u00A0b] [\u00A0]\n";
        var whitespaceLinks = MarkdownParser.Parse(referenceWhitespace, "commonmark", "label-whitespace", options: options).Links;
        Check(whitespaceLinks.Count(link => link.Resolution == MarkdownLinkResolution.ResolvedReference) == 2
            && whitespaceLinks.Single(link => link.ReferenceLabel == "a b").Resolution == MarkdownLinkResolution.UnresolvedReference,
            "Reference labels collapse spaces, tabs and line endings while preserving other Unicode whitespace.");

        const string hardBreakRaw = "> a  \r\n> b\n";
        var hardBreak = MarkdownParser.Parse(hardBreakRaw, "commonmark", "hard-break", options: options);
        var breakText = hardBreak.TextRegions.Single(region => region.Kind == "paragraph").Content;
        Check(breakText.Text == "a\nb" && Slice(hardBreakRaw, breakText.Map(new RawSpan(1, 1)).RawFragments.Single()) == "  \r\n",
            "Decoded hard break must cover the complete original token inside quote prefixes.");

        var escapedSpace = MarkdownParser.Parse("[x](a\\ b)", "commonmark", "non-escape", options: options);
        Check(!escapedSpace.Links.Any(link => link.Resolution == MarkdownLinkResolution.Inline)
            && escapedSpace.TextRegions.Single(region => region.Kind == "paragraph").Content.Text == "[x](a\\ b)",
            "A backslash before a space must not make a bare link destination valid.");
        const string nestedAutolinkRaw = "[outer <https://example.test>](target)";
        var nestedAutolink = MarkdownParser.Parse(nestedAutolinkRaw, "commonmark", "nested-autolink", options: options);
        Check(nestedAutolink.Links.Count == 1 && nestedAutolink.Links[0].Target == "https://example.test"
            && nestedAutolink.Links[0].Kind == MarkdownLinkKind.Autolink
            && nestedAutolink.TextRegions.Single(region => region.Kind == "paragraph").Content.Text == "[outer https://example.test](target)",
            "An autolink inside brackets must take precedence over a surrounding link.");
        var flatClosers = string.Concat(Enumerable.Repeat("word* ", 400));
        Check(MarkdownParser.Parse(flatClosers, "commonmark", "flat-delimiters", options: options).Status == MarkdownParseStatus.Complete,
            "Unmatched closing punctuation must not accumulate artificial nesting depth.");
        var unclosedBracket = MarkdownParser.Parse("*foo [bar* baz", "commonmark", "unclosed-bracket", options: options);
        Check(unclosedBracket.TextRegions.Single(region => region.Kind == "paragraph").Content.Text == "foo [bar baz",
            "An unmatched opening bracket must not block emphasis across it.");

        const string extensions = "~~x~~ $x$ https://example.test www.example.test\n";
        var standard = MarkdownParser.Parse(extensions, "commonmark", "standard-extensions", options: options);
        var legacy = MarkdownParser.Parse(extensions, "commonmark", "legacy-extensions");
        Check(standard.Links.Count == 0 && standard.TextRegions.Single().Content.Text == extensions.TrimEnd('\n'),
            "Standard Markdown must preserve extension syntax literally.");
        Check(legacy.Links.Count == 2 && legacy.TextRegions.Single().Content.Text == "x x https://example.test www.example.test",
            "The default profile must retain its existing strikethrough, math and bare-URL behavior.");
        Console.WriteLine(JsonSerializer.Serialize(new { task = "SM-04-inlines", status = "PASS", checks = "HTML precedence/raw spans/entities/Unicode references/profile compatibility" }));
    }

    internal static void ReviewShortcutFallback(Assembly portable)
    {
        var cases = new (string Id, string Raw, string Token, string? Target, string ReferenceLabel, bool Image, string Text, string Literal)[]
        {
            ("unclosed-following-label", "[foo][\n\n[foo]: /u\n", "[foo]", "/u", "foo", false, "foo[", "["),
            ("unclosed-image-label", "![foo][\n\n[foo]: /u\n", "![foo]", "/u", "foo", true, "foo[", "["),
            ("invalid-following-label", "[foo][ ]\n\n[foo]: /u\n", "[foo]", "/u", "foo", false, "foo[ ]", "[ ]"),
            ("full-precedence", "[foo][bar]\n\n[foo]: /shortcut\n[bar]: /full\n", "[foo][bar]", "/full", "bar", false, "foo", ""),
            ("collapsed-precedence", "[foo][][\n\n[foo]: /u\n", "[foo][]", "/u", "foo", false, "foo[", "["),
            ("unresolved-full", "[foo][missing]\n\n[foo]: /shortcut\n", "[foo][missing]", null, "missing", false, "[foo][missing]", ""),
        };
        var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
        var portableOptions = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
            new object[] { 1048576, 131072, 200, 16777216, "commonmark/0.31.2", 1 });
        var portableParse = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Parse")!;
        foreach (var item in cases)
        {
            var document = MarkdownParser.Parse(item.Raw, "review-shortcut", item.Id, options: options);
            var counterpart = portableParse.Invoke(null,
                new object?[] { item.Raw, "review-shortcut", item.Id, null, portableOptions, CancellationToken.None })!;
            Check(JsonSerializer.Serialize(document) == JsonSerializer.Serialize(counterpart, counterpart.GetType()),
                item.Id + ": runtime/source facts differ.");
            Check(document.Status == MarkdownParseStatus.Complete, item.Id + ": parsing must complete.");
            var uses = document.Links.Where(link => link.Kind == MarkdownLinkKind.ReferenceUse).ToArray();
            Check(uses.Length == 1, item.Id + ": exactly one reference use is expected.");
            var use = uses[0];
            Check(use.Target == item.Target && use.ReferenceLabel == item.ReferenceLabel && use.Image == item.Image
                && use.Label.Text == "foo"
                && use.Resolution == (item.Target is null ? MarkdownLinkResolution.UnresolvedReference : MarkdownLinkResolution.ResolvedReference),
                item.Id + ": reference target, label or resolution differs.");
            Check(use.RawSpan == new RawSpan(0, item.Token.Length) && Slice(item.Raw, use.RawSpan) == item.Token,
                item.Id + ": the reference raw token must exclude trailing literal text.");
            if (item.Target is not null)
            {
                var target = new RawSpan(item.Raw.LastIndexOf(item.Target, StringComparison.Ordinal), item.Target.Length);
                Check(use.TargetRawSpan == target && Slice(item.Raw, target) == item.Target,
                    item.Id + ": the target must point into its reference definition.");
            }
            else
                Check(use.TargetRawSpan is null, item.Id + ": unresolved full references have no target position.");
            var content = document.TextRegions.Single(region => region.Kind == "paragraph").Content;
            Check(content.Text == item.Text, item.Id + ": trailing literal text changed.");
            if (item.Literal.Length != 0)
            {
                var mapped = content.Map(new RawSpan(item.Text.Length - item.Literal.Length, item.Literal.Length));
                Check(mapped.Precision == MarkdownMappingPrecision.Exact
                    && string.Concat(mapped.RawFragments.Select(fragment => Slice(item.Raw, fragment))) == item.Literal,
                    item.Id + ": trailing literal source mapping differs.");
                var next = item.Token.Length;
                foreach (var fragment in mapped.RawFragments)
                {
                    Check(fragment.Start == next, item.Id + ": trailing literal raw position differs.");
                    next = fragment.End;
                }
                Check(next == item.Token.Length + item.Literal.Length, item.Id + ": trailing literal extent differs.");
            }
        }
        Console.WriteLine("PASS review shortcut fallback: raw tokens, literal tails, images and full/collapsed precedence.");
    }

    private static void AssertAtomic(string input, MarkdownTextProjection projection, string decoded, string token)
    {
        var at = projection.Text!.IndexOf(decoded, StringComparison.Ordinal);
        var mapped = projection.Map(new RawSpan(at, 1));
        Check(mapped.Precision == MarkdownMappingPrecision.CoveringTokens && Slice(input, mapped.RawFragments.Single()) == token,
            "Decoded entity must map to its complete raw token: " + token);
        if (decoded.Length > 1)
            Check(projection.Map(new RawSpan(at + 1, 0)).Precision == MarkdownMappingPrecision.Unknown,
                "An interior entity boundary must not claim an exact raw position: " + token);
    }

    private static string Slice(string input, RawSpan span) => input.Substring(span.Start, span.Length);
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
