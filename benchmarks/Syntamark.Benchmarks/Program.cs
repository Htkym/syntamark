using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syntamark;

if (args.Length > 0 && args[0] is "--syntax" or "--list")
{
    SyntaxAndListBenchmarks.Run(args);
    return;
}

if (args.Length == 1 && args[0] == "--snapshot")
{
    Console.WriteLine(QueryChecks.CaptureHash());
    return;
}
if (args.Length != 3)
    throw new ArgumentException("Usage: <label> <source-commit-or-label> <new-results.json>, or --snapshot");
QueryChecks.Run();
var results = new List<Measurement>();
var retained = new List<object>();
var small = "# Linear &amp; \\* 😀\r\n\r\nParagraph [link](/target) and `code`.\r\n";
var medium = string.Concat(Enumerable.Repeat(small + "> quoted line\r\n> another line\r\n\r\n", 160));
var mapDocument = Parse(small);
var projection = mapDocument.Headings[0].Text;
var atomicAt = projection.Text!.IndexOf('&');
var unknown = Parse("    code\n").TextRegions.Single().Content;
var random = new Random(51205);
var offsets = Enumerable.Range(0, 1024).Select(_ => random.Next(medium.Length + 1)).ToArray();
var lineDocument = Parse(medium);

// Warm the query methods on disposable instances before measuring per-document cold initialization.
for (var i = 0; i < 5000; i++)
{
    BenchmarkSink.Map = projection.Map(new RawSpan(0, projection.Text.Length));
    BenchmarkSink.Line = lineDocument.GetLinePosition(offsets[i % offsets.Length]);
}
for (var i = 0; i < 20; i++)
    BenchmarkSink.Document = Parse(small);

Measure("parse-small", 500, () =>
{
    for (var i = 0; i < 500; i++)
        BenchmarkSink.Document = Parse(small);
});
Measure("parse-medium", 10, () =>
{
    for (var i = 0; i < 10; i++)
        BenchmarkSink.Document = Parse(medium);
});
Measure("map-linear-repeated", 100000, () =>
{
    for (var i = 0; i < 100000; i++)
        BenchmarkSink.Map = projection.Map(new RawSpan(0, 6));
});
Measure("map-atomic-repeated", 100000, () =>
{
    for (var i = 0; i < 100000; i++)
        BenchmarkSink.Map = projection.Map(new RawSpan(atomicAt, 1));
});
Measure("map-zero-width-repeated", 100000, () =>
{
    for (var i = 0; i < 100000; i++)
        BenchmarkSink.Map = projection.Map(new RawSpan(atomicAt, 0));
});
Measure("map-unknown-repeated", 100000, () =>
{
    for (var i = 0; i < 100000; i++)
        BenchmarkSink.Map = unknown.Map(new RawSpan(0, 1));
});
Measure("line-random-repeated", 10000, () =>
{
    for (var i = 0; i < 10000; i++)
        BenchmarkSink.Line = lineDocument.GetLinePosition(offsets[i % offsets.Length]);
});
Measure("line-ascending-repeated", 10000, () =>
{
    for (var i = 0; i < 10000; i++)
        BenchmarkSink.Line = lineDocument.GetLinePosition(i % (medium.Length + 1));
});

MeasureFresh("map-first", small, documents =>
{
    foreach (var document in documents)
        BenchmarkSink.Map = document.Headings[0].Text.Map(new RawSpan(0, 6));
});
MeasureFresh("line-first-start", medium, documents =>
{
    foreach (var document in documents)
        BenchmarkSink.Line = document.GetLinePosition(1);
});
MeasureFresh("line-first-end", medium, documents =>
{
    foreach (var document in documents)
        BenchmarkSink.Line = document.GetLinePosition(medium.Length);
});
MeasureFresh("line-second-index", medium, documents =>
{
    foreach (var document in documents)
        BenchmarkSink.Line = document.GetLinePosition(medium.Length);
}, initializeLine: true);

foreach (var mode in new[] { "no-queries", "one-map", "all-maps", "line-index" })
    retained.Add(Retained(medium, mode));
var assembly = typeof(MarkdownParser).Assembly;
var output = new
{
    label = args[0],
    source = args[1],
    timestampUtc = DateTime.UtcNow,
    framework = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
    processors = Environment.ProcessorCount,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    assemblySha256 = Hash(File.ReadAllBytes(assembly.Location)),
    parserVersion = mapDocument.ParserVersion,
    optionsHash = mapDocument.OptionsHash,
    profile = mapDocument.ProfileId,
    querySemanticHash = QueryChecks.CaptureHash(),
    fixtures = new[] { new { id = "small", length = small.Length, sha256 = Hash(Encoding.UTF8.GetBytes(small)), status = mapDocument.Status.ToString() },
        new { id = "medium", length = medium.Length, sha256 = Hash(Encoding.UTF8.GetBytes(medium)), status = lineDocument.Status.ToString() } },
    results,
    retained
};
using (var stream = new FileStream(args[2], FileMode.CreateNew))
    JsonSerializer.Serialize(stream, output, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(JsonSerializer.Serialize(new { label = args[0], status = "COMPLETE", querySemanticHash = QueryChecks.CaptureHash(), output = args[2] }));

void Measure(string name, int operations, Action action)
{
    var samples = new List<Sample>();
    action();
    for (var sample = 0; sample < 5; sample++)
        samples.Add(Time(operations, action));
    results.Add(new Measurement(name, operations, samples));
}

void MeasureFresh(string name, string raw, Action<MarkdownDocument[]> action, bool initializeLine = false)
{
    var samples = new List<Sample>();
    for (var sample = -1; sample < 5; sample++)
    {
        var documents = Enumerable.Range(0, 32).Select(_ => Parse(raw)).ToArray();
        if (initializeLine)
            foreach (var document in documents)
                BenchmarkSink.Line = document.GetLinePosition(1);
        var measured = Time(documents.Length, () => action(documents));
        if (sample >= 0)
            samples.Add(measured);
        GC.KeepAlive(documents);
    }
    results.Add(new Measurement(name, 32, samples));
}

static Sample Time(int operations, Action action)
{
    var bytes = GC.GetAllocatedBytesForCurrentThread();
    var gen0 = GC.CollectionCount(0);
    var gen1 = GC.CollectionCount(1);
    var gen2 = GC.CollectionCount(2);
    var start = Stopwatch.GetTimestamp();
    action();
    var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
    return new Sample(elapsed / operations, (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)operations,
        GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static object Retained(string raw, string mode)
{
    var before = GC.GetTotalMemory(true);
    var documents = Enumerable.Range(0, 32).Select(_ => Parse(raw)).ToArray();
    foreach (var document in documents)
    {
        if (mode == "one-map")
            BenchmarkSink.Map = document.Headings[0].Text.Map(new RawSpan(0, 6));
        if (mode == "all-maps")
            foreach (var projection in document.Headings.Select(heading => heading.Text)
                .Concat(document.TextRegions.Select(region => region.Content))
                .Concat(document.Links.Select(link => link.Label)))
                BenchmarkSink.Map = projection.Map(new RawSpan(0, projection.Text!.Length));
        if (mode == "line-index")
        {
            BenchmarkSink.Line = document.GetLinePosition(1);
            BenchmarkSink.Line = document.GetLinePosition(raw.Length);
        }
    }
    var after = GC.GetTotalMemory(true);
    GC.KeepAlive(documents);
    return new { mode, documents = documents.Length, retainedBytes = after - before, bytesPerDocument = (after - before) / (double)documents.Length };
}

static MarkdownDocument Parse(string raw)
{
    var document = MarkdownParser.Parse(raw, "benchmark", "fixed.md");
    if (document.Diagnostics.Any(diagnostic => diagnostic.Id == "LMD003") || document.Status == MarkdownParseStatus.Failed)
        throw new InvalidOperationException("Resource-stopped/failed parses are not benchmark results.");
    return document;
}

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
internal sealed record Sample(double NanosecondsPerOperation, double AllocatedBytesPerOperation, int Gen0, int Gen1, int Gen2);
internal sealed record Measurement(string Workload, int OperationsPerSample, IReadOnlyList<Sample> Samples);
internal static class BenchmarkSink
{
    internal static MarkdownDocument? Document;
    internal static MarkdownMappingResult? Map;
    internal static MarkdownLinePosition Line;
}
