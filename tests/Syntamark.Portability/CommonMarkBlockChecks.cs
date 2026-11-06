using System.Collections;
using System.Reflection;
using Syntamark;

internal static class CommonMarkBlockChecks
{
    private static readonly MarkdownParseOptions Standard = new(profileId: "commonmark/0.31.2");

    internal static void Run(Assembly runtime, Assembly portable)
    {
        var htmlCases = new[]
        {
            ("<ScRiPt>\r\n# false\r\n[bad](/wrong)\r\n</sTyLe>\r\n", ""),
            (" <!-- comment\r\n# false [bad](/wrong)\r\n--> tail\r\n", ""),
            ("<?processing\r\n# false [bad](/wrong)\r\n?>\r\n", ""),
            ("<!DOCTYPE\r\n# false [bad](/wrong)>\r\n", ""),
            ("<![CDATA[\r\n# false [bad](/wrong)\r\n]]>\r\n", ""),
            ("  <DIV invalid attribute\r\n# false [bad](/wrong)\r\n</div>\r\n", "\r\n"),
            ("<custom data-value='x'>\r\n# false [bad](/wrong)\r\n</custom>\r\n", "\r\n"),
        };
        foreach (var (rawHtml, gap) in htmlCases)
        {
            var input = rawHtml + gap + "# real\r\n[good](/ok)\r\n";
            var document = Parse(input);
            Check(document.Status == MarkdownParseStatus.Complete, "Recognized HTML changed parser termination status.");
            Check(document.Headings.Count == 1 && document.Headings[0].Text.Text == "real", "HTML emitted a false heading.");
            Check(document.Links.Count == 1 && document.Links[0].Target == "/ok", "HTML emitted a false link.");
            var opaque = document.Coverage.UnprojectedRegions.Single(r => r.Kind == "rawHtmlBlock");
            Check(opaque.RawSpan is { } span && Slice(input, span) == rawHtml, "HTML opaque range did not retain the exact original lines.");
            Check(document.Coverage.DecodedMapping == MarkdownCoverageState.Partial, "Unprojected HTML claimed complete prose coverage.");
            foreach (var assembly in new[] { runtime, portable })
            {
                var block = Blocks(assembly, input).First();
                Check(block.GetType().Name == "LithoHtmlBlock"
                    && Get<string>(block, "Text") == rawHtml.Replace("\r\n", "\n"), "Native HTML text or portable recognition differed.");
            }
        }

        const string inline = "paragraph\r\n<custom>\r\n[good](/ok)\r\n";
        var inlineDocument = Parse(inline);
        Check(inlineDocument.Links.Count == 1 && inlineDocument.Links[0].Target == "/ok"
            && inlineDocument.Coverage.UnprojectedRegions.All(r => r.Kind != "rawHtmlBlock"), "Type 7 HTML interrupted a paragraph.");
        const string code = "```\r\n<div>\r\n# false\r\n[bad](/wrong)\r\n```\r\n";
        var codeDocument = Parse(code);
        Check(codeDocument.Links.Count == 0 && codeDocument.Headings.Count == 0
            && codeDocument.Coverage.UnprojectedRegions.All(r => r.Kind != "rawHtmlBlock"), "Code content was reinterpreted as HTML or Markdown.");

        const string reference = "[\r\n Foo\r\n]:\r\n  <my url>\r\n  'title\r\nline'\r\n\r\n[Foo]\r\n";
        var referenceDocument = Parse(reference);
        var definition = referenceDocument.Links.Single(l => l.Kind == MarkdownLinkKind.ReferenceDefinition);
        Check(definition.Target == "my url" && definition.Title == "title\nline"
            && definition.TargetRawSpan is { } target && Slice(reference, target) == "my url", "Multiline definition target/title or target position changed.");
        Check(Slice(reference, definition.RawSpan) == "[\r\n Foo\r\n]:\r\n  <my url>\r\n  'title\r\nline'", "Definition raw range consumed neighboring prose.");
        Check(referenceDocument.Links.Single(l => l.Kind == MarkdownLinkKind.ReferenceUse).Target == "my url", "Multiline label did not resolve.");
        const string empty = "[e]: <>\r\n\r\n[e]\r\n";
        var emptyDefinition = Parse(empty).Links.Single(l => l.Kind == MarkdownLinkKind.ReferenceDefinition);
        Check(emptyDefinition.TargetRawSpan == new RawSpan(empty.IndexOf("<>", StringComparison.Ordinal) + 1, 0), "Empty definition target lost its known boundary.");

        const string tabFence = "> \t~~~lang\r\n> \t x\r\n> \t~~~\r\n";
        var fence = Parse(tabFence).Fences.Single();
        Check(Slice(tabFence, fence.OpeningMarkerSpan) == "~~~" && fence.ClosingMarkerSpan is { } close
            && Slice(tabFence, close) == "~~~" && fence.InfoSpan is { } info && Slice(tabFence, info) == "lang", "Tab container moved fence delimiters or info positions.");
        Check(fence.Content.Text == " x\n" && fence.Content.Map(new RawSpan(0, 1)).Precision == MarkdownMappingPrecision.CoveringTokens,
            "A partially removed tab must retain content spaces and covering mapping.");
        Check(Parse("# \u00A0heading\u00A0\n").Headings.Single().Text.Text == "\u00A0heading\u00A0",
            "ATX syntax stripped non-ASCII content whitespace.");
        Check(Parse("foo\n---\u00A0\n").Headings.Count == 0 && !Parse("~~~\nx\n~~~\u00A0\n").Fences.Single().Closed,
            "Non-ASCII whitespace was accepted after a setext underline or closing fence.");
        foreach (var input in new[] { "- foo\n\n\t\tbar\n", ">\t\tfoo\n", "-\t\tfoo\n" })
        {
            foreach (var assembly in new[] { runtime, portable })
            {
                var codeNode = Descendants(Blocks(assembly, input)).Single(n => n.GetType().Name == "LithoCode");
                Check(Get<string>(codeNode, "Text") == "  bar\n" || Get<string>(codeNode, "Text") == "  foo\n", "Partial indentation tab lost its remaining columns.");
            }
        }
        var nestedTabs = new[]
        {
            ("> >\t\tfoo\n", "foo\n"),
            ("- >\t\tfoo\n", "foo\n"),
            (">\t>\t\tfoo\n", "  foo\n"),
            ("> -\t\tfoo\n", "foo\n"),
            ("1. >\t\tfoo\n", "   foo\n"),
            ("\t\tfoo\n", "\tfoo\n"),
        };
        foreach (var (input, expectedCode) in nestedTabs)
        {
            Check(Parse(input).TextRegions.Single(region => region.Kind == "indentedCode").Content.Text == expectedCode,
                "Nested container tab stops lost their original visual column.");
            foreach (var assembly in new[] { runtime, portable })
                Check(Get<string>(Descendants(Blocks(assembly, input)).Single(node => node.GetType().Name == "LithoCode"), "Text") == expectedCode,
                    "Nested container code content differs between runtime and portable source.");
        }
        const string nestedTabFence = "> >\t~~~lang\r\n> >\t\tx\r\n> >\t~~~\r\n";
        var nestedFence = Parse(nestedTabFence).Fences.Single();
        Check(nestedFence.Content.Text == "\tx\n" && Slice(nestedTabFence, nestedFence.OpeningMarkerSpan) == "~~~"
            && nestedFence.ClosingMarkerSpan is { } nestedClose && Slice(nestedTabFence, nestedClose) == "~~~"
            && nestedFence.InfoSpan is { } nestedInfo && Slice(nestedTabFence, nestedInfo) == "lang",
            "Nested container tab stops moved fenced code or its syntax tokens.");
        var mappedTab = nestedFence.Content.Map(new RawSpan(0, 1));
        Check(mappedTab.Precision == MarkdownMappingPrecision.CoveringTokens
            && mappedTab.RawFragments.Single() == new RawSpan(nestedTabFence.IndexOf("\t\tx", StringComparison.Ordinal) + 1, 1)
            && Slice(nestedTabFence, mappedTab.RawFragments.Single()) == "\t",
            "Expanded nested-container indentation must map back to the exact original tab.");
        foreach (var input in new[] { "-\n  foo\n", "-   \n  foo\n", "- foo\n-   \n- bar\n" })
            foreach (var assembly in new[] { runtime, portable })
                Check(Get<bool>(Blocks(assembly, input).Single(), "Tight"), "An empty marker line counted as a blank line between blocks.");
        foreach (var assembly in new[] { runtime, portable })
            Check(!Get<bool>(Blocks(assembly, "- a\n- b\n\n  [ref]: /url\n- d\n").Single(), "Tight"),
                "A blank line before a reference definition failed to loosen the list.");
        foreach (var input in new[] { "> [r]: /u\n> foo\nbar\n", "- > [r]: /u\n  > foo\nbar\n" })
            foreach (var parseOptions in new[] { new MarkdownParseOptions(), Standard })
            {
                var links = MarkdownParser.Parse(input, "block-checks", "probe.md", options: parseOptions).Links
                    .Where(l => l.Kind == MarkdownLinkKind.ReferenceDefinition).ToArray();
                Check(links.Length == 1 && links[0].RawSpan == new RawSpan(input.StartsWith('>') ? 2 : 4, 7)
                    && Slice(input, links[0].RawSpan) == "[r]: /u",
                    "A structural lazy-continuation probe duplicated or moved the original reference definition.");
            }
        var longItem = "- start\n" + string.Concat(Enumerable.Repeat("continuation\n", 1600));
        Check(Parse(longItem).Status == MarkdownParseStatus.Complete,
            "Lazy list continuation exhausted the default scan budget through repeated structural probes.");
        Console.WriteLine("CommonMark block HTML, reference raw positions, and tab/fence checks PASS.");
    }

    internal static void ReviewReferenceRanges(Assembly runtime, Assembly portable)
    {
        var cases = new[]
        {
            ("quote-tab-eof", ">\t[r]: /u", "[r]: /u", "r", "/u"),
            ("quote-tab-use", ">\t[r]: /u\n>\t[r]\n", "[r]: /u", "r", "/u"),
            ("nested-quote-tab", ">>\t[r]: /u\r\n>>\t[r]\r\n", "[r]: /u", "r", "/u"),
            ("list-quote-tab", "1. >\t[r]: /u\n   >\t[r]\n", "[r]: /u", "r", "/u"),
            ("continued-target", ">\t[r]:\n>\t /u\n>\t[r]\n", "[r]:\n>\t /u", "r", "/u"),
            ("continued-label", "> [r\n>\tmore]: /u\n>\n> [r more]\n", "[r\n>\tmore]: /u", "r\n  more", "/u"),
            ("empty-target", ">\t[r]: <>\r\n>\t[r]\r\n", "[r]: <>", "r", ""),
            ("space-control", "  [r]: /u\n\n[r]\n", "  [r]: /u", "r", "/u"),
        };
        foreach (var assembly in new[] { runtime, portable })
            foreach (var (name, input, expectedDefinition, expectedLabel, expectedTarget) in cases)
            {
                var facts = ReviewFacts(assembly, input);
                Check(Get<object>(facts, "Status").ToString() == "Complete", name + ": reference parsing did not complete.");
                var links = ((IEnumerable)Get<object>(facts, "Links")).Cast<object>().ToArray();
                var definition = links.Single(link => Get<object>(link, "Kind").ToString() == "ReferenceDefinition");
                var definitionSpan = ReviewSpan(Get<object>(definition, "RawSpan"), input, name + " definition");
                Check(definitionSpan == new RawSpan(input.IndexOf(expectedDefinition, StringComparison.Ordinal), expectedDefinition.Length)
                    && Slice(input, definitionSpan) == expectedDefinition,
                    name + ": definition raw slice differs: " + Display(Slice(input, definitionSpan)));
                Check(Get<string>(definition, "Target") == expectedTarget, name + ": destination value changed.");
                var target = Get<object?>(definition, "TargetRawSpan");
                Check(target is not null, name + ": destination range became unknown.");
                var targetSpan = ReviewSpan(target!, input, name + " target");
                var targetStart = expectedTarget.Length == 0 ? input.IndexOf("<>", StringComparison.Ordinal) + 1
                    : input.IndexOf(expectedTarget, StringComparison.Ordinal);
                Check(targetSpan == new RawSpan(targetStart, expectedTarget.Length) && Slice(input, targetSpan) == expectedTarget,
                    name + ": destination raw slice differs at " + targetSpan.Start + ":" + targetSpan.Length);
                var label = Get<object>(definition, "Label");
                Check(Get<string>(label, "Text") == expectedLabel, name + ": label text differs.");
                var segments = ((IEnumerable)Get<object>(label, "SourceSegments")).Cast<object>().ToArray();
                Check(segments.Length != 0, name + ": label mapping is absent.");
                var firstLabelSpan = ReviewSpan(Get<object>(segments[0], "RawSpan"), input, name + " label start");
                Check(firstLabelSpan.Start == input.IndexOf("[r", StringComparison.Ordinal) + 1
                    && Slice(input, firstLabelSpan).StartsWith('r'), name + ": label starts outside the original r token.");
                var decodedCursor = 0;
                foreach (var segment in segments)
                {
                    var kind = Get<object>(segment, "Kind").ToString();
                    Check(kind is "Linear" or "Atomic", name + ": representable label mapping became unknown.");
                    var rawSpan = ReviewSpan(Get<object>(segment, "RawSpan"), input, name + " label segment");
                    var decodedSpan = ReviewSpan(Get<object>(segment, "DecodedSpan"), expectedLabel, name + " decoded label segment");
                    Check(decodedSpan.Start == decodedCursor, name + ": label mapping has a decoded gap or overlap at " + decodedCursor);
                    decodedCursor = decodedSpan.End;
                    if (kind == "Linear")
                        Check(Slice(input, rawSpan) == Slice(expectedLabel, decodedSpan), name + ": linear label mapping is not the original raw slice.");
                }
                Check(decodedCursor == expectedLabel.Length, name + ": label mapping dropped the decoded suffix at " + decodedCursor);
                if (name == "continued-label")
                {
                    var padding = segments.Single(segment => Get<object>(segment, "Kind").ToString() == "Atomic");
                    Check(Slice(input, ReviewSpan(Get<object>(padding, "RawSpan"), input, name)) == "\t"
                        && Slice(expectedLabel, ReviewSpan(Get<object>(padding, "DecodedSpan"), expectedLabel, name)) == "  ",
                        name + ": expanded label spaces lost the original tab mapping.");
                }
                var uses = links.Where(link => Get<object>(link, "Kind").ToString() == "ReferenceUse").ToArray();
                Check(uses.Length == (name == "quote-tab-eof" ? 0 : 1), name + ": reference use count differs.");
                foreach (var use in uses)
                {
                    ReviewSpan(Get<object>(use, "RawSpan"), input, name + " use");
                    Check(Get<object>(use, "Resolution").ToString() == "ResolvedReference" && Get<string>(use, "Target") == expectedTarget,
                        name + ": reference use no longer resolves to the original destination.");
                }
            }
        foreach (var assembly in new[] { runtime, portable })
        {
            const string legacy = ">\t[r]: /u\n>\t[r]\n";
            var links = ((IEnumerable)Get<object>(ReviewFacts(assembly, legacy, "lithosharp-markdown/1"), "Links")).Cast<object>().ToArray();
            var definition = links.Single(link => Get<object>(link, "Kind").ToString() == "ReferenceDefinition");
            Check(Slice(legacy, ReviewSpan(Get<object>(definition, "RawSpan"), legacy, "legacy control")) == "[r]: /u",
                "Legacy quote-tab definition range changed.");
        }
        Console.WriteLine("Review reference container/tab raw ranges, targets and label mappings PASS.");
    }

    internal static void ReviewHtmlTabs(Assembly runtime, Assembly portable)
    {
        var cases = new[]
        {
            ("quoted-whole-tab", "> <pre>\n> \tx\n> </pre>\n", "<pre>\n\tx\n</pre>\n", "<pre>\n> \tx\n> </pre>\n"),
            ("root-tab-control", "<pre>\n\tx\n</pre>\n", "<pre>\n\tx\n</pre>\n", "<pre>\n\tx\n</pre>\n"),
            ("nested-quote-tab", "> > <pre>\r\n> > \tx\r\n> > </pre>\r\n", "<pre>\n\tx\n</pre>\n", "<pre>\r\n> > \tx\r\n> > </pre>\r\n"),
            ("list-tab", "- <pre>\n  \tx\n  </pre>\n", "<pre>\n\tx\n</pre>\n", "<pre>\n  \tx\n  </pre>\n"),
            ("list-quote-tab", "1. > <pre>\n   > \tx\n   > </pre>\n", "<pre>\n\tx\n</pre>\n", "<pre>\n   > \tx\n   > </pre>\n"),
            ("partly-consumed-tab-control", ">\t<pre>\n>\tx\n>\t</pre>\n", "  <pre>\n  x\n  </pre>\n", "\t<pre>\n>\tx\n>\t</pre>\n"),
        };
        foreach (var assembly in new[] { runtime, portable })
            foreach (var (name, input, expected, expectedRaw) in cases)
            {
                var html = Descendants(Blocks(assembly, input)).Single(node => node.GetType().Name == "LithoHtmlBlock");
                var actual = Get<string>(html, "Text");
                Check(actual == expected, name + ": HTML literal text expected " + Display(expected) + " but got " + Display(actual));
                var nativeSpan = ReviewSpan(Get<object>(html, "Span"), input, name + " HTML block");
                Check(Slice(input, nativeSpan) == expectedRaw, name + ": native HTML raw envelope differs.");
                var facts = ReviewFacts(assembly, input);
                Check(Get<object>(facts, "Status").ToString() == "Complete", name + ": HTML parsing did not complete.");
                var coverage = Get<object>(facts, "Coverage");
                var opaque = ((IEnumerable)Get<object>(coverage, "UnprojectedRegions")).Cast<object>()
                    .Single(region => Get<string>(region, "Kind") == "rawHtmlBlock");
                Check(Slice(input, ReviewSpan(Get<object>(opaque, "RawSpan"), input, name + " opaque HTML")) == expectedRaw,
                    name + ": published HTML raw envelope differs.");
            }
        Console.WriteLine("Review HTML whole-tab preservation and partial-container-tab controls PASS.");
    }

    private static object ReviewFacts(Assembly assembly, string raw, string profile = "commonmark/0.31.2")
    {
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var options = assembly.GetType("Syntamark.Compilation.MdOptions", true)!.GetConstructors(instance)
            .Single(constructor => constructor.GetParameters().Length == 6)
            .Invoke(new object[] { 1048576, 131072, 200, 16777216, profile, 1 });
        return assembly.GetType("Syntamark.Compilation.MdParser", true)!.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Parse" && method.GetParameters().Length == 6)
            .Invoke(null, new object?[] { raw, "review-blocks", "container.md", null, options, CancellationToken.None })!;
    }

    private static RawSpan ReviewSpan(object range, string input, string subject)
    {
        var start = Get<int>(range, "Start");
        var length = Get<int>(range, "Length");
        Check(start >= 0 && length >= 0 && (long)start + length <= input.Length,
            subject + ": raw range " + start + ":" + length + " exceeds input length " + input.Length);
        return new RawSpan(start, length);
    }

    private static string Display(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static MarkdownDocument Parse(string raw) => MarkdownParser.Parse(raw, "block-checks", "input.md", options: Standard);
    private static string Slice(string raw, RawSpan span) => raw.Substring(span.Start, span.Length);
    private static T Get<T>(object value, string name) => (T)value.GetType()
        .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static object[] Blocks(Assembly assembly, string input)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var options = Activator.CreateInstance(assembly.GetType("Syntamark.Compilation.MdOptions", true)!, flags, null,
            new object[] { 1048576, 1048576, 200, 134217728, "commonmark/0.31.2", 1 }, null)!;
        var context = Activator.CreateInstance(assembly.GetType("Syntamark.Compilation.MdParseContext", true)!, flags, null,
            new[] { options, (object)CancellationToken.None }, null)!;
        var result = assembly.GetType("Syntamark.Compilation.LithoBlockParser", true)!.GetMethod("ParseFactsBlocks", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { input, context })!;
        return ((IEnumerable)result.GetType().GetField("Item1")!.GetValue(result)!).Cast<object>().ToArray();
    }
    private static IEnumerable<object> Descendants(IEnumerable<object> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var property = block.GetType().GetProperty(block.GetType().Name == "LithoList" ? "Items" : "Children");
            if (property?.GetValue(block) is IEnumerable children)
                foreach (var child in Descendants(children.Cast<object>()))
                    yield return child;
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
