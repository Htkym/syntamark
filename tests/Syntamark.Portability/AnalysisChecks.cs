using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Syntamark;

internal static class AnalysisChecks
{
    private const string Scope = "analysis";
    private const string Source = "selected.md";
    private const string Version = "snapshot-😀";
    private const string CommonMark = "commonmark/0.31.2";
    private const string Legacy = "lithosharp-markdown/1";
    private const MarkdownAnalysisParts CommonFacts = MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links | MarkdownAnalysisParts.Fences;
    private static readonly MarkdownAnalysisParts[] Parts =
    {
        MarkdownAnalysisParts.FrontMatter, MarkdownAnalysisParts.Headings, MarkdownAnalysisParts.Sections,
        MarkdownAnalysisParts.Links, MarkdownAnalysisParts.Fences, MarkdownAnalysisParts.TextRegions, MarkdownAnalysisParts.Syntax,
    };
    private const string Mixed = "\uFEFFintro\r\n\r\n# A &amp; \\*😀\r\n\r\n"
        + "[use][R] and <https://example.test/a?x=1&y=2> and [empty]()\r\n\r\n"
        + "## Child\r\n\r\ntext <span data-x='[bad](/wrong)'>[ok](/right)</span>\r\n\r\n"
        + "```cs\r\nx & y\r\n```\r\n\r\n# Tail\r\n\r\n[R]: /ref?a=1&amp;b=2 \"Guide\"\r\n";

    internal static void Run(Assembly portable)
    {
        foreach (var profile in new[] { Legacy, CommonMark })
        {
            var options = new MarkdownParseOptions(profileId: profile);
            var full = MarkdownParser.Parse(Mixed, Scope, Source, Version, options);
            var fullSyntax = MarkdownParser.ParseWithSyntax(Mixed, Scope, Source, Version, options);
            var allFacts = MarkdownParser.Analyze(Mixed, MarkdownAnalysisParts.AllFacts, Scope, Source, Version, options);
            var all = MarkdownParser.Analyze(Mixed, MarkdownAnalysisParts.All, Scope, Source, Version, options);
            Equal(Facts(full), Facts(allFacts), profile + ": AllFacts preserves the Parse contract");
            Equal(Facts(fullSyntax.Facts), Facts(all), profile + ": All preserves the ParseWithSyntax facts contract");
            Equal(fullSyntax.Root, all.Root, profile + ": All preserves the syntax tree");
            Check(allFacts.Root is null && allFacts.GetAvailability(MarkdownAnalysisParts.Syntax) == MarkdownAnalysisAvailability.NotRequested,
                profile + ": AllFacts does not request a tree");

            foreach (var selection in Parts.Concat(new[] { MarkdownAnalysisParts.None, CommonFacts, MarkdownAnalysisParts.AllFacts, MarkdownAnalysisParts.All }))
            {
                var selected = MarkdownParser.Analyze(Mixed, selection, Scope, Source, Version, options);
                Check(selected.RequestedParts == selection && selected.AnalysisSchemaVersion == 1,
                    profile + ": normalized request metadata");
                Check(selected.ScopeId == Scope && selected.SourceId == Source && selected.SourceVersion == Version
                    && selected.ParserVersion == full.ParserVersion && selected.ProfileId == profile
                    && selected.ContractVersion == ((selection & MarkdownAnalysisParts.AllFacts) == MarkdownAnalysisParts.AllFacts
                        ? full.ContractVersion : "analysis/1") && selected.OptionsHash == full.OptionsHash
                    && selected.TextHash == full.TextHash && selected.BodySpan == full.BodySpan,
                    profile + ": selected request preserves input and parser identity");
                foreach (var part in Parts)
                {
                    var requested = (selection & part) != 0;
                    var output = Output(selected, part);
                    if (!requested)
                        Check(output is null && selected.GetAvailability(part) == MarkdownAnalysisAvailability.NotRequested,
                            profile + ": omitted " + part + " is not a confirmed empty result");
                    else
                    {
                        Check(output is not null && selected.GetAvailability(part) is MarkdownAnalysisAvailability.Complete or MarkdownAnalysisAvailability.Partial,
                            profile + ": requested " + part + " is eagerly available");
                        Equal(part == MarkdownAnalysisParts.Syntax ? fullSyntax.Root : Output(full, part), output,
                            profile + ": selected " + part + " equals its full result");
                    }
                }
                var counterpart = PortableAnalyze(portable, Mixed, selection, profile);
                Equal(selected, counterpart, profile + ": runtime/portable exact result for " + selection);
                foreach (var part in Parts)
                {
                    var portablePart = Enum.ToObject(portable.GetType("Syntamark.MarkdownAnalysisParts", true)!, (int)part);
                    var availability = counterpart.GetType().GetMethod("GetAvailability")!.Invoke(counterpart, new[] { portablePart });
                    Check(selected.GetAvailability(part).ToString() == availability!.ToString(), profile + ": portable availability " + part);
                }
            }

            var common = MarkdownParser.Analyze(Mixed, CommonFacts, Scope, Source, Version, options);
            Check((int)CommonFacts == 26 && common.AnalysisHash == Hash("{\"analysisSchemaVersion\":1,\"optionsHash\":\""
                + full.OptionsHash + "\",\"requestedParts\":26}"), profile + ": canonical decimal request identity");
            var none = MarkdownParser.Analyze(Mixed, MarkdownAnalysisParts.None, Scope, Source, Version, options);
            Check(none.AnalysisHash == Hash("{\"analysisSchemaVersion\":1,\"optionsHash\":\""
                + full.OptionsHash + "\",\"requestedParts\":0}") && none.AnalysisHash != common.AnalysisHash
                && none.OptionsHash == common.OptionsHash && none.Coverage is not null && none.Diagnostics is not null,
                profile + ": None is a valid distinct metadata request");
            var reordered = MarkdownParser.Analyze(Mixed, MarkdownAnalysisParts.Fences | MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links,
                Scope, Source, Version, options);
            Check(reordered.AnalysisHash == common.AnalysisHash, profile + ": flag spelling cannot change identity");

            var heading = common.Headings!.First();
            Check(heading.Text.Text == "A & *😀", profile + ": decoded heading includes entity, escape and surrogate pair");
            var entity = heading.Text.Map(new RawSpan(2, 1));
            Check(entity.Precision == MarkdownMappingPrecision.CoveringTokens
                && entity.RawFragments.SequenceEqual(new[] { At(Mixed, "&amp;") }), profile + ": selected heading retains atomic entity mapping");
            var selectedLinks = common.Links ?? throw new InvalidOperationException("Selected links were unavailable.");
            Check(!selectedLinks.Any(link => link.Target == "/wrong") && selectedLinks.Any(link => link.Target == "/right"),
                profile + ": link selection keeps the opaque attribute barrier");

            var syntaxOnly = MarkdownParser.Analyze(Mixed, MarkdownAnalysisParts.Syntax, Scope, Source, Version, options);
            var root = syntaxOnly.Root ?? throw new InvalidOperationException("Syntax-only lost its root.");
            Check(root.RawSpan == new RawSpan(0, Mixed.Length) && root.GetRawText() == Mixed
                && syntaxOnly.Links is null && syntaxOnly.Headings is null, profile + ": syntax is owned without publishing dependencies");
            var reference = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Link
                && string.Equals(node.ReferenceLabel, "R", StringComparison.OrdinalIgnoreCase));
            Check(reference.RawSpan == At(Mixed, "[use][R]") && reference.Target == "/ref?a=1&b=2"
                && reference.TargetRawSpan == At(Mixed, "/ref?a=1&amp;b=2")
                && reference.TargetRawSpan!.Value.Start > reference.RawSpan!.Value.End
                && reference.Children.All(child => child.RawSpan is not { } span || span.End <= reference.RawSpan.Value.End),
                profile + ": syntax-only forward definition is a semantic edge, not a child");
            var autolink = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Autolink);
            Check(autolink.TargetRawSpan == At(Mixed, "https://example.test/a?x=1&y=2"),
                profile + ": syntax-only autolink retains its content location");
            var empty = root.DescendantsAndSelf().Single(node => node.Kind == MarkdownSyntaxKind.Link && node.Target == "");
            Check(empty.TargetRawSpan == new RawSpan(At(Mixed, "[empty]()").Start + "[empty](".Length, 0),
                profile + ": syntax-only known empty destination is not an unknown range");

            var portableCommon = PortableAnalyze(portable, Mixed, CommonFacts, profile);
            foreach (var offset in new[] { 0, At(Mixed, "# A").Start, At(Mixed, "😀").End, At(Mixed, "[R]:").Start, Mixed.Length })
                Equal(common.GetLinePosition(offset), portableCommon.GetType().GetMethod("GetLinePosition")!.Invoke(portableCommon, new object[] { offset }),
                    profile + ": selected line position " + offset);

            var emptyResult = MarkdownParser.Analyze("plain\r\n", CommonFacts, Scope, "empty.md", options: options);
            Check(emptyResult.Status == MarkdownParseStatus.Complete && emptyResult.Headings is { Count: 0 }
                && emptyResult.Links is { Count: 0 } && emptyResult.Fences is { Count: 0 }
                && new[] { MarkdownAnalysisParts.Headings, MarkdownAnalysisParts.Links, MarkdownAnalysisParts.Fences }
                    .All(part => emptyResult.GetAvailability(part) == MarkdownAnalysisAvailability.Complete),
                profile + ": requested empty collections confirm completed absence");

            var limited = MarkdownParser.Analyze("# H\r\n", MarkdownAnalysisParts.All, Scope, "limited.md",
                options: new MarkdownParseOptions(maxScanUnits: 1, profileId: profile));
            Check(limited.Status == MarkdownParseStatus.Partial && limited.Diagnostics.Any(d => d.Id == "LMD003")
                && limited.GetAvailability(MarkdownAnalysisParts.Headings) == MarkdownAnalysisAvailability.Partial
                && limited.Root is null && limited.GetAvailability(MarkdownAnalysisParts.Syntax) == MarkdownAnalysisAvailability.Unavailable,
                profile + ": resource stop cannot become complete absence or an empty tree");

            foreach (var failure in new[]
            {
                (Input: "\uD800", Options: options, Diagnostic: "LMD001"),
                (Input: "# H", Options: new MarkdownParseOptions(maxInputUtf16: 1, profileId: profile), Diagnostic: "LMD002"),
            })
            {
                var failed = MarkdownParser.Analyze(failure.Input, MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Syntax,
                    Scope, "failed.md", options: failure.Options);
                Check(failed.Status == MarkdownParseStatus.Failed && failed.Diagnostics.Any(d => d.Id == failure.Diagnostic)
                    && failed.Headings is null && failed.Root is null
                    && failed.GetAvailability(MarkdownAnalysisParts.Headings) == MarkdownAnalysisAvailability.Unavailable
                    && failed.GetAvailability(MarkdownAnalysisParts.Syntax) == MarkdownAnalysisAvailability.Unavailable
                    && failed.GetAvailability(MarkdownAnalysisParts.Links) == MarkdownAnalysisAvailability.NotRequested,
                    profile + ": input rejection differs from omitted and empty results");
            }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Throws<OperationCanceledException>(() => MarkdownParser.Analyze(Mixed, CommonFacts, Scope, Source,
                options: options, cancellationToken: canceled.Token));
        }

        SectionsOnly();
        YamlBarrier();
        foreach (var invalid in new[] { (MarkdownAnalysisParts)256, (MarkdownAnalysisParts)(-1) })
            Throws<ArgumentOutOfRangeException>(() => MarkdownParser.Analyze("plain", invalid, Scope, Source));
        var valid = MarkdownParser.Analyze("plain", MarkdownAnalysisParts.None, Scope, Source);
        foreach (var invalid in new[] { MarkdownAnalysisParts.None, MarkdownAnalysisParts.AllFacts, MarkdownAnalysisParts.All,
            MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links, (MarkdownAnalysisParts)256, (MarkdownAnalysisParts)(-1) })
            Throws<ArgumentOutOfRangeException>(() => valid.GetAvailability(invalid));
        var failedNone = MarkdownParser.Analyze("\uD800", MarkdownAnalysisParts.None, Scope, "none-failed.md");
        Check(failedNone.Status == MarkdownParseStatus.Failed && failedNone.Diagnostics.Any(d => d.Id == "LMD001")
            && Parts.All(part => failedNone.GetAvailability(part) == MarkdownAnalysisAvailability.NotRequested),
            "None still performs mandatory input validation");
        Console.WriteLine("PASS selective: full equivalence, selected facts/maps, semantic seeds, section boundaries, availability, identity, YAML, budgets and runtime/portable equality.");
    }

    private static void SectionsOnly()
    {
        const string raw = "# A\r\nx\r\n## B\r\ny\r\n# C\r\nz\r\n";
        foreach (var profile in new[] { Legacy, CommonMark })
        {
            var result = MarkdownParser.Analyze(raw, MarkdownAnalysisParts.Sections, Scope, "sections.md",
                options: new MarkdownParseOptions(profileId: profile));
            var sections = result.Sections ?? throw new InvalidOperationException("Sections-only lost its output.");
            Check(result.GetAvailability(MarkdownAnalysisParts.Sections) == MarkdownAnalysisAvailability.Complete
                && result.Headings is null && result.GetAvailability(MarkdownAnalysisParts.Headings) == MarkdownAnalysisAvailability.NotRequested,
                profile + ": section dependencies are not requested headings");
            var expected = new[]
            {
                (Key: "preamble", Parent: (string?)null, Heading: (string?)null, Direct: new RawSpan(0, 0), Tree: new RawSpan(0, 0)),
                (Key: "section-0", Parent: (string?)null, Heading: (string?)"heading-0", Direct: new RawSpan(5, 3), Tree: new RawSpan(0, 17)),
                (Key: "section-1", Parent: (string?)"section-0", Heading: (string?)"heading-1", Direct: new RawSpan(14, 3), Tree: new RawSpan(8, 9)),
                (Key: "section-2", Parent: (string?)null, Heading: (string?)"heading-2", Direct: new RawSpan(22, 3), Tree: new RawSpan(17, 8)),
            };
            Check(sections.Count == expected.Length, profile + ": expected section count");
            for (var i = 0; i < expected.Length; i++)
                Check(sections[i].LocalKey == expected[i].Key && sections[i].ParentLocalKey == expected[i].Parent
                    && sections[i].HeadingLocalKey == expected[i].Heading && sections[i].DirectBodySpan == expected[i].Direct
                    && sections[i].SubtreeSpan == expected[i].Tree, profile + ": independently specified section " + i);
        }
    }

    private static void YamlBarrier()
    {
        const string duplicate = "---\r\na: 1\r\na: 2\r\n---\r\n# After\r\n";
        var invalid = MarkdownParser.Analyze(duplicate, MarkdownAnalysisParts.Headings, Scope, "yaml.md");
        Check(invalid.FrontMatter is null && invalid.GetAvailability(MarkdownAnalysisParts.FrontMatter) == MarkdownAnalysisAvailability.NotRequested
            && invalid.Diagnostics.Any(d => d.Id == "LMD005" && d.Reason == "duplicateKey")
            && invalid.Headings is { Count: 1 } && invalid.Headings[0].Text.Text == "After",
            "Unrequested YAML still validates before exposing the body");
        var none = MarkdownParser.Analyze(duplicate, MarkdownAnalysisParts.None, Scope, "yaml-none.md");
        Check(none.Diagnostics.Any(d => d.Id == "LMD005" && d.Reason == "duplicateKey")
            && Parts.All(part => none.GetAvailability(part) == MarkdownAnalysisAvailability.NotRequested),
            "None does not bypass mandatory YAML validation");
        var unterminated = MarkdownParser.Analyze("---\r\na: value\r\n# Hidden\r\n", MarkdownAnalysisParts.Headings, Scope, "unterminated.md");
        Check(unterminated.BodySpan is null && unterminated.Diagnostics.Any(d => d.Id == "LMD004")
            && (unterminated.Headings is null || unterminated.Headings.Count == 0)
            && unterminated.GetAvailability(MarkdownAnalysisParts.Headings) != MarkdownAnalysisAvailability.Complete,
            "Unrequested unterminated YAML never becomes parsed Markdown body");
    }

    private static object PortableAnalyze(Assembly portable, string raw, MarkdownAnalysisParts parts, string profile)
    {
        var options = Activator.CreateInstance(portable.GetType("Syntamark.MarkdownParseOptions", true)!,
            new object[] { 1048576, 131072, 200, 16777216, profile, 1 });
        var selected = Enum.ToObject(portable.GetType("Syntamark.MarkdownAnalysisParts", true)!, (int)parts);
        return portable.GetType("Syntamark.MarkdownParser", true)!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "Analyze" && method.GetParameters().Length == 7)
            .Invoke(null, new[] { (object)raw, selected, Scope, Source, Version, options, CancellationToken.None })!;
    }

    private static object? Output(object value, MarkdownAnalysisParts part) => Get(value, part switch
    {
        MarkdownAnalysisParts.FrontMatter => "FrontMatter",
        MarkdownAnalysisParts.Headings => "Headings",
        MarkdownAnalysisParts.Sections => "Sections",
        MarkdownAnalysisParts.Links => "Links",
        MarkdownAnalysisParts.Fences => "Fences",
        MarkdownAnalysisParts.TextRegions => "TextRegions",
        MarkdownAnalysisParts.Syntax => "Root",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    });

    private static Dictionary<string, object?> Facts(object value)
    {
        var names = new[] { "ScopeId", "SourceId", "SourceVersion", "ParserVersion", "ProfileId", "ContractVersion", "OptionsHash", "TextHash",
            "Status", "BodySpan", "FrontMatter", "Headings", "Sections", "Links", "Fences", "TextRegions", "Diagnostics", "Capabilities", "Coverage" };
        return names.ToDictionary(name => name, name => Get(value, name), StringComparer.Ordinal);
    }

    private static object? Get(object value, string name) => value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(value);
    private static RawSpan At(string raw, string token)
    {
        var start = raw.IndexOf(token, StringComparison.Ordinal);
        Check(start >= 0, "Missing independent fixture token: " + token);
        return new RawSpan(start, token.Length);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void Equal(object? expected, object? actual, string message) => Check(Json(expected) == Json(actual), message);
    private static string Json(object? value) => value is null ? "null" : JsonSerializer.Serialize(value, value.GetType());
    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
