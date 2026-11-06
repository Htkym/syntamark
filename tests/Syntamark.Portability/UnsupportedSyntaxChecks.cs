using System.Collections;
using System.Reflection;
using Syntamark;

internal static class UnsupportedSyntaxChecks
{
    private const string Mixed = "\uFEFF---\r\ntitle: \"[^yaml] {#yaml}\"\r\n---\r\n"
        + "# Head 😀 {#heading}\r\n\r\n> A[^note]\r\n>\r\n> [^note]: note\r\n\r\n"
        + "Term\r\n\r\n: description with `code`\r\n\r\n+---+---+\r\n| A | B |\r\n+===+===+\r\n\r\n"
        + "- Item {.item}\r\n\r\n| Head |\r\n| --- |\r\n| [^cell] {.cell} |\r\n";

    private const string Excluded = "---\r\ntitle: \"[^yaml] {#yaml}\"\r\n---\r\n"
        + "`[^inline] {#inline}` and `multi\r\n[^multiline] {#multiline}`\r\n\r\n"
        + "\\[^escaped] \\{#escaped} &#91;^entity] &#123;#entity}\r\n\r\n"
        + "[link](/{#url} \"[^title]\")\r\n\r\n<span data-x=\"{#html} [^html]\">literal</span>\r\n\r\n"
        + "```md\r\n[^fenced]: x\r\nTerm\r\n: d\r\n+---+\r\n| x |\r\n{#fenced}\r\n```\r\n\r\n"
        + "    [^indented] {#indented}\r\n    : d\r\n    +---+\r\n    | x |\r\n\r\n"
        + ": orphan\r\n\r\n\r\n+---+\r\n\r\nTerm\r\n    : continuation\r\n    +---+\r\n    | x |\r\n";

    internal static void Run(Assembly portable)
    {
        foreach (var assembly in new[] { typeof(MarkdownParser).Assembly, portable })
        {
            foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
            {
                var mixed = profile == "commonmark/0.31.2" ? Mixed.Substring(Mixed.IndexOf("# Head", StringComparison.Ordinal)) : Mixed;
                var excluded = profile == "commonmark/0.31.2" ? Excluded.Substring(Excluded.IndexOf("`[^inline]", StringComparison.Ordinal)) : Excluded;
                var expected = new[]
                {
                    At(mixed, "{#heading}", "GenericAttribute", "genericAttribute"),
                    At(mixed, "[^note]", "Footnote", "footnoteReference"),
                    At(mixed, "[^note]:", "Footnote", "footnoteDefinition", length: 7),
                    At(mixed, ": description", "DefinitionList", "definitionListMarker", length: 1),
                    At(mixed, "+---+---+", "GridTable", "gridTableBorder"),
                    At(mixed, "+===+===+", "GridTable", "gridTableBorder"),
                    At(mixed, "{.item}", "GenericAttribute", "genericAttribute"),
                    At(mixed, "[^cell]", "Footnote", "footnoteReference"),
                    At(mixed, "{.cell}", "GenericAttribute", "genericAttribute"),
                };
                var selected = Analyze(assembly, mixed, MarkdownAnalysisParts.UnsupportedSyntax, profile);
                Check(Candidates(selected).SequenceEqual(expected), profile + ": candidate kinds, reasons and raw UTF-16 token spans");
                Check(Get(selected, "Root") is null && Get(selected, "Headings") is null,
                    profile + ": candidate-only requests do not publish other parts");
                Check(Availability(selected, assembly) == "Complete", profile + ": selected candidates are complete");
                Check(Candidates(Analyze(assembly, excluded, MarkdownAnalysisParts.UnsupportedSyntax, profile)).Length == 0,
                    profile + ": code, front matter, escapes, entities, HTML, link metadata and isolated markers are excluded");

                var omitted = Analyze(assembly, mixed, MarkdownAnalysisParts.All, profile);
                Check(Get(omitted, "UnsupportedSyntax") is null && Availability(omitted, assembly) == "NotRequested",
                    profile + ": legacy All retains its selection contract");
                Check(Equals(Get(omitted, "OptionsHash"), Get(selected, "OptionsHash"))
                    && !Equals(Get(omitted, "AnalysisHash"), Get(selected, "AnalysisHash")),
                    profile + ": selection affects analysis identity without changing options identity");

                const string nested = "> Term\n> : text\n\n- Term\n\n  : text\n";
                var nestedCandidates = Candidates(Analyze(assembly, nested, MarkdownAnalysisParts.UnsupportedSyntax, profile));
                Check(nestedCandidates.SequenceEqual(new[]
                {
                    At(nested, ": text", "DefinitionList", "definitionListMarker", length: 1),
                    At(nested, "  : text", "DefinitionList", "definitionListMarker", offset: 2, length: 1),
                }), profile + ": definition markers respect container source maps and a blank line");

                const string labels = "[see {#label}](url) [see[^inner]](url) ![alt {.image}](image)";
                Check(Candidates(Analyze(assembly, labels, MarkdownAnalysisParts.UnsupportedSyntax, profile)).SequenceEqual(new[]
                {
                    At(labels, "{#label}", "GenericAttribute", "genericAttribute"),
                    At(labels, "[^inner]", "Footnote", "footnoteReference"),
                    At(labels, "{.image}", "GenericAttribute", "genericAttribute"),
                }), profile + ": link and image labels remain Markdown while metadata stays excluded");
            }
        }

        var limited = MarkdownParser.Analyze("x[^note]", MarkdownAnalysisParts.UnsupportedSyntax, "candidates", "limited.md",
            options: new MarkdownParseOptions(maxScanUnits: 1));
        Check(limited.Status == MarkdownParseStatus.Partial
            && limited.GetAvailability(MarkdownAnalysisParts.UnsupportedSyntax) == MarkdownAnalysisAvailability.Partial
            && limited.Diagnostics.Any(diagnostic => diagnostic.Id == "LMD003"),
            "candidate requests retain resource-limit status");
        Console.WriteLine("PASS unsupported syntax: runtime/portable token ranges, exclusions, selection and limits");
    }

    private readonly record struct Candidate(string Kind, string Reason, int Start, int Length);

    private static Candidate At(string text, string token, string kind, string reason, int offset = 0, int? length = null)
    {
        var start = text.IndexOf(token, StringComparison.Ordinal);
        Check(start >= 0, "Expected token is present.");
        return new Candidate(kind, reason, start + offset, length ?? token.Length);
    }

    private static Candidate[] Candidates(object result) => ((IEnumerable)Get(result, "UnsupportedSyntax")!).Cast<object>()
        .Select(candidate =>
        {
            var range = Get(candidate, "RawSpan")!;
            return new Candidate(Get(candidate, "Kind")!.ToString()!, (string)Get(candidate, "Reason")!,
                (int)Get(range, "Start")!, (int)Get(range, "Length")!);
        }).ToArray();

    private static object Analyze(Assembly assembly, string text, MarkdownAnalysisParts parts, string profile)
    {
        var optionsType = assembly.GetType("Syntamark.MarkdownParseOptions", true)!;
        var constructor = optionsType.GetConstructors().Single(candidate => candidate.GetParameters().Length == 6);
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.Name == "profileId" ? profile : parameter.DefaultValue).ToArray();
        var options = constructor.Invoke(arguments);
        var selection = Enum.ToObject(assembly.GetType("Syntamark.MarkdownAnalysisParts", true)!, (int)parts);
        return assembly.GetType("Syntamark.MarkdownParser", true)!.GetMethod("Analyze")!
            .Invoke(null, new[] { text, selection, "candidates", "input.md", null, options, CancellationToken.None })!;
    }

    private static string Availability(object result, Assembly assembly)
    {
        var selection = Enum.ToObject(assembly.GetType("Syntamark.MarkdownAnalysisParts", true)!, (int)MarkdownAnalysisParts.UnsupportedSyntax);
        return result.GetType().GetMethod("GetAvailability")!.Invoke(result, new[] { selection })!.ToString()!;
    }

    private static object? Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
