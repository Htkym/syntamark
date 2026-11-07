using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syntamark;

internal static class BodyAnalysisChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const MarkdownAnalysisParts Parts = MarkdownAnalysisParts.FrontMatter | MarkdownAnalysisParts.Headings
        | MarkdownAnalysisParts.Links | MarkdownAnalysisParts.Syntax;
    private const string Markers = "---\r\nTitle 😀\r\n---\r\n\r\n# Tail\r\n";
    private const string Snapshot = "\uFEFF---\r\n\r\n# Tail &amp; \\*😀\r\n\r\n[go](doc😀.md)\r\n";

    internal static void Run(Assembly portable)
    {
        var bodyOptions = new MarkdownParseOptions(MarkdownInputKind.Body);
        foreach (var raw in new[] { Markers, Snapshot })
        {
            var body = MarkdownParser.Analyze(raw, Parts, "body", "input.md", options: bodyOptions);
            Check(body.Status == MarkdownParseStatus.Complete && body.Root!.GetRawText() == raw && body.BodySpan == new RawSpan(0, raw.Length)
                && body.FrontMatter is { State: MarkdownFrontMatterState.Absent, OpeningSpan: null, YamlSpan: null, ClosingSpan: null, Root: null }
                && !body.Diagnostics.Any(diagnostic => diagnostic.Id is "LMD004" or "LMD005"),
                "Body input must preserve the complete snapshot without recognizing a front matter envelope.");
            var counterpart = Analyze(portable, false, raw, Parts, Options(portable, false));
            Check(JsonSerializer.Serialize(body) == JsonSerializer.Serialize(counterpart, counterpart.GetType()),
                "Runtime and netstandard Body results must match, including original positions and identities.");
            if (raw == Markers)
            {
                Check(body.Root!.Children[0].Kind == MarkdownSyntaxKind.ThematicBreak
                    && body.Root.Children[0].RawSpan == new RawSpan(0, 3)
                    && body.Headings!.Select(heading => heading.RawLevel).SequenceEqual(new[] { 2, 1 })
                    && body.Headings![0].RawSpan == At(raw, "Title 😀\r\n---"),
                    "Leading delimiter lines must remain Markdown thematic and setext syntax in Body mode.");
            }
            else
            {
                Check(body.Root!.Children[0].Kind == MarkdownSyntaxKind.Paragraph
                    && body.Root.Children[0].RawSpan == new RawSpan(0, 4)
                    && body.Headings!.Single().RawSpan == At(raw, "# Tail &amp; \\*😀")
                    && body.Headings!.Single().Text.Text == "Tail & *😀"
                    && body.Links!.Single().TargetRawSpan == At(raw, "doc😀.md")
                    && body.GetLinePosition(body.Links!.Single().RawSpan.Start) == new MarkdownLinePosition(5, 1),
                    "A leading BOM, CRLF and surrogate pairs must keep their original UTF-16 positions.");
            }
        }

        var ordinary = MarkdownParser.Analyze(Snapshot, Parts, "body", "input.md");
        foreach (var options in new[] { new MarkdownParseOptions(), new MarkdownParseOptions(MarkdownFrontMatterRecovery.None),
            new MarkdownParseOptions(MarkdownInputKind.Document) })
        {
            var document = MarkdownParser.Analyze(Snapshot, Parts, "body", "input.md", options: options);
            Check(options.InputKind == MarkdownInputKind.Document && document.OptionsHash == ordinary.OptionsHash
                && document.AnalysisHash == ordinary.AnalysisHash,
                "The existing constructors and explicit Document mode must retain default cache identities.");
        }
        var bodyResult = MarkdownParser.Analyze(Snapshot, Parts, "body", "input.md", options: bodyOptions);
        var recovery = MarkdownParser.Analyze(Snapshot, Parts, "body", "input.md",
            options: new MarkdownParseOptions(MarkdownInputKind.Body, MarkdownFrontMatterRecovery.TreatAsBody));
        var canonical = "{\"inputKind\":\"body\",\"maxInputUtf16\":1048576,\"maxNestingDepth\":200,\"maxOutputItems\":131072,"
            + "\"maxScanUnits\":16777216,\"optionsSchemaVersion\":1,\"profileId\":\"lithosharp-markdown/1\"}";
        Check(bodyResult.OptionsHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()
            && bodyResult.OptionsHash != ordinary.OptionsHash && bodyResult.AnalysisHash != ordinary.AnalysisHash
            && JsonSerializer.Serialize(bodyResult) == JsonSerializer.Serialize(recovery),
            "Body identity must include inputKind and ignore recovery that has no effect on Body input.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var assembly in new[] { typeof(MarkdownParser).Assembly, portable })
        {
            foreach (var core in new[] { false, true })
            {
                Reject<ArgumentOutOfRangeException>(() => Options(assembly, core, inputKind: (MarkdownInputKind)(-1)));
                Reject<ArgumentOutOfRangeException>(() => Options(assembly, core, recovery: (MarkdownFrontMatterRecovery)(-1)));
                Reject<OperationCanceledException>(() => Analyze(assembly, core, Snapshot, Parts, Options(assembly, core), canceled.Token));
            }
            RenderingLimits(assembly);
        }
        Console.WriteLine("Body analysis: PASS (runtime/netstandard, snapshot positions, identities, rendering limits, cancellation).");
    }

    private static void RenderingLimits(Assembly assembly)
    {
        const string raw = "# Guide\n\nA paragraph.\n";
        const MarkdownAnalysisParts parts = MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Syntax;
        var projected = Analyze(assembly, true, raw, parts, Options(assembly, true, maxOutputItems: 4));
        var blocks = Items(Get(projected, "Blocks")!).ToArray();
        var heading = Items(Get(projected, "Headings")!).Single();
        Check(Get(projected, "Status")!.ToString() == "Partial" && HasLimit(projected, "output")
            && Get(projected, "Root") is null && (string?)Get(Get(heading, "Text")!, "Text") == "Guide"
            && blocks.Length == 2 && blocks[0].GetType().Name == "LithoHeading" && blocks[1].GetType().Name == "LithoParagraph"
            && (int)Get(Get(blocks[1], "Span")!, "Start")! == raw.IndexOf("A paragraph.", StringComparison.Ordinal),
            "Completed rendering blocks and known heading facts must survive a later syntax output limit.");

        var interrupted = Analyze(assembly, true, raw, parts, Options(assembly, true, maxScanUnits: 1));
        Check(Get(interrupted, "Status")!.ToString() == "Partial" && HasLimit(interrupted, "scan")
            && Get(interrupted, "Blocks") is null && !Items(Get(interrupted, "Headings")!).Any(),
            "An interrupted block parse must not publish incomplete rendering blocks.");
    }

    private static bool HasLimit(object result, string reason) => Items(Get(result, "Diagnostics")!).Any(diagnostic =>
        (string?)Get(diagnostic, "Id") == "LMD003" && (string?)Get(diagnostic, "Reason") == reason);

    private static object Options(Assembly assembly, bool core, MarkdownInputKind inputKind = MarkdownInputKind.Body,
        MarkdownFrontMatterRecovery recovery = MarkdownFrontMatterRecovery.None, int maxOutputItems = 131072, int maxScanUnits = 16777216)
    {
        var prefix = core ? "Syntamark.Compilation.Md" : "Syntamark.Markdown";
        return assembly.GetType(prefix + (core ? "Options" : "ParseOptions"), true)!.GetConstructors(Instance)
            .Single(constructor => constructor.GetParameters().Length == 8).Invoke(new[]
            {
                Enum.ToObject(assembly.GetType(prefix + "InputKind", true)!, (int)inputKind),
                Enum.ToObject(assembly.GetType(prefix + "FrontMatterRecovery", true)!, (int)recovery),
                (object)1048576, maxOutputItems, 200, maxScanUnits, "lithosharp-markdown/1", 1,
            });
    }

    private static object Analyze(Assembly assembly, bool core, string raw, MarkdownAnalysisParts parts, object options,
        CancellationToken cancellationToken = default)
    {
        var prefix = core ? "Syntamark.Compilation.Md" : "Syntamark.Markdown";
        return assembly.GetType(prefix + "Parser", true)!
            .GetMethod(core ? "AnalyzeWithBlocks" : "Analyze", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { (object)raw, Enum.ToObject(assembly.GetType(prefix + "AnalysisParts", true)!, (int)parts),
                "body", "input.md", null, options, cancellationToken })!;
    }

    private static RawSpan At(string raw, string token) => new(raw.IndexOf(token, StringComparison.Ordinal), token.Length);
    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();

    private static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (TargetInvocationException error) when (error.InnerException is T)
        {
            return;
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
