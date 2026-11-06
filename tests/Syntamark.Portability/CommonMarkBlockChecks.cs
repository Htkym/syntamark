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

    private static MarkdownDocument Parse(string raw) => MarkdownParser.Parse(raw, "block-checks", "input.md", options: Standard);
    private static string Slice(string raw, RawSpan span) => raw.Substring(span.Start, span.Length);
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
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
