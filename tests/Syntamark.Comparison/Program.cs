using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Syntamark;

var pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
Check(ConvertMarkdig(SourceSpan.Empty) is null && ConvertMarkdig(new SourceSpan(8, 7)) == new RangeFact(8, 0)
    && ConvertMarkdig(new SourceSpan(8, 8)) == new RangeFact(8, 1), "Inclusive range and empty-sentinel conversion.");
var fixtures = new[]
{
    new Fixture("plain", "# Heading\n\n[Guide](/docs \"Title\")\n",
        [new(1, "Heading", "# Heading")], [new("Guide", "/docs", "Title", "[Guide](/docs \"Title\")", "/docs")]),
    new Fixture("decoded-utf16-crlf", "# A&amp;\\*😀\r\n\r\n> [G&amp;\\*😀](/docs?a=1&amp;b=2)\r\n",
        [new(1, "A&*😀", "# A&amp;\\*😀")], [new("G&*😀", "/docs?a=1&b=2", null, "[G&amp;\\*😀](/docs?a=1&amp;b=2)", "/docs?a=1&amp;b=2")]),
    new Fixture("nested-inline", "# *A* `b  c`\n\n- [**bold**](/nested)\n",
        [new(1, "A b  c", "# *A* `b  c`")], [new("bold", "/nested", null, "[**bold**](/nested)", "/nested")]),
    new Fixture("reference", "[Guide][r]\n\n[r]: /target \"Reference\"\n",
        [], [new("Guide", "/target", "Reference", "[Guide][r]", "/target")]),
    new Fixture("image", "![A&amp;B](/image.png)\n", [],
        [new("A&B", "/image.png", null, "![A&amp;B](/image.png)", "/image.png", true)]),
};

var results = new List<object>();
var errors = new List<string>();
foreach (var fixture in fixtures)
{
    var syntamark = Syntamark.MarkdownParser.Parse(fixture.Input, "comparison", fixture.Id);
    Check(syntamark.Status == MarkdownParseStatus.Complete, fixture.Id + ": Syntamark did not finish completely.");
    var markdig = Markdig.Markdown.Parse(fixture.Input, pipeline);
    var left = FromSyntamark(syntamark);
    var right = FromMarkdig(markdig);
    var expected = new CommonFacts(
        fixture.Headings.Select(h => new HeadingFact(h.Level, h.Text, Find(fixture.Input, h.Raw))).ToArray(),
        fixture.Links.Select(l => new LinkFact(l.Image, l.Label, l.Target, l.Title, Find(fixture.Input, l.Raw),
            Find(fixture.Input, l.TargetRaw))).ToArray());
    var expectedJson = JsonSerializer.Serialize(expected);
    foreach (var (engine, actual) in new[] { ("Syntamark", left), ("Markdig", right) })
        if (JsonSerializer.Serialize(actual) != expectedJson)
            errors.Add(fixture.Id + "/" + engine + ": output differs from the independently selected raw tokens and decoded values.");
    results.Add(new
    {
        fixture.Id,
        rawUtf16Length = fixture.Input.Length,
        inputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Input))).ToLowerInvariant(),
        syntamark.ParserVersion,
        syntamark.OptionsHash,
        expected,
        syntamark = left,
        markdig = right,
        equal = JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right)
    });
}

// A zero-length token and the sentinel (0,-1) are distinct observations. Do not invent a location.
const string emptyInput = "lead 😀\r\n\r\n[x]() [y](<>)\r\n";
var emptySyntamark = Syntamark.MarkdownParser.Parse(emptyInput, "comparison", "empty-destination");
var emptyMarkdig = Markdig.Markdown.Parse(emptyInput, pipeline).Descendants().OfType<LinkInline>().ToArray();
Check(emptySyntamark.Links.Count == 2 && emptyMarkdig.Length == 2, "Both empty destinations must be recognized.");
var emptyDestinations = emptySyntamark.Links.Zip(emptyMarkdig).Select((pair, index) =>
{
    var expected = new RangeFact(index == 0 ? emptyInput.IndexOf("()", StringComparison.Ordinal) + 1
        : emptyInput.IndexOf("<>", StringComparison.Ordinal) + 1, 0);
    var syntamark = ConvertSyntamark(pair.First.TargetRawSpan);
    var markdig = ConvertMarkdig(pair.Second.UrlSpan);
    Check(pair.First.Target == "" && pair.Second.Url == "", "Empty destination values must remain empty.");
    return new
    {
        index,
        expected,
        syntamark,
        markdig,
        syntamarkExact = syntamark == expected,
        markdigExact = markdig == expected,
        markdigNativeStart = pair.Second.UrlSpan.Start,
        markdigNativeEnd = pair.Second.UrlSpan.End
    };
}).ToArray();

var mappingDoc = Syntamark.MarkdownParser.Parse("# A&amp;\\*😀\r\n", "comparison", "mapping");
var projection = mappingDoc.Headings.Single().Text;
var entityMap = projection.Map(new RawSpan(1, 1));
Check(entityMap.Precision == MarkdownMappingPrecision.CoveringTokens
    && entityMap.RawFragments.Single() == new RawSpan(3, 5), "Independent entity mapping oracle.");
var entityNode = Markdig.Markdown.Parse("# A&amp;\\*😀\r\n", pipeline).Descendants().OfType<HtmlEntityInline>().Single();
Check(entityNode.Original.ToString() == "&amp;" && entityNode.Transcoded.ToString() == "&",
    "Markdig retains original and transcoded entity text.");

var output = new
{
    contract = "headings-and-resolved-links/1",
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    syntamarkProfile = "lithosharp-markdown/1",
    markdigProfile = "CommonMark core; precise source locations; no extensions",
    markdigPackageVersion = "1.5.1",
    markdigAssembly = typeof(Markdig.Markdown).Assembly.FullName,
    markdigInformationalVersion = typeof(Markdig.Markdown).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    commonCases = results,
    emptyDestinationProbe = new
    {
        input = emptyInput,
        cases = emptyDestinations,
        classification = "Position probe, excluded from the equal-output subset; exact/missing observations are reported independently."
    },
    decodedMappingProbe = new
    {
        syntamarkPrecision = entityMap.Precision.ToString(),
        syntamarkFragments = entityMap.RawFragments,
        markdigOriginal = entityNode.Original.ToString(),
        markdigTranscoded = entityNode.Transcoded.ToString(),
        markdigArbitrarySubstringAdapter = "not implemented; this is not a zero-cost equivalent mapping operation"
    },
    errors,
    status = errors.Count == 0 ? "PASS" : "FAIL",
};
var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 2 && args[0] == "--output")
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    File.WriteAllText(args[1], json + Environment.NewLine, new UTF8Encoding(false));
}
else if (args.Length != 0)
    throw new ArgumentException("Usage: Syntamark.Comparison [--output <path>]");
Console.WriteLine(json);
return errors.Count == 0 ? 0 : 1;

static CommonFacts FromSyntamark(Syntamark.MarkdownDocument document) => new(
    document.Headings.Select(h => new HeadingFact(h.RawLevel, h.Text.Text!, ConvertSyntamark(h.RawSpan)!)).ToArray(),
    document.Links.Where(l => l.Kind != MarkdownLinkKind.ReferenceDefinition && l.Resolution != MarkdownLinkResolution.UnresolvedReference)
        .Select(l => new LinkFact(l.Image, l.Label.Text!, l.Target!, l.Title, ConvertSyntamark(l.RawSpan)!, ConvertSyntamark(l.TargetRawSpan))).ToArray());

static CommonFacts FromMarkdig(Markdig.Syntax.MarkdownDocument document) => new(
    document.Descendants().OfType<HeadingBlock>().Select(h => new HeadingFact(h.Level, Text(h.Inline!), ConvertMarkdig(h.Span)!)).ToArray(),
    document.Descendants().OfType<LinkInline>().Select(l => new LinkFact(l.IsImage, Text(l), l.Url!, l.Title,
        ConvertMarkdig(l.Span)!, ConvertMarkdig(l.UrlSpan))).ToArray());

static string Text(ContainerInline container)
{
    var text = new StringBuilder();
    foreach (var child in container)
        switch (child)
        {
            case LiteralInline literal:
                text.Append(literal.Content);
                break;
            case HtmlEntityInline entity:
                text.Append(entity.Transcoded);
                break;
            case CodeInline code:
                text.Append(code.Content);
                break;
            case LineBreakInline:
                text.Append('\n');
                break;
            case ContainerInline nested:
                text.Append(Text(nested));
                break;
            default:
                throw new NotSupportedException("Comparison text adapter does not observe " + child.GetType().Name);
        }
    return text.ToString();
}

static RangeFact? ConvertMarkdig(SourceSpan span)
{
    if (span == SourceSpan.Empty)
        return null;
    if (span.Start < 0 || span.End < span.Start - 1)
        throw new InvalidOperationException("Invalid Markdig range: " + span);
    return new(span.Start, checked(span.End - span.Start + 1));
}
static RangeFact? ConvertSyntamark(RawSpan? span) => span.HasValue ? new(span.Value.Start, span.Value.Length) : null;
static RangeFact Find(string input, string raw)
{
    var start = input.IndexOf(raw, StringComparison.Ordinal);
    Check(start >= 0 && input.IndexOf(raw, start + 1, StringComparison.Ordinal) < 0, "Oracle token must occur once: " + raw);
    return new(start, raw.Length);
}
static void Check(bool valid, string message)
{
    if (!valid)
        throw new InvalidOperationException(message);
}

internal sealed record RangeFact(int Start, int Length);
internal sealed record HeadingFact(int Level, string Text, RangeFact RawSpan);
internal sealed record LinkFact(bool Image, string Label, string Target, string? Title, RangeFact RawSpan, RangeFact? TargetRawSpan);
internal sealed record CommonFacts(HeadingFact[] Headings, LinkFact[] Links);
internal sealed record ExpectedHeading(int Level, string Text, string Raw);
internal sealed record ExpectedLink(string Label, string Target, string? Title, string Raw, string TargetRaw, bool Image = false);
internal sealed record Fixture(string Id, string Input, ExpectedHeading[] Headings, ExpectedLink[] Links);
