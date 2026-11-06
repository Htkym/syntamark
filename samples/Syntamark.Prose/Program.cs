using System.Text.Json;
using Syntamark;
using Syntamark.Prose.Sample;

// Independent synthetic fixture: no Yomiyasu input or personal document is copied here.
var raw = string.Join("\r\n", new[]
{
    "# 見出し &amp; 絵😀", "", "段落の\\*印と[案内](/docs)。", "",
    "> 引用の文。", "> 次の行。", "", "- 項目の文。", "",
    "前 `機密` 後 ![画像の代替](img.png) 続 <https://example.test> 終。", "",
    "文<span>中</span>末。", "", "```txt", "隠すコード", "```", "",
    "    隠す字下げ", "", "<div>", "隠すHTML", "</div>", "", "[参照][r]。", "", "[r]: /target", "",
});
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var syntax = MarkdownParser.ParseWithSyntax(raw, "prose-sample", "japanese.md", options: options);
var selection = ProsePolicy.Select(syntax);
string[] expected = ["見出し & 絵😀", "段落の*印と案内。", "引用の文。\n次の行。", "項目の文。",
    "前 ", " 後 ", " 続 ", " 終。", "文", "中", "末。", "参照。"];
Check(selection.HasSyntax && selection.Segments.Select(segment => segment.Text).SequenceEqual(expected),
    "Expected prose includes labels, headings, quote/list text and preserves gaps around excluded content.");
Check(selection.Exclusions.Count(item => item.Reason == "code-block") == 2
    && selection.Exclusions.Count(item => item.Reason == "inline-code") == 1
    && selection.Exclusions.Count(item => item.Reason == "image-alt") == 1
    && selection.Exclusions.Count(item => item.Reason == "autolink") == 1
    && selection.Exclusions.Count(item => item.Reason == "html-block") == 1
    && selection.Exclusions.Count(item => item.Reason == "html-token") == 2
    && selection.Exclusions.Count(item => item.Reason == "reference-definition") == 1,
    "Excluded ranges report their individual caller-policy reasons.");
Check(syntax.Facts.Status == MarkdownParseStatus.Complete
    && syntax.Facts.Coverage.DecodedMapping == MarkdownCoverageState.Partial,
    "Selection retains the parser's separate status and HTML projection coverage.");

var heading = selection.Segments[0];
CheckMap(heading, new RawSpan(4, 1), new RawSpan(6, 5), "&amp;", 1, 7, MarkdownMappingPrecision.CoveringTokens);
CheckMap(heading, new RawSpan(7, 2), new RawSpan(13, 2), "😀", 1, 14, MarkdownMappingPrecision.Exact);
CheckMap(selection.Segments[1], new RawSpan(3, 1), new RawSpan(22, 2), "\\*", 3, 4, MarkdownMappingPrecision.CoveringTokens);
var quote = selection.Segments.Single(segment => segment.Text.StartsWith("引用", StringComparison.Ordinal));
CheckMap(quote, new RawSpan(6, 1), new RawSpan(raw.IndexOf("次の行。", StringComparison.Ordinal), 1),
    "次", 6, 3, MarkdownMappingPrecision.Exact);
var crossLine = quote.Map(new RawSpan(3, 5));
Check(crossLine.RawFragments.Count == 2 && string.Concat(crossLine.RawFragments.Select(Slice)) == "文。\r\n次の"
    && crossLine.Precision == MarkdownMappingPrecision.CoveringTokens, "A quote-prefix gap stays multiple diagnostic fragments.");
var afterCode = selection.Segments.Single(segment => segment.Text == " 後 ");
CheckMap(afterCode, new RawSpan(1, 1), new RawSpan(raw.IndexOf("後", StringComparison.Ordinal), 1),
    "後", 10, 8, MarkdownMappingPrecision.Exact);
Throws<ArgumentOutOfRangeException>(() => afterCode.Map(new RawSpan(afterCode.Text.Length, 1)));

var ordinary = MarkdownParser.Parse(raw, "prose-sample", "japanese.md", options: options);
Check(JsonSerializer.Serialize(syntax.Facts) == JsonSerializer.Serialize(ordinary), "The helper does not change legacy TextRegions or facts.");
const string frontMatter = "---\r\ntitle: hidden\r\n---\r\n# 本文\r\n";
var legacyFrontMatter = ProsePolicy.Select(MarkdownParser.ParseWithSyntax(frontMatter, "prose-sample", "frontmatter.md"));
Check(legacyFrontMatter.Segments.Single().Text == "本文"
    && legacyFrontMatter.Exclusions.Single().Reason == "frontmatter", "Parsed legacy front matter is excluded explicitly.");
var standardFrontMatter = ProsePolicy.Select(MarkdownParser.ParseWithSyntax(frontMatter, "prose-sample", "literal-yaml.md", options: options));
Check(standardFrontMatter.Facts.FrontMatter.State == MarkdownFrontMatterState.Absent
    && standardFrontMatter.Exclusions.All(item => item.Reason != "frontmatter")
    && standardFrontMatter.Segments.Any(segment => segment.Text.Contains("title: hidden", StringComparison.Ordinal)),
    "CommonMark treats YAML-looking text as ordinary Markdown; this sample adds no second front-matter parser.");

const string table = "| 見出し |\r\n| --- |\r\n| 本文 |\r\n";
var legacyTable = ProsePolicy.Select(MarkdownParser.ParseWithSyntax(table, "prose-sample", "table.md"));
Check(legacyTable.Segments.Select(segment => segment.Text).SequenceEqual(new[] { "見出し", "本文" })
    && legacyTable.Segments.All(segment => segment.Kind == "tableCell"), "Legacy GFM table cells remain independently inspectable prose.");
var standardTable = ProsePolicy.Select(MarkdownParser.ParseWithSyntax(table, "prose-sample", "literal-table.md", options: options));
Check(standardTable.Segments.Single().Kind == "paragraph" && standardTable.Segments.Single().Text == "| 見出し |\n| --- |\n| 本文 |",
    "CommonMark table-looking text remains a literal paragraph.");
var gaps = ProsePolicy.Select(MarkdownParser.ParseWithSyntax("A`x`B![](/image)C", "prose-sample", "gaps.md", options: options));
Check(gaps.Segments.Select(segment => segment.Text).SequenceEqual(new[] { "A", "B", "C" }), "No fake ABC sentence is joined across excluded or empty-alt gaps.");
var bareUrl = ProsePolicy.Select(MarkdownParser.ParseWithSyntax("裸URL https://example.test を残す。", "prose-sample", "bare-url.md", options: options));
Check(bareUrl.Segments.Single().Text.Contains("https://example.test", StringComparison.Ordinal), "Bare URL detection remains an external editorial policy.");
var rejected = ProsePolicy.Select(MarkdownParser.ParseWithSyntax("long", "prose-sample", "limit.md", options: new MarkdownParseOptions(maxInputUtf16: 1)));
Check(!rejected.HasSyntax && rejected.Facts.Status == MarkdownParseStatus.Failed
    && rejected.Exclusions.Single().Reason == "syntax-unavailable", "A missing tree is not a successful empty prose document.");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
Throws<OperationCanceledException>(() => ProsePolicy.Select(syntax, canceled.Token));

var coordinates = new YomiCoordinates("\uFEFFA😀\r\nB\rC\n");
Check(coordinates.Text == "A😀\nB\nC\n"
    && coordinates.ToRaw(new CodePointRange(1, 1)) == new RawSpan(2, 2)
    && coordinates.ToRaw(new CodePointRange(2, 1)) == new RawSpan(4, 2)
    && coordinates.ToPython(new RawSpan(2, 2)) == new CodePointRange(1, 1)
    && coordinates.ToPython(new RawSpan(6, 1)) == new CodePointRange(3, 1), "BOM/EOL normalization and Python code-point positions are separate from raw UTF-16.");
Throws<ArgumentException>(() => coordinates.ToPython(new RawSpan(3, 1))); // Inside the surrogate pair.
Throws<ArgumentException>(() => coordinates.ToPython(new RawSpan(5, 1))); // Inside CRLF.
Throws<ArgumentException>(() => coordinates.ToPython(new RawSpan(0, 1))); // Removed BOM.
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS",
    profile = syntax.Facts.ProfileId,
    parserVersion = syntax.Facts.ParserVersion,
    prose = selection.Segments.Select(segment => segment.Text),
    exclusions = selection.Exclusions.Select(item => new { item.Reason, item.RawSpan }),
    checks = "Japanese/CRLF/entity/escape/emoji/quote fragments/code/image/HTML/reference/frontmatter/table/gaps/cancel/Python-coordinate boundaries",
    actualYomiyasuAdoption = false,
}));

string Slice(RawSpan span) => raw.Substring(span.Start, span.Length);
void CheckMap(ProseSegment segment, RawSpan local, RawSpan expectedRaw, string expectedSlice, int line, int column, MarkdownMappingPrecision precision)
{
    var result = segment.Map(local);
    Check(result.RawFragments.SequenceEqual(new[] { expectedRaw }) && result.Precision == precision
        && Slice(expectedRaw) == expectedSlice && syntax.Facts.GetLinePosition(expectedRaw.Start) == new MarkdownLinePosition(line, column),
        "Independent raw diagnostic expectation: " + expectedSlice);
}
static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
