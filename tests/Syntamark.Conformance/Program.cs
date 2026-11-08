using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Syntamark;

const string fixtureHash = "d431b29d97b6f73e69d547109cf5081578fac931e72afe95639ebe766c1b2a20";
var output = Path.GetFullPath(".local/commonmark");
int? selected = null;
var strict = false;
var includeSyntax = false;
var checkSessions = false;
string? revision = null;
string? assemblyPath = null;
var profile = "lithosharp-markdown/1";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--example" when i + 1 < args.Length && int.TryParse(args[++i], out var number):
            selected = number;
            break;
        case "--output" when i + 1 < args.Length:
            output = Path.GetFullPath(args[++i]);
            break;
        case "--strict":
            strict = true;
            break;
        case "--sessions":
            checkSessions = true;
            includeSyntax = true;
            break;
        case "--syntax":
            includeSyntax = true;
            break;
        case "--revision" when i + 1 < args.Length:
            revision = args[++i];
            break;
        case "--assembly" when i + 1 < args.Length:
            assemblyPath = Path.GetFullPath(args[++i]);
            break;
        case "--profile" when i + 1 < args.Length:
            profile = args[++i];
            break;
        default:
            throw new ArgumentException("Usage: [--example NUMBER] [--output DIRECTORY] [--revision LABEL] [--assembly DLL] [--profile ID] [--syntax] [--sessions] [--strict]");
    }
}

SyntaxHtml.CheckAdapter();
var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spec.json"));
if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != fixtureHash)
    throw new InvalidOperationException("Pinned CommonMark fixture checksum changed.");
var examples = JsonSerializer.Deserialize<Example[]>(bytes)!;
if (examples.Length != 652 || !examples.Select(e => e.Number).SequenceEqual(Enumerable.Range(1, 652)))
    throw new InvalidOperationException("CommonMark denominator or example identities changed.");
if (selected.HasValue && !examples.Any(e => e.Number == selected))
    throw new ArgumentOutOfRangeException(nameof(selected), "Example is not in the pinned specification.");

var loadContext = new AssemblyLoadContext("CommonMark selected revision", isCollectible: true);
loadContext.Resolving += (_, name) => AssemblyLoadContext.Default.LoadFromAssemblyName(name);
var assembly = assemblyPath is null ? typeof(MarkdownParser).Assembly : loadContext.LoadFromAssemblyPath(assemblyPath);
var optionsType = assembly.GetType("Syntamark.Compilation.MdOptions", true)!;
var contextType = assembly.GetType("Syntamark.Compilation.MdParseContext", true)!;
var parse = assembly.GetType("Syntamark.Compilation.LithoBlockParser", true)!
    .GetMethod("ParseFactsBlocks", BindingFlags.Static | BindingFlags.NonPublic)!;
var coreOptions = Activator.CreateInstance(optionsType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    null, new object[] { 1048576, 1048576, 200, 134217728, profile, 1 }, null)!;
var publicOptions = Activator.CreateInstance(assembly.GetType("Syntamark.MarkdownParseOptions", true)!,
    new object[] { 1048576, 1048576, 200, 134217728, profile, 1 })!;
var publicParse = assembly.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public)!;
var publicSyntaxParse = assembly.GetType("Syntamark.MarkdownParser", true)!.GetMethod("ParseWithSyntax", BindingFlags.Static | BindingFlags.Public);
object ParseFacts(string raw, string id) => publicParse.Invoke(null, new[] { raw, "commonmark-inventory", id, null, publicOptions, CancellationToken.None })!;
void CheckSession(string raw, string id)
{
    var create = assembly.GetType("Syntamark.MarkdownParser", true)!.GetMethod("CreateSession")!;
    var session = create.Invoke(null, new[] { raw, "commonmark-inventory", id, null, publicOptions, CancellationToken.None })!;
    var facts = session.GetType().GetProperty("Document")!.GetValue(session)!;
    var editType = assembly.GetType("Syntamark.MarkdownTextEdit", true)!;
    var position = Array.FindIndex(raw.ToCharArray(), c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
    var edits = Array.CreateInstance(editType, position >= 0 ? 1 : 0);
    var updated = raw;
    if (position >= 0)
    {
        var replacement = raw[position] == 'Z' ? "A" : "Z";
        var span = Activator.CreateInstance(assembly.GetType("Syntamark.RawSpan", true)!, new object[] { position, 1 });
        edits.SetValue(Activator.CreateInstance(editType, new[] { span, replacement }), 0);
        updated = raw.Remove(position, 1).Insert(position, replacement);
    }
    var next = session.GetType().GetMethod("ApplyEdits")!.Invoke(session,
        new[] { facts.GetType().GetProperty("TextHash")!.GetValue(facts), edits, "session-conformance", null, CancellationToken.None })!;
    var oracle = publicSyntaxParse!.Invoke(null,
        new[] { updated, "commonmark-inventory", id, "session-conformance", publicOptions, CancellationToken.None })!;
    string Json(object? value) => value is null ? "null" : JsonSerializer.Serialize(value, value.GetType());
    foreach (var pair in new[] { ("Document", "Facts"), ("Root", "Root") })
        if (Json(next.GetType().GetProperty(pair.Item1)!.GetValue(next)) != Json(oracle.GetType().GetProperty(pair.Item2)!.GetValue(oracle)))
            throw new InvalidOperationException("Parser session differs from full oracle: " + pair.Item1);
}
var results = new List<Result>();
foreach (var example in examples)
{
    if (selected.HasValue && example.Number != selected)
    {
        results.Add(new(example.Number, example.Section, "not-run", "Not selected by --example.", "unverified", null, null, null, null));
        continue;
    }
    string? actual = null;
    string? factsStatus = null;
    string? factsError = null;
    try
    {
        var facts = ParseFacts(example.Markdown, example.Number.ToString());
        factsStatus = SyntaxHtml.Get<object>(facts, "Status").ToString();
        try
        {
            CheckFacts(facts, example.Markdown);
            if (checkSessions)
                CheckSession(example.Markdown, example.Number.ToString());
        }
        catch (InvalidOperationException error)
        {
            factsError = error.Message;
        }
        var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { coreOptions, (object)CancellationToken.None }, null)!;
        var parsed = parse.Invoke(null, new[] { example.Markdown, context })!;
        var blocks = (IEnumerable)parsed.GetType().GetField("Item1")!.GetValue(parsed)!;
        actual = SyntaxHtml.Render(blocks, decodedInfo: profile == "commonmark/0.31.2");
        var reason = SyntaxHtml.Get<IEnumerable>(facts, "Diagnostics").Cast<object>().Any(d => SyntaxHtml.Get<string>(d, "Origin") == "resource") ? "resource-limit"
            : SyntaxHtml.Get<object>(SyntaxHtml.Get<object>(facts, "FrontMatter"), "State").ToString() != "Absent" ? "profile-front-matter"
            : actual == example.Html ? "html-match" : "html-mismatch";
        results.Add(new(example.Number, example.Section, reason == "html-match" ? "passed" : "failed", reason,
            factsError is null ? "passed-invariants" : "failed-invariants", factsStatus, factsError, actual, example.Html));
    }
    catch (Exception error) when (error is NotSupportedException || error.InnerException is NotSupportedException)
    {
        results.Add(new(example.Number, example.Section, "not-observable", (error.InnerException ?? error).Message,
            factsError is null ? "passed-invariants" : "failed-invariants", factsStatus, factsError, actual, example.Html));
    }
    catch (Exception error)
    {
        results.Add(new(example.Number, example.Section, "failed", "execution-error: " + (error.InnerException ?? error).Message,
            factsError is null ? "unverified" : "failed-invariants", factsStatus, factsError, actual, example.Html));
    }
    if (includeSyntax)
    {
        try
        {
            if (publicSyntaxParse is null)
                results[^1] = results[^1] with { Syntax = "not-observable", SyntaxError = "This revision has no public syntax entry." };
            else
            {
                var syntaxDocument = publicSyntaxParse.Invoke(null, new[] { example.Markdown, "commonmark-inventory", example.Number.ToString(), null, publicOptions, CancellationToken.None })!;
                var root = SyntaxHtml.Get<object?>(syntaxDocument, "Root");
                if (root is null)
                    throw new InvalidOperationException("Syntax root was not obtained with the explicit specification budget.");
                var unknown = CheckSyntax(root, example.Markdown.Length);
                results[^1] = results[^1] with { Syntax = "passed-invariants", SyntaxUnknownRanges = unknown };
            }
        }
        catch (Exception error)
        {
            results[^1] = results[^1] with { Syntax = "failed-invariants", SyntaxError = (error.InnerException ?? error).Message };
        }
    }
}

var totals = Counts(results);
var report = new
{
    specification = "CommonMark 0.31.2",
    fixtureSha256 = fixtureHash,
    profileId = profile,
    sourceRevision = revision,
    parserVersion = SyntaxHtml.Get<string>(ParseFacts("", "metadata"), "ParserVersion"),
    assemblyVersion = assembly.GetName().Version?.ToString(),
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    options = new { maxInputUtf16 = 1048576, maxOutputItems = 1048576, maxNestingDepth = 200, maxScanUnits = 134217728 },
    syntaxEntry = "LithoBlockParser.ParseFactsBlocks(raw) + test-only SyntaxHtml; full facade preprocessing and resource status checked separately",
    factsScope = "Raw range bounds, ordered decoded coverage, Linear raw/decoded equality; independent golden facts remain in Portability",
    selectedExample = selected,
    syntaxRequested = includeSyntax,
    syntaxInvariantFailures = results.Count(r => r.Syntax == "failed-invariants"),
    syntaxUnknownRanges = results.Sum(r => r.SyntaxUnknownRanges),
    totals,
    chapters = results.GroupBy(r => r.Section).Select(g => new { section = g.Key, counts = Counts(g), remaining = g.Where(r => r.Status != "passed").Select(r => r.Example) }),
    results,
};
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
var markdown = new StringBuilder("# CommonMark 0.31.2 inventory\n\nProfile: `" + profile + "`. This is an example inventory, not a complete conformance claim.\n\n"
    + "| Section | Passed | Failed | Not observable | Skipped | Not run | Total |\n|---|---:|---:|---:|---:|---:|---:|\n");
foreach (var chapter in results.GroupBy(r => r.Section))
{
    var c = Counts(chapter);
    markdown.AppendLine($"| {chapter.Key} | {c.Passed} | {c.Failed} | {c.NotObservable} | {c.Skipped} | {c.NotRun} | {c.Total} |");
}
markdown.AppendLine($"| Total | {totals.Passed} | {totals.Failed} | {totals.NotObservable} | {totals.Skipped} | {totals.NotRun} | {totals.Total} |");
markdown.AppendLine("\nPer-example expected/actual HTML, reason, facade status, and separate facts/syntax invariants are in `results.json`.\n\nHTML mismatches may include test adapter defects; review the expected/actual output before changing the parser. Facts/syntax invariants do not prove expected facts or nodes are complete or semantically correct.\n");
File.WriteAllText(Path.Combine(output, "summary.md"), markdown.ToString(), new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(new
{
    profileId = profile,
    fixtureSha256 = fixtureHash,
    totals,
    factsInvariantFailures = results.Count(r => r.Facts == "failed-invariants"),
    syntaxInvariantFailures = results.Count(r => r.Syntax == "failed-invariants"),
    syntaxUnknownRanges = results.Sum(r => r.SyntaxUnknownRanges),
    reportDirectory = output
}));
return results.Any(r => r.Facts == "failed-invariants" || r.Syntax == "failed-invariants" || r.Reason.StartsWith("execution-error:", StringComparison.Ordinal))
    || strict && results.Any(r => r.Status is "failed" or "not-observable" || includeSyntax && r.Syntax == "not-observable") ? 1 : 0;

static int CheckSyntax(object root, int rawLength)
{
    var pending = new Stack<object>();
    pending.Push(root);
    var unknown = 0;
    while (pending.Count != 0)
    {
        var node = pending.Pop();
        var parent = Range(node);
        if (!parent.HasValue)
            unknown++;
        else if (parent.Value.Start < 0 || parent.Value.End < parent.Value.Start || parent.Value.End > rawLength)
            throw new InvalidOperationException($"Syntax raw range is outside source: {SyntaxHtml.Get<object>(node, "Kind")} [{parent.Value.Start},{parent.Value.End}) / {rawLength}");
        var previousEnd = -1;
        foreach (var child in SyntaxHtml.Get<IEnumerable>(node, "Children").Cast<object>())
        {
            var range = Range(child);
            if (range.HasValue)
            {
                if (parent.HasValue && (range.Value.Start < parent.Value.Start || range.Value.End > parent.Value.End))
                    throw new InvalidOperationException($"Syntax child lies outside parent: {SyntaxHtml.Get<object>(child, "Kind")} [{range.Value.Start},{range.Value.End}) in {SyntaxHtml.Get<object>(node, "Kind")} [{parent.Value.Start},{parent.Value.End})");
                if (range.Value.Start < previousEnd)
                    throw new InvalidOperationException($"Syntax siblings overlap or are out of source order: {SyntaxHtml.Get<object>(node, "Kind")}, start {range.Value.Start} < previous end {previousEnd}");
                previousEnd = range.Value.End;
            }
            pending.Push(child);
        }
    }
    return unknown;
    static (int Start, int End)? Range(object node)
    {
        var span = SyntaxHtml.Get<object?>(node, "RawSpan");
        return span is null ? null : (SyntaxHtml.Get<int>(span, "Start"), SyntaxHtml.Get<int>(span, "End"));
    }
}

static Count Counts(IEnumerable<Result> sequence)
{
    var rows = sequence.ToArray();
    return new(rows.Count(r => r.Status == "passed"), rows.Count(r => r.Status == "failed"),
        rows.Count(r => r.Status == "not-observable"), rows.Count(r => r.Status == "skipped"), rows.Count(r => r.Status == "not-run"), rows.Length);
}

static void CheckFacts(object facts, string raw)
{
    Visit(facts);
    void Visit(object? value, string name = "")
    {
        if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum)
            return;
        if (value.GetType().FullName == "Syntamark.RawSpan")
        {
            if (name != "DecodedSpan" && (SyntaxHtml.Get<int>(value, "Start") < 0 || SyntaxHtml.Get<int>(value, "Length") < 0 || SyntaxHtml.Get<int>(value, "End") > raw.Length))
                throw new InvalidOperationException("Raw range outside input: " + name);
            return;
        }
        if (value.GetType().FullName == "Syntamark.MarkdownTextProjection")
        {
            var text = SyntaxHtml.Get<string?>(value, "Text");
            var position = 0;
            foreach (var segment in SyntaxHtml.Get<IEnumerable>(value, "SourceSegments").Cast<object>())
            {
                var decoded = SyntaxHtml.Get<object>(segment, "DecodedSpan");
                var length = SyntaxHtml.Get<int>(decoded, "Length");
                var start = SyntaxHtml.Get<int>(decoded, "Start");
                if (start != position || length == 0)
                    throw new InvalidOperationException("Decoded segments do not cover text in order.");
                position = SyntaxHtml.Get<int>(decoded, "End");
                var range = SyntaxHtml.Get<object?>(segment, "RawSpan");
                if (SyntaxHtml.Get<object>(segment, "Kind").ToString() == "Linear" && (range is null
                    || SyntaxHtml.Get<int>(range, "End") > raw.Length || raw.Substring(SyntaxHtml.Get<int>(range, "Start"), SyntaxHtml.Get<int>(range, "Length")) != text!.Substring(start, length)))
                    throw new InvalidOperationException("Linear segment differs from raw source.");
            }
            if (position != (text?.Length ?? 0))
                throw new InvalidOperationException("Decoded segment coverage differs from text length.");
        }
        if (value is IEnumerable collection)
        {
            foreach (var item in collection)
                Visit(item);
            return;
        }
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetIndexParameters().Length == 0)
                Visit(property.GetValue(value), property.Name);
    }
}

internal sealed record Example([property: JsonPropertyName("example")] int Number,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("html")] string Html);
internal sealed record Result(int Example, string Section, string Status, string Reason, string Facts,
    string? FactsStatus, string? FactsError, string? ActualHtml, string? ExpectedHtml,
    string Syntax = "not-run", string? SyntaxError = null, int SyntaxUnknownRanges = 0);
internal sealed record Count(int Passed, int Failed, int NotObservable, int Skipped, int NotRun, int Total);
