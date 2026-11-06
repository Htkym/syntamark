using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class OwnedSliceChecks
{
    internal static void Run(Assembly portable)
    {
        var joined = new (string Id, string Input, string Expected)[]
        {
            ("single", "b \t", "b"),
            ("multi", "a\nb  \t", "a\nb"),
            ("crlf", "a\r\nb \t\r\n", "a\nb"),
            ("quote", "> a\r\n> b \t\r\n", "a\nb"),
            ("list", "- a\n  b \t\n", "a\nb"),
            ("nbsp", "a\nb \u00A0", "a\nb \u00A0"),
        };
        var codeSpans = new (string Id, string Input, string Token, string RawContent, string Expected)[]
        {
            ("plain", "`alpha`", "`alpha`", "alpha", "alpha"),
            ("trim", "` alpha `", "` alpha `", " alpha ", "alpha"),
            ("spaces", "`   `", "`   `", "   ", "   "),
            ("tabs", "` \t `", "` \t `", " \t ", " \t "),
            ("nbsp", "` \u00A0 `", "` \u00A0 `", " \u00A0 ", " \u00A0 "),
            ("unicode-content", "` \u00A0x\u00A0 `", "` \u00A0x\u00A0 `", " \u00A0x\u00A0 ", "\u00A0x\u00A0"),
            ("emoji", "` 😀 `", "` 😀 `", " 😀 ", "😀"),
            ("nul", "` a\0b `", "` a\0b `", " a\0b ", "a\uFFFDb"),
            ("crlf", "` a\r\nb `", "` a\r\nb `", " a\r\nb ", "a b"),
            ("cr", "` a\rb `", "` a\rb `", " a\rb ", "a b"),
            ("lf", "` a\nb `", "` a\nb `", " a\nb ", "a b"),
            ("backticks", "`` `alpha` ``", "`` `alpha` ``", " `alpha` ", "`alpha`"),
            ("quote", "> ` alpha `\r\n", "` alpha `", " alpha ", "alpha"),
        };
        foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
        {
            var options = new MarkdownParseOptions(profileId: profile);
            foreach (var item in joined)
            {
                var syntax = MarkdownParser.ParseWithSyntax(item.Input, "slices", "join-" + item.Id, options: options);
                var projection = syntax.Facts.TextRegions.Single(region => region.Kind == "paragraph").Content;
                Check(syntax.Facts.Status == MarkdownParseStatus.Complete && projection.Text == item.Expected,
                    profile + ": joined text " + item.Id);
                var decoded = projection.Text!.IndexOf('b');
                var raw = projection.Map(new RawSpan(decoded, 1));
                Check(raw.Precision == MarkdownMappingPrecision.Exact
                    && raw.RawFragments.Single() == new RawSpan(item.Input.IndexOf('b'), 1),
                    profile + ": final-line raw position " + item.Id);
                Check(JsonSerializer.Serialize(syntax.Facts) == JsonSerializer.Serialize(
                    MarkdownParser.Parse(item.Input, "slices", "join-" + item.Id, options: options)),
                    profile + ": joined facts/syntax entry " + item.Id);
                PortableMatches(portable, profile, item.Input, "join-" + item.Id, syntax);
            }
            foreach (var item in codeSpans)
            {
                var syntax = MarkdownParser.ParseWithSyntax(item.Input, "slices", "code-" + item.Id, options: options);
                var node = syntax.Root!.DescendantsAndSelf().Single(value => value.Kind == MarkdownSyntaxKind.CodeSpan);
                var projection = syntax.Facts.TextRegions.Single(region => region.Kind == "paragraph").Content;
                Check(syntax.Facts.Status == MarkdownParseStatus.Complete
                    && node.Text == item.Expected && node.GetRawText() == item.Token && projection.Text == item.Expected,
                    profile + ": code text/token " + item.Id);
                var mapped = projection.Map(new RawSpan(0, 1));
                var fragment = mapped.RawFragments.Single();
                Check(mapped.Precision == MarkdownMappingPrecision.CoveringTokens
                    && item.Input.Substring(fragment.Start, fragment.Length) == item.RawContent,
                    profile + ": code atomic source content " + item.Id);
                Check(JsonSerializer.Serialize(syntax.Facts) == JsonSerializer.Serialize(
                    MarkdownParser.Parse(item.Input, "slices", "code-" + item.Id, options: options)),
                    profile + ": code facts/syntax entry " + item.Id);
                PortableMatches(portable, profile, item.Input, "code-" + item.Id, syntax);
            }
        }
        Console.WriteLine("PASS owned slices: joined ranges, code whitespace and original atomic envelopes.");
    }

    private static void PortableMatches(Assembly portable, string profile, string input, string id, MarkdownSyntaxDocument runtime)
    {
        var options = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
            [1048576, 131072, 200, 16777216, profile, 1]);
        var counterpart = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("ParseWithSyntax")!
            .Invoke(null, [input, "slices", id, null, options, CancellationToken.None])!;
        Check(JsonSerializer.Serialize(runtime) == JsonSerializer.Serialize(counterpart, counterpart.GetType()),
            profile + ": runtime/source " + id);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
