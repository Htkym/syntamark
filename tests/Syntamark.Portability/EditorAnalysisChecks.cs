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
        Console.WriteLine("Editor front matter recovery: PASS (runtime/netstandard2.0, default identity, raw positions).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
