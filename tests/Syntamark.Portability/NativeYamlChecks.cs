using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Syntamark;

internal static class NativeYamlChecks
{
    internal static void Run(Assembly portable)
    {
        OfficialFixtures(portable);
        const string input = "\uFEFF---\r\ntype: concept\r\ntitle: 'A # title' # comment\r\ntags: [one, \"false\", true, null, 0x2A, -3.5e2]\r\nsources:\r\n  - id: source-1\r\n    resource: https://example.org/a#fragment\r\n    actor: human:author\r\n  - id: source-2\r\n    extra: {nested: [1, 2], empty: {}}\r\n...\r\n";
        var document = Parse(input, portable);
        Check(document.Status == MarkdownParseStatus.Complete && document.ProfileId == "native-yaml/1", "Native YAML profile and complete document");
        var root = document.Root!;
        Check(root.Kind == MarkdownYamlKind.Mapping && root.Entries.Count == 4, "Ordered mapping entries");
        Check(root.Entries[1].Value.Value == "A # title" && root.Entries[1].Value.ScalarStyle == YamlScalarStyle.SingleQuoted, "Quoted comments remain content");
        var tags = root.Entries[2].Value.Children;
        Check(tags.Select(node => node.ScalarKind).SequenceEqual(new[] { YamlScalarKind.String, YamlScalarKind.String,
            YamlScalarKind.Boolean, YamlScalarKind.Null, YamlScalarKind.Integer, YamlScalarKind.Float }), "YAML 1.2 Core scalar kinds preserve quoted strings");
        var sources = root.Entries[3].Value.Children;
        Check(sources.Count == 2 && sources[0].Entries[1].Value.Value == "https://example.org/a#fragment"
            && sources[0].Entries[2].Value.Value == "human:author" && sources[1].Entries[1].Value.Entries[0].Value.Children.Count == 2,
            "Compact sequence mappings, URL/actor colons and nested flow values");
        foreach (var node in Nodes(root))
            Check(node.RawSpan.End <= input.Length && node.GetRawText() == input.Substring(node.RawSpan.Start, node.RawSpan.Length), "Every YAML raw span belongs to the original UTF-16 snapshot");

        foreach (var (yaml, expected) in new[]
        {
            ("v: |\n  a\n  b\n", "a\nb\n"),
            ("v: >-\n  a\n  b\n\n  c\n", "a b\nc"),
            ("v: |+\n  a\n\n", "a\n\n"),
            ("v: |2-\n  a\n    indented\n", "a\n  indented"),
            ("v: \"A\\n\\u263A\\U0001F600\"\n", "A\n☺😀"),
            ("v: 'it''s quoted'\n", "it's quoted"),
            ("v: a\n  continued\n\n  line\n", "a continued\nline"),
        })
        {
            var parsed = Parse(yaml, portable);
            Check(parsed.Status == MarkdownParseStatus.Complete && parsed.Root!.Entries[0].Value.Value == expected,
                "Scalar decoding: " + yaml.Replace("\n", "\\n"));
        }

        foreach (var yaml in new[] { "a: 1\na: 2\n", "a: 1\n\"a\": 2\n" })
        {
            var duplicate = Parse(yaml, portable);
            Check(duplicate.Status == MarkdownParseStatus.Partial && duplicate.Root!.Entries.Count == 2
                && duplicate.Diagnostics.Any(diagnostic => diagnostic.Reason == "duplicateKey"), "Duplicate entries remain available and cannot masquerade as valid typed metadata");
        }
        foreach (var (yaml, reason) in new[]
        {
            ("a: &anchor value\n", "alias"), ("a: *anchor\n", "alias"), ("a: !!str value\n", "tag"),
            ("%YAML 1.2\n---\na: b\n", "directive"), ("a: [1, 2\n", "syntax"),
            ("a: \"unterminated\n", "syntax"), ("a: 1\n---\nb: 2\n", "multipleDocument"),
        })
        {
            var rejected = Parse(yaml, portable);
            Check(rejected.Status != MarkdownParseStatus.Complete && rejected.RawText == yaml
                && rejected.Diagnostics.Any(diagnostic => diagnostic.Reason == reason), "Unsupported/invalid YAML remains raw with reason " + reason);
        }
        var mergeName = Parse("\"<<\": ordinary\nwhen: 2026-11-13T12:00:00Z\nyes: yes\n", portable);
        Check(mergeName.Status == MarkdownParseStatus.Complete && mergeName.Root!.Entries.All(entry => entry.Value.ScalarKind == YamlScalarKind.String),
            "An ordinary << key, timestamps and YAML 1.1 booleans do not gain unsupported semantics");
        Check(Parse("[a, {b: c}]", portable).Root!.Kind == MarkdownYamlKind.Sequence && Parse("plain", portable).Root!.Value == "plain",
            "Standalone YAML permits sequence and scalar roots");

        var bytes = new UTF8Encoding(false, true).GetBytes(input);
        var original = (byte[])bytes.Clone();
        var utf8 = YamlParser.ParseUtf8(bytes);
        bytes[0] = 0;
        var returned = utf8.GetOriginalUtf8()!;
        Check(returned.SequenceEqual(original) && utf8.RawText == input, "UTF-8 BOM, comments, order and CRLF are preserved independently of the caller byte array");
        returned[0] = 0;
        Check(utf8.GetOriginalUtf8()!.SequenceEqual(original), "Returned UTF-8 copies cannot mutate the snapshot");
        var badEncoding = YamlParser.ParseUtf8(new byte[] { 0xC0, 0xAF });
        Check(badEncoding.Status == MarkdownParseStatus.Failed && badEncoding.Diagnostics[0].Kind == YamlDiagnosticKind.InvalidEncoding,
            "Invalid UTF-8 is rejected without replacement characters");

        foreach (var (yaml, options, reason) in new[]
        {
            ("a: b", new YamlParseOptions(maxInputBytes: 3), "bytes"),
            ("a: b", new YamlParseOptions(maxInputUtf16: 3), "characters"),
            ("a: [[b]]", new YamlParseOptions(maxNestingDepth: 2), "depth"),
            ("a: b", new YamlParseOptions(maxNodes: 2), "nodes"),
            ("a: long", new YamlParseOptions(maxScalarUtf16: 3), "scalar"),
            ("a: b", new YamlParseOptions(maxScanUnits: 2), "scan"),
        })
        {
            var limited = YamlParser.Parse(yaml, options);
            Check(limited.Status != MarkdownParseStatus.Complete && limited.Diagnostics.Any(diagnostic => diagnostic.Kind == YamlDiagnosticKind.ResourceLimit && diagnostic.Reason == reason),
                "YAML limit: " + reason);
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var interrupted = YamlParser.Parse(input, cancellationToken: canceled.Token);
        Check(interrupted.Status == MarkdownParseStatus.Failed && interrupted.Diagnostics.Single().Kind == YamlDiagnosticKind.Canceled,
            "Standalone YAML cancellation is a distinct result");
        var frontMatter = MarkdownParser.Parse("---\na: 1\na: 2\n---\n# Body\n", "yaml", "duplicates.md");
        Check(frontMatter.FrontMatter.State == MarkdownFrontMatterState.Invalid && frontMatter.FrontMatter.Root!.Children.Count == 4
            && frontMatter.Headings.Count == 1, "Invalid frontmatter retains duplicate syntax and still analyzes the delimited body");
        var syntax = frontMatter.FrontMatter.GetYamlSyntax()!;
        Check(syntax.RawText == "a: 1\na: 2\n" && syntax.Root!.Entries.Count == 2 && syntax.Diagnostics[0].Reason == "duplicateKey"
            && syntax.Root.Entries[1].Key.RawSpan == new RawSpan(5, 1), "The same frontmatter parse exposes rich YAML syntax with header-local UTF-16 ranges");
        Check(Parse("[a: 1, a: 2]", portable).Status == MarkdownParseStatus.Complete, "Separate implicit flow mappings have independent key sets");
        Check(Parse("it's: fine\n", portable).Root!.Entries[0].Key.Value == "it's", "Quotes within a plain key are data");
        Check(Parse("a: |\n  \ttext\n", portable).Root!.Entries[0].Value.Value == "\ttext\n", "A tab after the required block indentation is scalar content");
        foreach (var yaml in new[] { "true: a\nTrue: b\n", "1: a\n01: b\n", "0o13: a\n0xB: b\n", "1.0: a\n1e0: b\n" })
            Check(Parse(yaml, portable).Diagnostics.Any(d => d.Reason == "duplicateKey"), "Scalar key equality uses Core tags and canonical values");
        Check(Parse("1: number\n\"1\": string\n", portable).Status == MarkdownParseStatus.Complete, "Integer and string mapping keys remain different");
        foreach (var yaml in new[] { "a: \0\n", "a: \"\0\"\n" })
            Check(Parse(yaml, portable).Diagnostics.Any(d => d.Reason == "character"), "Raw NUL is invalid YAML source");
        Check(Parse("a: +0x2A\nb: -0o7\n", portable).Root!.Entries.All(entry => entry.Value.ScalarKind == YamlScalarKind.String), "Core hexadecimal/octal resolution does not accept a sign");
        Console.WriteLine("Native YAML: PASS (runtime/netstandard syntax, scalar semantics, UTF-8 retention, diagnostics and limits).");
    }

    private static void OfficialFixtures(Assembly portable)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Yaml");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var id = fixture.GetProperty("id").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(directory, id, "in.yaml"));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            Check(hash == fixture.GetProperty("inputSha256").GetString(), id + ": pinned upstream input bytes");
            var parsed = Parse(new UTF8Encoding(false, true).GetString(bytes), portable);
            switch (fixture.GetProperty("expectation").GetString())
            {
                case "complete":
                    Check(parsed.Status == MarkdownParseStatus.Complete, id + ": upstream YAML must parse: " + string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
                    var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, id, "in.json")));
                    Check(JsonNode.DeepEquals(expected, JsonSerializer.SerializeToNode(Project(parsed.Root!))), id + ": upstream JSON tree/scalar expectation");
                    break;
                case "syntax":
                    Check(parsed.Status != MarkdownParseStatus.Complete && parsed.Diagnostics.Any(d => d.Kind == YamlDiagnosticKind.Syntax), id + ": upstream invalid syntax");
                    break;
                case "unsupported":
                    Check(parsed.Status != MarkdownParseStatus.Complete && parsed.Diagnostics.Any(d => d.Kind == YamlDiagnosticKind.Unsupported), id + ": explicit profile exclusion");
                    break;
            }
        }
    }

    private static object? Project(YamlNode node) => node.Kind switch
    {
        MarkdownYamlKind.Mapping => node.Entries.ToDictionary(entry => entry.Key.Value!, entry => Project(entry.Value), StringComparer.Ordinal),
        MarkdownYamlKind.Sequence => node.Children.Select(Project).ToArray(),
        _ => node.ScalarKind == YamlScalarKind.Null ? null : node.Value,
    };

    private static YamlDocument Parse(string raw, Assembly portable)
    {
        var runtime = YamlParser.Parse(raw);
        var counterpart = portable.GetType("Syntamark.YamlParser", true)!.GetMethod("Parse")!.Invoke(null,
            new object?[] { raw, null, CancellationToken.None })!;
        Check(JsonSerializer.Serialize(runtime) == JsonSerializer.Serialize(counterpart, counterpart.GetType()), "Runtime/netstandard YAML contract equality");
        return runtime;
    }

    private static IEnumerable<YamlNode> Nodes(YamlNode node)
    {
        yield return node;
        foreach (var child in node.Children.Concat(node.Entries.SelectMany(entry => new[] { entry.Key, entry.Value })))
            foreach (var descendant in Nodes(child))
                yield return descendant;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
