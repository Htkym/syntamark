using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Syntamark;
using SyntaDocument = Syntamark.MarkdownDocument;

if (args.Length is not (3 or 4) || args[0] is not ("A" or "B") || args.Length == 4 && args[3] != "--requested-common")
    throw new ArgumentException("Usage: <A|B engine order> <run label> <new-results.json> [--requested-common]");
var requestedMode = args.Length == 4;
#if !SYNTAMARK_ANALYSIS
if (requestedMode)
    throw new InvalidOperationException("Requested mode requires a compatible candidate DLL and -p:SyntamarkAnalysis=true.");
#endif
if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
    throw new InvalidOperationException("Run independent Release processes with DOTNET_TieredCompilation=0.");
var order = args[0] == "A" ? new[] { "Syntamark", "Markdig" } : new[] { "Markdig", "Syntamark" };
var builder = new MarkdownPipelineBuilder().UsePreciseSourceLocation();
var pipeline = builder.Build();
Check(pipeline.Extensions.Count == 0, "Pure CommonMark pipeline required.");
var options = new MarkdownParseOptions(maxInputUtf16: 1048576, maxOutputItems: 1048576,
    maxNestingDepth: 200, maxScanUnits: 134217728, profileId: "commonmark/0.31.2");
var fixtures = new[] { CreateFixture("small", 1, 100), CreateFixture("medium", 24, 8), CreateFixture("larger", 96, 2) };
var validation = new List<object>();
foreach (var fixture in fixtures)
{
    var synta = ParseFacts(fixture.Raw);
    var syntax = ParseSyntax(fixture.Raw);
    Check(syntax.Root?.RawSpan == new RawSpan(0, fixture.Raw.Length), "Owned syntax root envelope.");
    var markdig = Markdig.Markdown.Parse(fixture.Raw, pipeline);
    var expected = JsonSerializer.Serialize(fixture.Expected);
    var actualSynta = FromSyntamark(synta);
    var actualMarkdig = FromMarkdig(markdig);
    Check(JsonSerializer.Serialize(actualSynta) == expected, fixture.Id + ": Syntamark common facts differ.\n" + JsonSerializer.Serialize(actualSynta));
    Check(JsonSerializer.Serialize(actualMarkdig) == expected, fixture.Id + ": Markdig common facts differ.\n" + JsonSerializer.Serialize(actualMarkdig));
    var expectedLocations = fixture.Expected.Ranges().Select(range => ExpectedLocation(fixture.Raw, range)).ToArray();
    foreach (var facts in new[] { actualSynta, actualMarkdig })
        Check(JsonSerializer.Serialize(Locations(fixture.Snapshot, facts).Select(LocationFact.From).ToArray()) == JsonSerializer.Serialize(expectedLocations),
            fixture.Id + ": independent UTF-16 line/column oracle differs.");
    object? selectedAnalysis = null;
#if SYNTAMARK_ANALYSIS
    if (requestedMode)
    {
        var analyzed = ParseRequested(fixture.Raw);
        ValidateRequested(analyzed, synta);
        var selected = FromRequested(analyzed);
        Check(JsonSerializer.Serialize(selected) == expected, fixture.Id + ": requested common facts differ.");
        Check(JsonSerializer.Serialize(Locations(fixture.Snapshot, selected).Select(LocationFact.From).ToArray()) == JsonSerializer.Serialize(expectedLocations),
            fixture.Id + ": requested UTF-16 location oracle differs.");
        selectedAnalysis = DescribeRequested(analyzed);
    }
#endif
    validation.Add(new
    {
        fixture.Id,
        utf16Length = fixture.Raw.Length,
        inputSha256 = Hash(Encoding.UTF8.GetBytes(fixture.Raw)),
        expectedCommonFactsSha256 = Hash(Encoding.UTF8.GetBytes(expected)),
        expectedLocationsSha256 = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(expectedLocations))),
        headings = fixture.Expected.Headings.Length,
        links = fixture.Expected.Links.Length,
        fencedCode = fixture.Expected.Code.Length,
        locations = expectedLocations.Length,
        status = synta.Status.ToString(),
        resourceDiagnostics = synta.Diagnostics.Count(d => d.Origin == "resource"),
        rawCoverage = synta.Coverage.RawPositions.ToString(),
        mappingCoverage = synta.Coverage.DecodedMapping.ToString(),
        synta.ParserVersion,
        synta.OptionsHash,
        selectedAnalysis,
        extraSyntamarkFacts = requestedMode ? null : new
        {
            sections = synta.Sections.Count,
            textRegions = synta.TextRegions.Count,
            referenceDefinitions = synta.Links.Count(l => l.Kind == MarkdownLinkKind.ReferenceDefinition),
            sourceSegments = synta.Headings.Sum(h => h.Text.SourceSegments.Count)
        },
        fullReferenceExtrasOutsideTiming = requestedMode ? new
        {
            sections = synta.Sections.Count,
            textRegions = synta.TextRegions.Count,
            referenceDefinitions = synta.Links.Count(l => l.Kind == MarkdownLinkKind.ReferenceDefinition),
            sourceSegments = synta.Headings.Sum(h => h.Text.SourceSegments.Count)
        } : null,
        snapshotType = fixture.Snapshot.GetType().FullName,
        snapshotToStringReusesOriginal = ReferenceEquals(fixture.Raw, fixture.Snapshot.ToString()),
        independentOracles = "Constructed from unique raw tokens, explicit decoded values, and a separate raw UTF-16 CR/LF scanner before any timing.",
    });
}

var results = new List<Measurement>();
var retained = new List<object>();
foreach (var fixture in fixtures)
{
    foreach (var stage in requestedMode ? new[] { "common-facts", "snapshot-to-locations" }
        : new[] { "representation-unequal-work", "common-facts", "snapshot-to-locations" })
        foreach (var engine in order)
        {
            Func<object> operation = stage switch
            {
                "representation-unequal-work" => engine == "Syntamark" ? () => ParseSyntax(fixture.Raw)
                    : () => Markdig.Markdown.Parse(fixture.Raw, pipeline),
                "common-facts" => () => Common(engine, fixture.Raw),
                _ => () => EndToEnd(engine, fixture.Snapshot),
            };
            Measure(fixture.Id, engine, stage, fixture.Operations, operation);
            retained.Add(Retained(fixture.Id, engine, stage, operation));
        }
    if (!requestedMode)
    {
        // This feature has no Markdig adapter and never appears in the common contract timings.
        var projection = ParseFacts(fixture.Raw).Headings[0].Text;
        var entityAt = projection.Text!.IndexOf('&');
        var mapped = projection.Map(new RawSpan(entityAt, 1));
        var entityRaw = fixture.Raw.IndexOf("&amp;", StringComparison.Ordinal);
        Check(mapped.Precision == MarkdownMappingPrecision.CoveringTokens && mapped.RawFragments.Single() == new RawSpan(entityRaw, 5),
            "Separate decoded substring Map oracle.");
        Measure(fixture.Id, "Syntamark-only", "additional-preparsed-substring-map", 10000,
            () => projection.Map(new RawSpan(entityAt, 1)));
    }
}

Sink.Value = null;
var identity = ParseFacts(fixtures[0].Raw);
var output = new
{
    contract = requestedMode ? "bounded-requested-common-facts-and-roslyn-locations/1" : "bounded-common-facts-and-roslyn-locations/1",
    requestedCommon = requestedMode,
    label = args[1],
    processOrder = args[0],
    engineOrder = order,
    timestampUtc = DateTime.UtcNow,
    processId = Environment.ProcessId,
    framework = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
    processors = Environment.ProcessorCount,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    serverGc = System.Runtime.GCSettings.IsServerGC,
    loadContext = "Non-dedicated workstation; other agent/build activity and OS scheduling are uncontrolled. No wall-clock CI gate.",
    samplesPerWorkload = 5,
    warmupBatches = 1,
    coldMeasurements = "None. All reported timings follow validation and a complete untimed workload batch; not process-start or JIT latency.",
    snapshotLineIndex = "Shared immutable SourceText snapshots and their line indexes are warmed by preflight. Timed ToString materializes the genuine SubText; Location objects are new per operation.",
    memoryContract = "Retained managed heap delta after full collection, eight simultaneous outputs. Input/snapshot and shared pipelines are pre-existing roots. No native memory/RSS claim; small deltas are noisy.",
    stages = new[]
    {
        new { id = "representation-unequal-work", syntamark = "ParseWithSyntax: all facts + immutable syntax view", markdig = "Parse: AST + precise raw positions", comparison = "Unequal work; cannot rank as equivalent parse-only throughput." },
        new { id = "common-facts", syntamark = requestedMode ? "Analyze Headings|Links|Fences (26), project common records" : "Parse all facts, select common heading/link/fenced-code output", markdig = "Parse AST, traverse/decode/copy common output", comparison = requestedMode ? "Same validated common output and adapter costs. Reduced requested-output work compared with full Parse; diagnostics/mapping/resource contracts still differ from Markdig." : "Same validated output; adapter costs included. Syntamark still computes extra facts/mapping/diagnostics/budget checks; no claim of identical internal work." },
        new { id = "snapshot-to-locations", syntamark = requestedMode ? "SourceText.ToString -> requested common records -> Location[]" : "SourceText.ToString -> common-facts -> Location[]", markdig = "SourceText.ToString -> common-facts -> Location[]", comparison = "Same snapshot, output ranges and Roslyn UTF-16 line spans; SourceText constructed outside timed scope." },
    }.Where(stage => !requestedMode || stage.id != "representation-unequal-work"),
    decodedSubset = "Heading text and link labels/targets/titles; fenced code logical content with exactly one terminal line separator excluded. CRLF is normalized to LF in code only. No arbitrary decoded-substring mapping in either common stage.",
    syntamark = new { profile = options.ProfileId, identity.ParserVersion, identity.OptionsHash, assembly = AssemblyIdentity(typeof(Syntamark.MarkdownParser).Assembly), options },
    markdig = new
    {
        packageVersion = "1.5.1",
        assembly = AssemblyIdentity(typeof(Markdig.Markdown).Assembly),
        preciseSourceLocation = builder.PreciseSourceLocation,
        extensions = pipeline.Extensions.Select(e => e.GetType().FullName).ToArray(),
        pipeline.MaximumNestingDepth,
        resourceStatus = "No equivalent accounting/cancellation adapter. Fixed bounded fixtures validate normally; not an adversarial resource-contract test."
    },
    roslyn = new { packageVersion = "4.14.0", assembly = AssemblyIdentity(typeof(SourceText).Assembly), compilerHost = "Not measured. This is .NET 10 CLI application use of SourceText/Location." },
    yamlRuntimeDependency = new { packageVersion = "18.1.0", usedByFixtures = false },
    validation,
    results,
    retained,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
using (var stream = new FileStream(args[2], FileMode.CreateNew))
    JsonSerializer.Serialize(stream, output, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(JsonSerializer.Serialize(new { status = "COMPLETE", label = args[1], validation = "PASS", fixtures = fixtures.Length, workloads = results.Count, output = args[2] }));

SyntaDocument ParseFacts(string raw)
{
    var result = Syntamark.MarkdownParser.Parse(raw, "comparison-benchmark", "fixed.md", options: options);
    Complete(result);
    return result;
}
MarkdownSyntaxDocument ParseSyntax(string raw)
{
    var result = Syntamark.MarkdownParser.ParseWithSyntax(raw, "comparison-benchmark", "fixed.md", options: options);
    Complete(result.Facts);
    Check(result.Root is not null, "Complete syntax parse must have a root.");
    return result;
}
CommonFacts Common(string engine, string raw)
{
    if (engine != "Syntamark")
        return FromMarkdig(Markdig.Markdown.Parse(raw, pipeline));
#if SYNTAMARK_ANALYSIS
    if (requestedMode)
        return FromRequested(ParseRequested(raw));
#endif
    return FromSyntamark(ParseFacts(raw));
}
#if SYNTAMARK_ANALYSIS
MarkdownAnalysisResult ParseRequested(string raw)
{
    var result = Syntamark.MarkdownParser.Analyze(raw,
        MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links | MarkdownAnalysisParts.Fences,
        "comparison-benchmark", "fixed.md", options: options);
    Check(result.Status == MarkdownParseStatus.Complete && result.Diagnostics.All(d => d.Origin != "resource"),
        "Partial/resource-stopped requested parses cannot be benchmark results.");
    return result;
}
static void ValidateRequested(MarkdownAnalysisResult result, SyntaDocument full)
{
    Check(result.RequestedParts == (MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links | MarkdownAnalysisParts.Fences)
        && (int)result.RequestedParts == 26 && result.AnalysisSchemaVersion == 1, "Requested analysis identity differs.");
    Check(result.OptionsHash == full.OptionsHash && result.ProfileId == full.ProfileId && result.ScopeId == full.ScopeId
        && result.SourceId == full.SourceId && result.SourceVersion == full.SourceVersion && result.TextHash == full.TextHash,
        "Requested/full input and options identities differ.");
    Check(result.AnalysisHash.Length == 64 && result.AnalysisHash.All(Uri.IsHexDigit), "Missing request hash.");
    foreach (var part in new[] { MarkdownAnalysisParts.Headings, MarkdownAnalysisParts.Links, MarkdownAnalysisParts.Fences })
        Check(result.GetAvailability(part) == MarkdownAnalysisAvailability.Complete, "Requested category is not complete.");
    foreach (var part in new[] { MarkdownAnalysisParts.FrontMatter, MarkdownAnalysisParts.Sections, MarkdownAnalysisParts.TextRegions, MarkdownAnalysisParts.Syntax })
        Check(result.GetAvailability(part) == MarkdownAnalysisAvailability.NotRequested, "Unrequested availability differs.");
    Check(result.Headings is not null && result.Links is not null && result.Fences is not null
        && result.FrontMatter is null && result.Sections is null && result.TextRegions is null && result.Root is null,
        "Unrequested outputs must be null; selected outputs must be available.");
}
static object DescribeRequested(MarkdownAnalysisResult result) => new
{
    requestedParts = (int)result.RequestedParts,
    result.AnalysisSchemaVersion,
    result.AnalysisHash,
    result.OptionsHash,
    status = result.Status.ToString(),
    availability = Enum.GetValues<MarkdownAnalysisParts>().Where(part => (int)part is 1 or 2 or 4 or 8 or 16 or 32 or 64)
        .ToDictionary(part => part.ToString(), part => result.GetAvailability(part).ToString()),
    selectedCounts = new
    {
        headings = result.Headings!.Count,
        links = result.Links!.Count,
        fences = result.Fences!.Count,
        referenceDefinitions = result.Links!.Count(link => link.Kind == MarkdownLinkKind.ReferenceDefinition),
        headingSourceSegments = result.Headings!.Sum(heading => heading.Text.SourceSegments.Count)
    },
    unrequested = new { frontMatter = (string?)null, sections = (int?)null, textRegions = (int?)null, syntax = (string?)null },
    result.Coverage,
    resourceDiagnostics = result.Diagnostics.Count(diagnostic => diagnostic.Origin == "resource"),
    workDifference = "Only Headings, Links and Fences are published; null unrequested groups do not assert absence. Recognition and required dependency work, diagnostics and resource accounting remain.",
};
#endif
EndToEndResult EndToEnd(string engine, SourceText snapshot)
{
    var facts = Common(engine, snapshot.ToString());
    return new(facts, Locations(snapshot, facts));
}
void Measure(string fixture, string engine, string stage, int operations, Func<object> operation)
{
    Batch(operations, operation); // One complete untimed batch, in addition to preflight validation.
    var samples = new List<Sample>();
    for (var i = 0; i < 5; i++)
    {
        Sink.Value = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var start = Stopwatch.GetTimestamp();
        Batch(operations, operation);
        var ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        samples.Add(new(ns / operations, (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)operations,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2));
    }
    results.Add(new(fixture, engine, stage, operations, samples));
}
static void Batch(int count, Func<object> operation)
{
    for (var i = 0; i < count; i++)
        Sink.Value = operation();
}
[MethodImpl(MethodImplOptions.NoInlining)]
static object Retained(string fixture, string engine, string stage, Func<object> operation)
{
    Sink.Value = null;
    var cohort = new object[8];
    var before = GC.GetTotalMemory(true);
    for (var i = 0; i < cohort.Length; i++)
        cohort[i] = operation();
    var after = GC.GetTotalMemory(true);
    GC.KeepAlive(cohort);
    return new { fixture, engine, stage, outputs = cohort.Length, retainedBytes = after - before, bytesPerOutput = (after - before) / (double)cohort.Length };
}
static void Complete(SyntaDocument document) => Check(document.Status == MarkdownParseStatus.Complete
    && document.Diagnostics.All(d => d.Origin != "resource"), "Partial/resource-stopped parses cannot be benchmark results.");

static CommonFacts FromSyntamark(SyntaDocument document) => FromSyntamarkParts(document.Headings, document.Links, document.Fences);
#if SYNTAMARK_ANALYSIS
static CommonFacts FromRequested(MarkdownAnalysisResult result) => FromSyntamarkParts(
    result.Headings ?? throw new InvalidOperationException("Requested headings unavailable."),
    result.Links ?? throw new InvalidOperationException("Requested links unavailable."),
    result.Fences ?? throw new InvalidOperationException("Requested fences unavailable."));
#endif
static CommonFacts FromSyntamarkParts(IReadOnlyList<MarkdownHeading> headings, IReadOnlyList<MarkdownLink> links, IReadOnlyList<MarkdownFence> fences) => new(
    headings.Select(h => new HeadingFact(h.RawLevel, h.Text.Text!, RangeFact.From(h.RawSpan))).ToArray(),
    links.Where(l => l.Kind != MarkdownLinkKind.ReferenceDefinition && l.Resolution != MarkdownLinkResolution.UnresolvedReference)
        .Select(l => new LinkFact(l.Image, l.Label.Text!, l.Target!, l.Title, RangeFact.From(l.RawSpan), RangeFact.From(l.TargetRawSpan!.Value))).ToArray(),
    fences.Select(f => new CodeFact(f.Info!, CodeContent(f.Content.Text!), RangeFact.From(f.RawSpan))).ToArray());
static CommonFacts FromMarkdig(Markdig.Syntax.MarkdownDocument document)
{
    var headings = new List<HeadingFact>();
    var links = new List<LinkFact>();
    var code = new List<CodeFact>();
    foreach (var node in document.Descendants())
        switch (node)
        {
            case HeadingBlock h:
                headings.Add(new(h.Level, InlineText(h.Inline!), RangeFact.From(h.Span)));
                break;
            case LinkInline l:
                links.Add(new(l.IsImage, InlineText(l), l.Url!, l.Title, RangeFact.From(l.Span), RangeFact.From(l.UrlSpan)));
                break;
            case FencedCodeBlock f:
                var content = new StringBuilder();
                for (var i = 0; i < f.Lines.Count; i++)
                    content.Append(f.Lines.Lines[i].Slice).Append('\n');
                code.Add(new(f.Info!, CodeContent(content.ToString()), RangeFact.From(f.Span)));
                break;
        }
    return new(headings.ToArray(), links.ToArray(), code.ToArray());
}
static string InlineText(ContainerInline container)
{
    var text = new StringBuilder();
    foreach (var child in container)
        switch (child)
        {
            case LiteralInline literal:
                text.Append(literal.Content);
                break;
            case HtmlEntityInline entity:
                text.Append(entity.Transcoded);
                break;
            case CodeInline code:
                text.Append(code.Content);
                break;
            case LineBreakInline:
                text.Append('\n');
                break;
            case ContainerInline nested:
                text.Append(InlineText(nested));
                break;
            default:
                throw new NotSupportedException("Not part of the validated common subset: " + child.GetType().Name);
        }
    return text.ToString();
}
static string CodeContent(string value)
{
    var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
    return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
}
static Location[] Locations(SourceText snapshot, CommonFacts facts) => facts.Ranges().Select(range =>
{
    var span = new TextSpan(range.Start, range.Length);
    return Location.Create("fixed.md", span, snapshot.Lines.GetLinePositionSpan(span));
}).ToArray();
static LocationFact ExpectedLocation(string raw, RangeFact range) => new(range.Start, range.Length, PositionAt(raw, range.Start), PositionAt(raw, range.Start + range.Length));

static LineFact PositionAt(string raw, int offset)
{
    var line = 0;
    var column = 0;
    for (var i = 0; i < offset; i++)
    {
        if (raw[i] == '\r')
        {
            if (i + 1 < offset && raw[i + 1] == '\n')
                i++;
            line++;
            column = 0;
        }
        else if (raw[i] == '\n')
        {
            line++;
            column = 0;
        }
        else
            column++;
    }
    return new(line, column);
}
static Fixture CreateFixture(string id, int units, int operations)
{
    var raw = new StringBuilder();
    var headings = new List<HeadingFact>();
    var links = new List<LinkFact>();
    var code = new List<CodeFact>();
    for (var i = 0; i < units; i++)
    {
        var n = i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        var heading = "# Section " + n + " A&amp;\\*😀 `b  c`";
        var inline = "[G&amp;\\*😀 " + n + "](/guide/" + n + "?a=1&amp;b=2 \"Title " + n + "\")";
        var reference = "[Reference " + n + "][r" + n + "]";
        var destination = "/guide/" + n + "?a=1&amp;b=2";
        var refDestination = "/ref/" + n;
        var codeText = "var value" + n + " = \"😀 &amp;\";\r\nline" + n + "\\tail\r\n";
        var fence = "```csharp\r\n" + codeText + "```";
        var unit = heading + "\r\n\r\nText " + inline + " " + reference + " and `inline  " + n + "`.\r\n\r\n[r" + n + "]: "
            + refDestination + " \"Reference\"\r\n\r\n" + fence + "\r\n\r\n";
        var start = raw.Length;
        RangeFact Token(string token)
        {
            var at = unit.IndexOf(token, StringComparison.Ordinal);
            Check(at >= 0 && unit.IndexOf(token, at + 1, StringComparison.Ordinal) < 0, "Oracle token must occur once per unit.");
            return new(start + at, token.Length);
        }
        headings.Add(new(1, "Section " + n + " A&*😀 b  c", Token(heading)));
        links.Add(new(false, "G&*😀 " + n, "/guide/" + n + "?a=1&b=2", "Title " + n, Token(inline), Token(destination)));
        links.Add(new(false, "Reference " + n, refDestination, "Reference", Token(reference), Token(refDestination)));
        code.Add(new("csharp", CodeContent(codeText), Token(fence)));
        raw.Append(unit);
    }
    var input = raw.ToString();
    // A genuine SubText slice makes materialization part of the end-to-end workload.
    const string prefix = "excluded-prefix:";
    var snapshot = SourceText.From(prefix + input, Encoding.UTF8).GetSubText(new TextSpan(prefix.Length, input.Length));
    Check(snapshot.ToString() == input, "Snapshot must contain exactly the same raw fixture.");
    return new(id, input, new(headings.ToArray(), links.ToArray(), code.ToArray()), snapshot, operations);
}
static object AssemblyIdentity(Assembly assembly) => new
{
    name = assembly.FullName,
    informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    sha256 = Hash(File.ReadAllBytes(assembly.Location))
};
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed record RangeFact(int Start, int Length)
{
    internal static RangeFact From(RawSpan span) => new(span.Start, span.Length);
    internal static RangeFact From(SourceSpan span)
    {
        if (span == SourceSpan.Empty || span.Start < 0 || span.End < span.Start)
            throw new InvalidOperationException("Common subset requires known nonempty ranges.");
        return new(span.Start, checked(span.End - span.Start + 1));
    }
}
internal sealed record HeadingFact(int Level, string Text, RangeFact RawSpan);
internal sealed record LinkFact(bool Image, string Label, string Target, string? Title, RangeFact RawSpan, RangeFact TargetRawSpan);
internal sealed record CodeFact(string Info, string LogicalContent, RangeFact RawSpan);
internal sealed record CommonFacts(HeadingFact[] Headings, LinkFact[] Links, CodeFact[] Code)
{
    internal IEnumerable<RangeFact> Ranges() => Headings.Select(h => h.RawSpan).Concat(Links.SelectMany(l => new[] { l.RawSpan, l.TargetRawSpan })).Concat(Code.Select(c => c.RawSpan));
}
internal sealed record Fixture(string Id, string Raw, CommonFacts Expected, SourceText Snapshot, int Operations);
internal sealed record EndToEndResult(CommonFacts Facts, Location[] Locations);
internal sealed record LineFact(int Line, int Column);
internal sealed record LocationFact(int Start, int Length, LineFact StartLine, LineFact EndLine)
{
    internal static LocationFact From(Location location)
    {
        var span = location.SourceSpan;
        var line = location.GetLineSpan();
        return new(span.Start, span.Length, new(line.StartLinePosition.Line, line.StartLinePosition.Character), new(line.EndLinePosition.Line, line.EndLinePosition.Character));
    }
}
internal sealed record Sample(double NanosecondsPerOperation, double AllocatedBytesPerOperation, int Gen0, int Gen1, int Gen2);
internal sealed record Measurement(string Fixture, string Engine, string Stage, int OperationsPerSample, IReadOnlyList<Sample> Samples);
internal static class Sink { internal static object? Value; }
