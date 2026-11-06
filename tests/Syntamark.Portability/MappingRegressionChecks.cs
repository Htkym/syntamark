using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class MappingRegressionChecks
{
    internal static void Run(Assembly portable)
    {
        var cases = new (string Id, string Input)[]
        {
            ("escaped-pipe", "| H |\n| - |\n| a\\|b [Guide](/docs) |\n"),
            ("repeated-empty-cells", "| H | H | H |\n| - | - | - |\n| a\\|b [Guide](/docs) | | a\\|b [Guide](/docs) |\n"),
            ("multiple-escaped-pipes", "| H |\n| - |\n| a😀\\|b\\|c [Guide](/docs) |\n"),
            ("quote-table-crlf", "> | H |\r\n> | - |\r\n> | a\\|b [Guide](/docs) |\r\n"),
            ("list-table", "- | H |\n  | - |\n  | a\\|b [Guide](/docs) |\n"),
            ("bom-frontmatter-table", "\uFEFF---\r\ntitle: X\r\n---\r\n| H |\r\n| - |\r\n| a\\|b [Guide](/docs) |\r\n"),
            ("empty-destination", "before [x]() [y](<>)\n"),
            ("table-empty-destination", "| H |\n| - |\n| a\\|b [x]() [y](<>) |\n"),
            ("quote-empty-destination", "> before [x]() [y](<>)\r\n"),
            ("list-empty-destination", "- before [x]() [y](<>)\n"),
            ("bom-frontmatter-empty-destination", "\uFEFF---\ntitle: X\n---\nbefore [x]() [y](<>)\n"),
        };
        var failures = new List<string>();
        foreach (var (id, input) in cases)
        {
            var doc = MarkdownParser.Parse(input, "mapping", id);
            var portableDoc = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Parse")!
                .Invoke(null, new object?[] { input, "mapping", id, null, null, CancellationToken.None })!;
            Check(JsonSerializer.Serialize(doc) == JsonSerializer.Serialize(portableDoc, portableDoc.GetType()),
                id + ": runtime/portable full public facts");
            var expectedAt = 0;
            foreach (var link in doc.Links.Where(link => link.Kind == MarkdownLinkKind.Inline))
            {
                var token = link.Target == "/docs" ? "[Guide](/docs)" : link.Label.Text == "x" ? "[x]()" : "[y](<>)";
                var expected = input.IndexOf(token, expectedAt, StringComparison.Ordinal);
                Check(expected >= 0, id + ": expected link token");
                expectedAt = expected + token.Length;
                var targetAt = expected + (token == "[y](<>)" ? 5 : token == "[x]()" ? 4 : 8);
                if (link.RawSpan != new RawSpan(expected, token.Length)
                    || link.TargetRawSpan != new RawSpan(targetAt, link.Target!.Length))
                {
                    failures.Add(id + ": " + token + " raw=" + link.RawSpan + " target=" + link.TargetRawSpan
                        + " expected=" + expected + "/" + targetAt);
                }
                else
                {
                    Check(Slice(input, link.RawSpan) == token && Slice(input, link.TargetRawSpan!.Value) == link.Target,
                        id + ": link/destination raw slices");
                }
            }
            var expectedLinks = input.Contains("[Guide]", StringComparison.Ordinal)
                ? input.Split("[Guide]", StringSplitOptions.None).Length - 1 : 2;
            Check(doc.Status == MarkdownParseStatus.Complete && doc.Links.Count == expectedLinks, id + ": expected complete links");
        }
        if (failures.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        const string transformed = "| H |\n| - |\n| a\\|b `c\\|d` \\| [Guide](/docs) |\n";
        var table = MarkdownParser.Parse(transformed, "mapping", "transformed-table");
        var text = table.TextRegions.Last(region => region.Kind == "tableCell").Content;
        Check(text.Text == "a|b c|d | Guide", "Table predecoding grammar remains unchanged");
        var escapedPipe = text.Map(new RawSpan(1, 1));
        Check(escapedPipe.Precision == MarkdownMappingPrecision.CoveringTokens
            && Slice(transformed, escapedPipe.RawFragments.Single()) == "\\|", "Escaped table pipe maps to both raw units");
        var codeAt = text.Text!.IndexOf("c|d", StringComparison.Ordinal);
        var code = text.Map(new RawSpan(codeAt + 1, 1));
        Check(code.Precision == MarkdownMappingPrecision.CoveringTokens
            && Slice(transformed, code.RawFragments.Single()) == "c\\|d", "Table code token maps before predecoding");
        Check(text.Map(new RawSpan(codeAt + 1, 0)).Precision == MarkdownMappingPrecision.Unknown,
            "Code-normalization interior is not an exact empty position");
        Check(text.Map(new RawSpan(1, 0)).RawFragments.Single() == new RawSpan(transformed.IndexOf("\\|", StringComparison.Ordinal), 0),
            "Escaped table pipe opening boundary");
        Check(text.Map(new RawSpan(2, 0)).RawFragments.Single() == new RawSpan(transformed.IndexOf("\\|", StringComparison.Ordinal) + 2, 0),
            "Escaped table pipe closing boundary");
        const string transformedLink = "| H |\n| - |\n| [a\\|b](/a\\|b) |\n";
        var linkWithPipe = MarkdownParser.Parse(transformedLink, "mapping", "transformed-link").Links.Single();
        Check(Slice(transformedLink, linkWithPipe.RawSpan) == "[a\\|b](/a\\|b)"
            && Slice(transformedLink, linkWithPipe.TargetRawSpan!.Value) == "/a\\|b"
            && linkWithPipe.Target == "/a|b", "Escapes inside link label/destination preserve complete raw tokens");
        Check(linkWithPipe.Label.Map(new RawSpan(1, 1)).Precision == MarkdownMappingPrecision.CoveringTokens
            && Slice(transformedLink, linkWithPipe.Label.Map(new RawSpan(1, 1)).RawFragments.Single()) == "\\|",
            "Escaped link label projection uses table source positions");
        foreach (var raw in new[] { "\\|", "\\\\|", "`\\|`" })
        {
            var source = "| H |\n| - |\n| " + raw + " |\n";
            var content = MarkdownParser.Parse(source, "mapping", "terminal-pipe").TextRegions.Last().Content;
            var projected = content.Map(new RawSpan(0, content.Text!.Length));
            Check(content.Text == "|" && projected.Precision == MarkdownMappingPrecision.CoveringTokens
                && Slice(source, projected.RawFragments.Single()) == raw.Trim('`'), "Terminal table token/raw width: " + raw);
        }
        // tableCell.RawSpan continues to identify the whole table, not an individual cell.
        Check(table.TextRegions.Where(region => region.Kind == "tableCell").Select(region => region.RawSpan).Distinct().Count() == 1,
            "Existing tableCell range contract");
        var nul = MarkdownParser.Parse("A\0B", "mapping", "nul").TextRegions.Single().Content;
        Check(nul.Text == "A\uFFFDB" && nul.Map(new RawSpan(1, 1)).Precision == MarkdownMappingPrecision.CoveringTokens
            && nul.Map(new RawSpan(1, 1)).RawFragments.Single() == new RawSpan(0, 3), "NUL replacement retains a covering raw token");
        Console.WriteLine(JsonSerializer.Serialize(new { task = "SM-02", status = "PASS", cases = cases.Length, checks = "raw slices/empty destinations/table transformations/atomic boundaries" }));
    }

    private static string Slice(string input, RawSpan span) => input.Substring(span.Start, span.Length);
    private static void Check(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
