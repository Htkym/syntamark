using System.Collections;
using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class ProfileChecks
{
    private const string Legacy = "lithosharp-markdown/1";
    private const string CommonMark = "commonmark/0.31.2";
    private const string DefaultHash = "970d603ab09c239dde63511f42543a76b80b8897a323e335a4cf57685d1c8591";
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(Assembly runtime, Assembly portable)
    {
        var defaultDocument = MarkdownParser.Parse("# H", "profile", "default.md");
        Check(defaultDocument.ProfileId == Legacy && defaultDocument.OptionsHash == DefaultHash, "Legacy default profile/hash changed.");
        var standard = new MarkdownParseOptions(profileId: CommonMark);
        var standardDocument = MarkdownParser.Parse("# H", "profile", "standard.md", options: standard);
        Check(standardDocument.ProfileId == CommonMark && standardDocument.ContractVersion == "2.0"
            && standardDocument.OptionsHash != DefaultHash, "Selected profile was not propagated or hashed.");
        Check(standardDocument.Capabilities.Single(c => c.Feature == "markdownSyntax").State == MarkdownFeatureState.Partial,
            "The profile must not claim full standard conformance without the complete gate.");
        Reject(() => new MarkdownParseOptions(profileId: "commonmark/latest"));
        Reject(() => new MarkdownParseOptions(profileId: CommonMark, optionsSchemaVersion: 2));
        Reject(() => new MarkdownParseOptions(profileId: CommonMark, maxScanUnits: 0));

        const string frontMatter = "---\ntitle: value\n---\n# After\n";
        Check(MarkdownParser.Parse(frontMatter, "profile", "legacy.md").FrontMatter.State == MarkdownFrontMatterState.Parsed,
            "Legacy YAML handling changed.");
        var withoutYaml = MarkdownParser.Parse(frontMatter, "profile", "plain.md", options: standard);
        Check(withoutYaml.FrontMatter.State == MarkdownFrontMatterState.Absent
            && withoutYaml.BodySpan == new RawSpan(0, frontMatter.Length), "CommonMark consumed front matter.");
        const string bom = "\uFEFF# H\r\n";
        Check(MarkdownParser.Parse(bom, "profile", "bom.md", options: standard).BodySpan == new RawSpan(0, bom.Length),
            "The CommonMark snapshot must retain BOM offsets.");

        var extensions = new[]
        {
            "| H |\n| - |\n| x |\n", "- [x] task\n", "~~strike~~\n", "https://example.com\n",
            "$$\nx\n$$\n", "$x$\n", ":::note\ntext\n:::\n", "import x from 'x';\n\n{value}\n",
        };
        foreach (var input in extensions.Append(frontMatter).Append("a <span>**b** [x](/u)</span>\n"))
        {
            var runtimeFacts = ParseCore(runtime, input, CommonMark);
            var portableFacts = ParseCore(portable, input, CommonMark);
            Check(JsonSerializer.Serialize(Snapshot(runtimeFacts)) == JsonSerializer.Serialize(Snapshot(portableFacts)),
                "Selected profile differs between runtime and portable source.");
            Check((string)Get(runtimeFacts, "ProfileId")! == CommonMark, "Portable metadata lost selected profile.");
        }
        foreach (var input in extensions)
        {
            var facts = MarkdownParser.Parse(input, "profile", "extension.md", options: standard);
            Check(facts.FrontMatter.State == MarkdownFrontMatterState.Absent
                && !facts.Links.Any(link => link.Resolution != MarkdownLinkResolution.UnresolvedReference),
                "A non-CommonMark extension leaked links or front matter into the standard profile.");
            Check(!facts.TextRegions.Any(r => r.Kind is "tableCell" or "blockMath" or "rawHtmlOrMdx"),
                "An extension leaked special projection into the standard profile.");
            var kinds = NodeKinds(runtime, input, CommonMark).ToArray();
            Check(!kinds.Any(k => k is "LithoTable" or "LithoStrike" or "LithoMath" or "LithoMathInline" or "LithoDirective" or "LithoAdmonition" or "task-marker"),
                "An extension AST node leaked into the standard profile.");
        }

        const string inlineHtml = "a <span data-x='[bad](/wrong)'>**b** [x](/u)</span>\n";
        var inline = MarkdownParser.Parse(inlineHtml, "profile", "inline.md", options: standard);
        Check(inline.Links.Count == 1 && inline.Links[0].Target == "/u", "Inline HTML hid Markdown children or exposed an attribute as a link.");
        Check(inline.Coverage.UnprojectedRegions.Count(r => r.Kind == "rawHtmlInline") == 2,
            "Inline HTML must expose only its two tag-token ranges as unprojected.");
        Check(inline.Status == MarkdownParseStatus.Complete && inline.Coverage.DecodedMapping == MarkdownCoverageState.Partial,
            "Recognized HTML projection limits must not become parser termination failures.");
        Check(inline.Coverage.UnprojectedRegions.Select(r => r.RawSpan is { } span ? inlineHtml.Substring(span.Start, span.Length) : null)
            .SequenceEqual(new[] { "<span data-x='[bad](/wrong)'>", "</span>" }), "Inline HTML opaque positions include normal Markdown content.");
        const string blockHtml = "<div>\n# false\n[bad](/wrong)\n</div>\n\n# real\n";
        var block = MarkdownParser.Parse(blockHtml, "profile", "block.md", options: standard);
        Check(block.Headings.Count == 1 && block.Headings[0].Text.Text == "real" && block.Links.Count == 0,
            "HTML block content generated false Markdown facts.");
        var opaque = block.Coverage.UnprojectedRegions.Single(r => r.Kind == "rawHtmlBlock");
        Check(opaque.RawSpan is { } range && blockHtml.Substring(range.Start, range.Length).Contains("[bad](/wrong)", StringComparison.Ordinal),
            "HTML block raw range lost its content.");

        foreach (var profile in new[] { Legacy, CommonMark })
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                MarkdownParser.Parse("# H", "profile", "cancel.md", options: new MarkdownParseOptions(profileId: profile), cancellationToken: canceled.Token);
                throw new InvalidOperationException("Pre-cancellation returned a document.");
            }
            catch (OperationCanceledException) { }
            var limited = MarkdownParser.Parse("# H", "profile", "budget.md", options: new MarkdownParseOptions(maxScanUnits: 1, profileId: profile));
            Check(limited.Status == MarkdownParseStatus.Partial && limited.Diagnostics.Any(d => d.Origin == "resource"),
                "Selected profile lost budget stopping.");
            var inputAtLimit = MarkdownParser.Parse("# H\n", "profile", "input.md", options: new MarkdownParseOptions(maxInputUtf16: 4, profileId: profile));
            var inputOverLimit = MarkdownParser.Parse("# H\n", "profile", "input.md", options: new MarkdownParseOptions(maxInputUtf16: 3, profileId: profile));
            Check(inputAtLimit.Status != MarkdownParseStatus.Failed && inputOverLimit.Status == MarkdownParseStatus.Failed
                && inputOverLimit.Diagnostics.Single().Reason == "input", "Input length boundary changed by profile selection.");
        }
        Console.WriteLine("SM03 profile selection, identity, extension isolation, HTML projection, runtime/source and budget checks PASS.");
    }

    private static object ParseCore(Assembly assembly, string raw, string profile)
    {
        var options = Options(assembly, profile);
        return assembly.GetType("Syntamark.Compilation.MdParser", true)!.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "Parse" && m.GetParameters().Length == 6)
            .Invoke(null, new object?[] { raw, "profile", "same.md", null, options, CancellationToken.None })!;
    }

    private static object Options(Assembly assembly, string profile) => assembly.GetType("Syntamark.Compilation.MdOptions", true)!
        .GetConstructors(Instance).Single(c => c.GetParameters().Length == 6)
        .Invoke(new object[] { 1048576, 131072, 200, 16777216, profile, 1 });

    private static IEnumerable<string> NodeKinds(Assembly assembly, string input, string profile)
    {
        var context = Activator.CreateInstance(assembly.GetType("Syntamark.Compilation.MdParseContext", true)!, Instance, null,
            new[] { Options(assembly, profile), (object)CancellationToken.None }, null)!;
        var tree = assembly.GetType("Syntamark.Compilation.LithoBlockParser", true)!.GetMethod("ParseFactsBlocks", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new[] { input, context })!;
        return Walk((IEnumerable)tree.GetType().GetField("Item1")!.GetValue(tree)!);
        static IEnumerable<string> Walk(IEnumerable nodes)
        {
            foreach (var node in nodes)
            {
                var type = node!.GetType();
                yield return type.Name;
                if (type.Name == "LithoItem" && Get(node, "Checked") is bool)
                    yield return "task-marker";
                foreach (var property in type.GetProperties(Instance).Where(p => p.Name is "Children" or "Inlines" or "Items" or "Label" or "Rows"))
                    if (property.GetValue(node) is IEnumerable children)
                        foreach (var kind in Walk(children))
                            yield return kind;
                if (node is IEnumerable nested)
                    foreach (var kind in Walk(nested))
                        yield return kind;
            }
        }
    }

    private static object? Snapshot(object? value)
    {
        if (value is null || value is string || value.GetType().IsPrimitive)
            return value;
        if (value.GetType().IsEnum)
            return value.ToString();
        if (value is IEnumerable list)
            return list.Cast<object?>().Select(Snapshot).ToArray();
        return value.GetType().GetProperties(Instance).Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")
            .ToDictionary(p => p.Name, p => Snapshot(p.GetValue(value)), StringComparer.Ordinal);
    }

    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);
    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid profile/schema/budget was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
