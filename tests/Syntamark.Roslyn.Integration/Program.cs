using System.Collections.Immutable;
using System.Reflection;
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
var snapshot = new SnapshotFile("original.md", raw);
var first = await Run([snapshot]);
var todo = first.Single(d => d.Id == "SMC001");
var expectedStart = raw.IndexOf("TODO", StringComparison.Ordinal);
Check(todo.Location.SourceSpan == new TextSpan(expectedStart, "TODO &amp;😀".Length), "BOM/CRLF/entity/emoji raw span");
Check(todo.Location.GetLineSpan().StartLinePosition == new LinePosition(3, 2), "Roslyn zero-based UTF-16 line/column");
Check(todo.Properties["precision"] == "CoveringTokens", "Entity is token-covering, not an exact edit");
var identity = first.Single(d => d.Id == "SMC000");
Check(identity.Properties["yamlVersion"]?.StartsWith("18.1.0", StringComparison.Ordinal) == true, "Actual YamlDotNet informational version");
Check(identity.Properties["yamlAssembly"] == "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e", "Actual YamlDotNet assembly identity");
Check(identity.Properties["status"] == "Complete", "YAML front matter actually parsed in source host");
if (args.Contains("--candidate"))
    Check(identity.Properties["emptyTargetSpans"] == "42:0", "Candidate empty destination has a known nonzero position");

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
    yaml = identity.Properties["yamlVersion"],
    location = new { todo.Location.SourceSpan.Start, todo.Location.SourceSpan.Length, line = 3, column = 2 },
    cases = "snapshot/edit/delete/rename/exclusion/options/unreadable/partial/multifragment/parallel/pre-cancel/cancel-at-read",
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
sealed class GlobalOptions(string? scan) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        value = scan ?? "";
        return scan is not null && key == "build_property.SyntamarkMaxScanUnits";
    }
}
sealed class ConfigProvider(GlobalOptions options) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions => options;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new GlobalOptions(null);
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new GlobalOptions(null);
}
