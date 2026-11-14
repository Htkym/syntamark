using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Syntamark.Samples;

var analyzer = new MarkdownTodoAnalyzer();
var compilation = CSharpCompilation.Create("MarkdownCanary", [CSharpSyntaxTree.ParseText("class C {}")],
    [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var host = typeof(DiagnosticAnalyzer).Assembly.GetName();
var raw = "\uFEFF---\r\ntitle: 😀\r\n---\r\n# TODO &amp;😀\r\n[x]()\r\n";
raw += "\r\n<div>\r\n# TODO Hidden\r\n[bad](/wrong)\r\n</div>\r\n\r\ntext <span>**bold** [x](/u)</span>\r\n";
var snapshot = new SnapshotFile("original.md", raw);
var first = await Run([snapshot]);
var todo = first.Single(d => d.Id == "SMC001");
var expectedStart = raw.IndexOf("TODO", StringComparison.Ordinal);
Check(todo.Location.SourceSpan == new TextSpan(expectedStart, "TODO &amp;😀".Length), "BOM/CRLF/entity/emoji raw span");
Check(todo.Location.GetLineSpan().StartLinePosition == new LinePosition(3, 2), "Roslyn zero-based UTF-16 line/column");
Check(todo.Properties["precision"] == "CoveringTokens", "Entity is token-covering, not an exact edit");
var identity = first.Single(d => d.Id == "SMC000");
Check(identity.Properties["yamlProfileId"] == "native-yaml/1", "Actual native YAML profile identity");
Check(!identity.Properties["parserReferences"]!.Split(';').Any(name => name is "YamlDotNet" or "SkiaSharp"),
    "Compiler source host has no retired parser or image dependency");
Check(identity.Properties["status"] == "Complete" && identity.Properties["yamlProbeState"] == "Parsed",
    "Selected profile completed and the separate native YAML probe parsed front matter");
Check(identity.Properties["yamlProbeProfileId"] == "lithosharp-markdown/1", "YAML probe profile is explicit");
CheckAnalysis(identity, "Complete", "Complete");
Check(identity.Properties["emptyTargetSpans"] == "42:0", "Candidate empty destination has a known nonzero position");
Check(identity.Properties["profileId"] == "commonmark/0.31.2" && identity.Properties["standardFrontMatterState"] == "Absent",
    "Candidate standard parse does not implicitly consume YAML");
Check(identity.Properties["syntaxState"] == "Document" && int.Parse(identity.Properties["syntaxNodes"]!) > 0,
    "Candidate compiled and loaded the internal syntax entry");
Check(identity.Properties["syntaxRootSpan"] == "0:" + raw.Length, "Candidate syntax owns the original BOM/CRLF snapshot");
var marker = raw.IndexOf("# TODO", StringComparison.Ordinal);
var headingLength = raw.IndexOf('\r', marker) - marker;
Check(identity.Properties["syntaxHeadingSpans"]!.Split(';').Contains(marker + ":" + headingLength),
    "TODO rule selects the raw heading range from the internal tree");
Check(identity.Properties["htmlSyntaxNodes"] == "3" && first.Count(d => d.Id == "SMC001") == 1,
    "HTML block/inline syntax is present and hidden HTML heading is not diagnosed");
Check(!identity.Properties["linkTargets"]!.Contains("/wrong", StringComparison.Ordinal)
    && identity.Properties["linkTargets"]!.Contains("/u", StringComparison.Ordinal), "HTML content has no fake link facts while inline Markdown remains parsed");

var edited = await Run([new SnapshotFile("original.md", "# Done\n")]);
Check(edited.All(d => d.Id != "SMC001"), "Edited snapshot clears old TODO");
var renamed = (await Run([new SnapshotFile("renamed.md", "# TODO\n")])).Single(d => d.Id == "SMC001");
Check(renamed.Location.GetLineSpan().Path == "renamed.md", "Renamed file owns location");
Check((await Run([])).IsEmpty, "Removed AdditionalFile clears diagnostics");
Check((await Run([new SnapshotFile("outside.txt", "# TODO\n")])).IsEmpty, "Excluded extension clears diagnostics");
var unreadable = await Run([new SnapshotFile("missing.md", null)]);
Check(unreadable.Length == 1 && unreadable[0].Id == "SMC002" && unreadable[0].Location == Location.None,
    "Unreadable AdditionalFile remains distinct from empty input");
var partial = await Run([snapshot], new GlobalOptions("1"));
Check(partial.Single(d => d.Id == "SMC000").Properties["status"] == "Partial" && partial.All(d => d.Id != "SMC001"),
    "Budget stop cannot fabricate missing facts or absence diagnostics");
var partialIdentity = partial.Single(d => d.Id == "SMC000");
Check(partialIdentity.Properties["syntaxState"] == "unavailable", "Early scan stop does not fabricate an empty tree");
CheckAnalysis(partialIdentity, "Partial", "Unavailable");
var outputLimited = await Run([snapshot], new GlobalOptions(null, "1"));
var outputIdentity = outputLimited.Single(d => d.Id == "SMC000");
Check(outputIdentity.Properties["status"] == "Partial"
    && outputIdentity.Properties["syntaxState"] == "unavailable"
    && outputLimited.All(d => d.Id != "SMC001"), "Output stop preserves partial/null-root handling in the compiler consumer");
CheckAnalysis(outputIdentity, "Partial", "Unavailable");
var failed = await Run([new SnapshotFile("invalid.md", "\uD800")]);
var failedIdentity = failed.Single(d => d.Id == "SMC000");
Check(failedIdentity.Properties["status"] == "Failed" && failedIdentity.Properties["standardFrontMatterState"] == "Unknown"
    && failedIdentity.Properties["syntaxState"] == "unavailable" && failed.All(d => d.Id != "SMC001"),
    "Failed input keeps unavailable outputs explicit while diagnostic properties remain readable");
CheckAnalysis(failedIdentity, "Unavailable", "Unavailable");
Check((await Run([snapshot])).Count(d => d.Id == "SMC001") == 1, "Options change does not cache a partial snapshot");

var fragments = (await Run([new SnapshotFile("quote.md", "> TODO\r\n> continuation\r\n> ===\r\n")]))
    .Single(d => d.Id == "SMC001");
Check(fragments.Location.SourceSpan.Start == 2 && fragments.AdditionalLocations.Count == 1,
    "Stripped quote prefix stays as separate locations");
Check(fragments.AdditionalLocations[0].SourceSpan.Start == 10, "Second raw fragment excludes quote marker");
var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Run([snapshot])));
Check(parallel.All(result => result.Single(d => d.Id == "SMC001").Location.SourceSpan == todo.Location.SourceSpan),
    "Concurrent document analysis is deterministic");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    await Canceled(() => Run([snapshot], cancellationToken: canceled.Token));
}
using (var cancelAtRead = new CancellationTokenSource())
{
    await Canceled(() => Run([new SnapshotFile("cancel.md", "# TODO", cancelAtRead)], cancellationToken: cancelAtRead.Token));
}
var assembly = typeof(MarkdownTodoAnalyzer).Assembly;
Check(assembly.GetTypes().Count(t => t.FullName == "Syntamark.Compilation.MdParser") == 1
    && assembly.GetType("Syntamark.MarkdownParser") is null, "Source compiled once, runtime facade absent");
Check(assembly.GetType("Syntamark.Compilation.SourceText") is not null
    && typeof(SourceText).FullName == "Microsoft.CodeAnalysis.Text.SourceText", "Both SourceText types coexist");
Check(assembly.GetTypes().Count(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit") == 1,
    "Source payload IsExternalInit is defined once");
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS",
    host = host.FullName,
    target = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName,
    parser = identity.Properties["parserVersion"],
    yaml = identity.Properties["yamlProfileId"],
    profileId = identity.Properties["profileId"],
    analysis = new { parts = identity.Properties["analysisParts"], hash = identity.Properties["analysisHash"], schemaVersion = identity.Properties["analysisSchemaVersion"] },
    syntax = new { state = identity.Properties["syntaxState"], nodes = identity.Properties["syntaxNodes"], htmlNodes = identity.Properties["htmlSyntaxNodes"], rootSpan = identity.Properties["syntaxRootSpan"] },
    yamlProbe = new { profileId = identity.Properties["yamlProbeProfileId"], state = identity.Properties["yamlProbeState"], status = identity.Properties["yamlProbeStatus"] },
    location = new { todo.Location.SourceSpan.Start, todo.Location.SourceSpan.Length, line = 3, column = 2 },
    cases = "snapshot/edit/delete/rename/exclusion/options/unreadable/partial/multifragment/parallel/pre-cancel/cancel-at-read" + "/standard-syntax/HTML/source-span/output-budget/native-YAML-probe/selective-analysis-75/failed-input-availability",
}));

async Task<ImmutableArray<Diagnostic>> Run(ImmutableArray<AdditionalText> files, GlobalOptions? global = null,
    CancellationToken cancellationToken = default)
{
    var options = new AnalyzerOptions(files, new ConfigProvider(global ?? new GlobalOptions(null)));
    var driver = compilation.WithAnalyzers([analyzer], new CompilationWithAnalyzersOptions(options,
        onAnalyzerException: (exception, _, _) => throw new InvalidOperationException("Analyzer exception", exception),
        concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));
    return await driver.GetAnalyzerDiagnosticsAsync(cancellationToken);
}

static async Task Canceled(Func<Task<ImmutableArray<Diagnostic>>> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("Cancellation returned successful diagnostics");
    }
    catch (OperationCanceledException) { }
}
static void CheckAnalysis(Diagnostic identity, string expectedFactsAvailability, string expectedSyntaxAvailability)
{
    Check(identity.Properties["analysisParts"] == "75" && identity.Properties["analysisSchemaVersion"] == "1"
        && identity.Properties["analysisContractVersion"] == "analysis/2",
        "Candidate source consumer requests FrontMatter, Headings, Links and Syntax explicitly");
    var optionsHash = identity.Properties["analysisOptionsHash"];
    Check(optionsHash is { Length: 64 }, "Candidate records actual options identity");
    var canonical = "{\"analysisSchemaVersion\":1,\"optionsHash\":\"" + optionsHash + "\",\"requestedParts\":75}";
    var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    Check(identity.Properties["analysisHash"] == expectedHash, "Candidate analysis identity includes the actual request and options");
    foreach (var part in new[] { "FrontMatter", "Headings", "Links" })
        Check(identity.Properties["analysis" + part + "Availability"] == expectedFactsAvailability,
            "Candidate requested availability: " + part);
    Check(identity.Properties["analysisSyntaxAvailability"] == expectedSyntaxAvailability, "Candidate syntax availability is explicit");
    foreach (var part in new[] { "Sections", "Fences", "TextRegions" })
        Check(identity.Properties["analysis" + part + "Availability"] == "NotRequested", "Candidate omitted availability: " + part);
}
static void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

sealed class SnapshotFile(string path, string? text, CancellationTokenSource? cancelAtRead = null) : AdditionalText
{
    public override string Path => path;
    public override SourceText? GetText(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = text is null ? null : SourceText.From(text, Encoding.UTF8);
        cancelAtRead?.Cancel();
        return result;
    }
}
sealed class GlobalOptions(string? scan, string? output = null) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        var selected = key switch
        {
            "build_property.SyntamarkMaxScanUnits" => scan,
            "build_property.SyntamarkMaxOutputItems" => output,
            _ => null,
        };
        value = selected ?? "";
        return selected is not null;
    }
}
sealed class ConfigProvider(GlobalOptions options) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions => options;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new GlobalOptions(null);
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new GlobalOptions(null);
}
