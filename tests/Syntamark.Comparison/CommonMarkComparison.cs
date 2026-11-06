using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Markdig;
using Syntamark;

internal static class CommonMarkComparison
{
    private const string Profile = "commonmark/0.31.2";
    private const string FixtureHash = "d431b29d97b6f73e69d547109cf5081578fac931e72afe95639ebe766c1b2a20";

    internal static int Run(string directory)
    {
        SyntaxHtml.CheckAdapter();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spec.json"));
        if (Hash(bytes) != FixtureHash)
            throw new InvalidOperationException("Pinned CommonMark fixture checksum changed.");
        var examples = JsonSerializer.Deserialize<SpecExample[]>(bytes)!;
        if (examples.Length != 652 || !examples.Select(e => e.Number).SequenceEqual(Enumerable.Range(1, 652)))
            throw new InvalidOperationException("CommonMark denominator or example identities changed.");

        var builder = new MarkdownPipelineBuilder().UsePreciseSourceLocation();
        var pipeline = builder.Build();
        if (pipeline.Extensions.Count != 0)
            throw new InvalidOperationException("The comparison requires CommonMark core without extensions.");
        var assembly = typeof(Syntamark.MarkdownParser).Assembly;
        var optionsType = assembly.GetType("Syntamark.Compilation.MdOptions", true)!;
        var contextType = assembly.GetType("Syntamark.Compilation.MdParseContext", true)!;
        var parse = assembly.GetType("Syntamark.Compilation.LithoBlockParser", true)!
            .GetMethod("ParseFactsBlocks", BindingFlags.Static | BindingFlags.NonPublic)!;
        var options = new MarkdownParseOptions(1048576, 1048576, 200, 134217728, Profile, 1);
        var coreOptions = Activator.CreateInstance(optionsType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { 1048576, 1048576, 200, 134217728, Profile, 1 }, null)!;
        var metadata = Syntamark.MarkdownParser.Parse("", "commonmark-comparison", "metadata", options: options);
        var results = new List<ExampleResult>();
        foreach (var example in examples)
        {
            FactsResult? facts = null;
            try
            {
                var document = Syntamark.MarkdownParser.Parse(example.Markdown, "commonmark-comparison",
                    example.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), options: options);
                facts = new(document.Status.ToString(), document.Coverage.RawPositions.ToString(),
                    document.Coverage.DecodedMapping.ToString(), document.Coverage.UnprojectedRegions.Count,
                    document.Coverage.Reasons.ToArray(), document.Diagnostics.Select(d => new DiagnosticResult(d.Id, d.Origin, d.Reason)).ToArray());
            }
            catch (Exception error)
            {
                results.Add(new(example.Number, example.Section, example.Markdown, Hash(Encoding.UTF8.GetBytes(example.Markdown)),
                    example.Html, new("execution-error", null, false, error.Message), ObserveMarkdig(example), null));
                continue;
            }

            SyntaxResult syntamark;
            try
            {
                var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { coreOptions, (object)CancellationToken.None }, null)!;
                var tree = parse.Invoke(null, new[] { example.Markdown, context })!;
                var blocks = (IEnumerable)tree.GetType().GetField("Item1")!.GetValue(tree)!;
                var html = SyntaxHtml.Render(blocks, decodedInfo: true);
                var match = html == example.Html;
                syntamark = new(facts.Status != "Complete" || facts.ResourceLimited ? "incomplete"
                    : match ? "passed" : "failed", html, match, null);
            }
            catch (Exception error) when ((error.InnerException ?? error) is NotSupportedException)
            {
                syntamark = new("not-observable", null, false, (error.InnerException ?? error).Message);
            }
            catch (Exception error)
            {
                syntamark = new("execution-error", null, false, (error.InnerException ?? error).Message);
            }
            results.Add(new(example.Number, example.Section, example.Markdown, Hash(Encoding.UTF8.GetBytes(example.Markdown)),
                example.Html, syntamark, ObserveMarkdig(example), facts));
        }

        var totals = Counts(results);
        var report = new
        {
            contract = "commonmark-html-and-separate-facts/1",
            specification = "CommonMark 0.31.2",
            htmlComparison = "Ordinal full-string equality. Renderer formatting differences are retained and are not inferred to be grammar failures.",
            fixtureSha256 = FixtureHash,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            syntamark = new
            {
                profileId = Profile,
                metadata.ParserVersion,
                metadata.ContractVersion,
                metadata.OptionsHash,
                assemblyIdentity = assembly.FullName,
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assemblySha256 = Hash(File.ReadAllBytes(assembly.Location)),
                options = new { maxInputUtf16 = 1048576, maxOutputItems = 1048576, maxNestingDepth = 200, maxScanUnits = 134217728 },
                syntaxEntry = "ParseFactsBlocks + shared test-only SyntaxHtml, decoded info strings",
                factsEntry = "MarkdownParser.Parse on the same raw input; separate parse from test syntax observation"
            },
            markdig = new
            {
                packageVersion = "1.5.1",
                assemblyIdentity = typeof(Markdig.Markdown).Assembly.FullName,
                informationalVersion = typeof(Markdig.Markdown).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                preciseSourceLocations = builder.PreciseSourceLocation,
                extensions = pipeline.Extensions.Select(e => e.GetType().FullName).ToArray(),
                maximumNestingDepth = pipeline.MaximumNestingDepth,
                factsContract = "not-adapted; no equivalent resource or decoded mapping contract is inferred from HTML"
            },
            totals,
            chapters = results.GroupBy(r => r.Section).Select(g => new
            {
                section = g.Key,
                totals = Counts(g),
                syntamarkRemaining = g.Where(r => r.Syntamark.Status != "passed").Select(r => r.Example),
                markdigRemaining = g.Where(r => r.Markdig.Status != "passed").Select(r => r.Example)
            }),
            results
        };
        var output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(new { specification = "CommonMark 0.31.2", totals, reportDirectory = output }));
        // Inventory mode reports syntax gaps without treating them as execution or adapter success.
        return results.Any(r => r.Syntamark.Status == "execution-error" || r.Markdig.Status == "execution-error") ? 1 : 0;

        SyntaxResult ObserveMarkdig(SpecExample example)
        {
            try
            {
                var html = Markdig.Markdown.ToHtml(example.Markdown, pipeline);
                return new(html == example.Html ? "passed" : "failed", html, html == example.Html, null);
            }
            catch (Exception error)
            {
                return new("execution-error", null, false, error.Message);
            }
        }
    }

    private static object Counts(IEnumerable<ExampleResult> rows)
    {
        var all = rows.ToArray();
        return new
        {
            total = all.Length,
            syntamark = SyntaxCounts(all.Select(r => r.Syntamark)),
            markdig = SyntaxCounts(all.Select(r => r.Markdig)),
            syntamarkFacts = new
            {
                complete = all.Count(r => r.Facts?.Status == "Complete"),
                partial = all.Count(r => r.Facts?.Status == "Partial"),
                failed = all.Count(r => r.Facts?.Status == "Failed"),
                notObserved = all.Count(r => r.Facts is null),
                resourceLimited = all.Count(r => r.Facts?.ResourceLimited == true),
                rawPositionsComplete = all.Count(r => r.Facts?.RawPositions == "Complete"),
                decodedMappingComplete = all.Count(r => r.Facts?.DecodedMapping == "Complete")
            }
        };
    }

    private static object SyntaxCounts(IEnumerable<SyntaxResult> rows)
    {
        var all = rows.ToArray();
        return new
        {
            passed = all.Count(r => r.Status == "passed"),
            failed = all.Count(r => r.Status == "failed"),
            incomplete = all.Count(r => r.Status == "incomplete"),
            notObservable = all.Count(r => r.Status == "not-observable"),
            executionErrors = all.Count(r => r.Status == "execution-error"),
            skipped = 0,
            notRun = 0
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record SpecExample([property: JsonPropertyName("example")] int Number,
        [property: JsonPropertyName("section")] string Section, [property: JsonPropertyName("markdown")] string Markdown,
        [property: JsonPropertyName("html")] string Html);
    private sealed record SyntaxResult(string Status, string? ActualHtml, bool HtmlMatch, string? Error);
    private sealed record DiagnosticResult(string Id, string Origin, string Reason);
    private sealed record FactsResult(string Status, string RawPositions, string DecodedMapping, int UnprojectedRegions,
        string[] Reasons, DiagnosticResult[] Diagnostics)
    {
        public bool ResourceLimited => Diagnostics.Any(d => d.Origin == "resource");
    }
    private sealed record ExampleResult(int Example, string Section, string Markdown, string InputSha256,
        string ExpectedHtml, SyntaxResult Syntamark, SyntaxResult Markdig, FactsResult? Facts);
}
