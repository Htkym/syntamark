using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class EditorAnalysisChecks
{
    internal static void Run(Assembly portable)
    {
        var options = new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody);
        const MarkdownAnalysisParts parts = MarkdownAnalysisParts.FrontMatter | MarkdownAnalysisParts.Headings
            | MarkdownAnalysisParts.Sections | MarkdownAnalysisParts.Syntax;
        foreach (var raw in new[] { "\uFEFF---\r\na: x\r\n# Heading\r\n", "---\na: x\n# Heading", "---" })
        {
            var strict = MarkdownParser.Analyze(raw, parts, "editor", "frontmatter.md");
            var recovered = MarkdownParser.Analyze(raw, parts, "editor", "frontmatter.md", options: options);
            Check(strict.BodySpan is null && strict.FrontMatter!.State == MarkdownFrontMatterState.Unterminated
                && strict.Diagnostics.Any(d => d.Id == "LMD004") && strict.Headings!.Count == 0,
                "Default parsing must keep an unterminated envelope out of the body.");
            Check(recovered.BodySpan == new RawSpan(0, raw.Length) && recovered.Status == MarkdownParseStatus.Partial
                && recovered.Diagnostics.Any(d => d.Id == "LMD004" && d.RawSpan == strict.FrontMatter!.OpeningSpan)
                && recovered.FrontMatter!.State == MarkdownFrontMatterState.Unterminated && recovered.FrontMatter.Root is null
                && recovered.FrontMatter.FailureSpan == new RawSpan(raw.Length, 0)
                && recovered.FrontMatter.FailureLine == (raw == "---" ? 2 : 4)
                && recovered.GetAvailability(MarkdownAnalysisParts.FrontMatter) == MarkdownAnalysisAvailability.Partial
                && recovered.Root is not null, "Recovery must expose original input, failure evidence and partial status together.");
            if (raw.Contains("# Heading", StringComparison.Ordinal))
                Check(recovered.Headings!.Single().RawSpan.Start == raw.IndexOf("# Heading", StringComparison.Ordinal),
                    "Recovered heading positions must use the original BOM/CRLF snapshot.");
            Check(recovered.OptionsHash != strict.OptionsHash && recovered.AnalysisHash != strict.AnalysisHash,
                "Recovery must not share a strict analysis cache identity.");
            var portableOptions = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
                new[] { Enum.ToObject(portable.GetType("Syntamark.MarkdownFrontMatterRecovery", true)!, 1),
                    (object)1048576, 131072, 200, 16777216, "lithosharp-markdown/1", 1 });
            var counterpart = portable.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Analyze")!.Invoke(null,
                new[] { (object)raw, Enum.ToObject(portable.GetType("Syntamark.MarkdownAnalysisParts", true)!, (int)parts),
                    "editor", "frontmatter.md", null, portableOptions, CancellationToken.None });
            Check(JsonSerializer.Serialize(recovered) == JsonSerializer.Serialize(counterpart, counterpart!.GetType()),
                "Runtime and Source recovery contracts must match.");
        }
        var ordinary = MarkdownParser.Parse("# Heading", "editor", "plain.md");
        var explicitNone = MarkdownParser.Parse("# Heading", "editor", "plain.md",
            options: new MarkdownParseOptions(MarkdownFrontMatterRecovery.None));
        Check(ordinary.OptionsHash == explicitNone.OptionsHash && ordinary.FrontMatter.FailureSpan is null,
            "The existing constructor and explicit None must preserve default option identity.");
        var standard = MarkdownParser.Parse("---\na: x", "editor", "standard.md",
            options: new MarkdownParseOptions(profileId: "commonmark/0.31.2"));
        var standardRecovery = MarkdownParser.Parse("---\na: x", "editor", "standard.md",
            options: new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody, profileId: "commonmark/0.31.2"));
        Check(standard.OptionsHash == standardRecovery.OptionsHash && standardRecovery.FrontMatter.State == MarkdownFrontMatterState.Absent,
            "Profiles without front matter must hash the same effective options.");
        foreach (var assembly in new[] { typeof(MarkdownParser).Assembly, portable })
            RenderingBlocks(assembly);
        Console.WriteLine("Editor front matter recovery: PASS (runtime/netstandard2.0, default identity, raw positions).");
    }

    private static void RenderingBlocks(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const string raw = "---\ntitle: x\n---\n# Heading\n\n[use][r]\n\n[r]: /target\n";
        var optionsType = assembly.GetType("Syntamark.Compilation.MdOptions", true)!;
        var parser = assembly.GetType("Syntamark.Compilation.MdParser", true)!;
        var options = optionsType.GetConstructors(flags).Single(ctor => ctor.GetParameters().Length == 6)
            .Invoke(new object[] { 1048576, 131072, 200, 16777216, "lithosharp-markdown/1", 1 });
        var parts = Enum.ToObject(assembly.GetType("Syntamark.Compilation.MdAnalysisParts", true)!, 10);
        var arguments = new[] { (object)raw, parts, "rendering", "same.md", null, options, CancellationToken.None };
        var result = parser.GetMethod("AnalyzeWithBlocks", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
        object? Get(object value, string name) => value.GetType().GetProperty(name, flags)!.GetValue(value);
        var blocks = ((System.Collections.IEnumerable)Get(result, "Blocks")!).Cast<object>().ToArray();
        var heading = ((System.Collections.IEnumerable)Get(result, "Headings")!).Cast<object>().Single();
        var headingOffset = (int)Get(Get(heading, "RawSpan")!, "Start")!;
        var bodyOffset = (int)Get(Get(result, "BodySpan")!, "Start")!;
        Check(blocks[0].GetType().Name == "LithoHeading" && Get(result, "Root") is null
            && (int)Get(Get(blocks[0], "Span")!, "Start")! + bodyOffset == headingOffset,
            "Rendering blocks and raw facts must share one body offset without requiring public syntax.");
        var links = ((System.Collections.IEnumerable)Get(result, "Links")!).Cast<object>();
        Check(links.Any(link => (string?)Get(link, "Target") == "/target" && Get(link, "Resolution")!.ToString() == "ResolvedReference"),
            "The shared rendering parse must resolve forward references for selected facts.");
        var ordinary = parser.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
        Check(Get(ordinary, "Blocks") is null && Get(ordinary, "AnalysisHash")!.Equals(Get(result, "AnalysisHash")),
            "Ordinary analysis must not retain rendering blocks or change selected-facts identity.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
