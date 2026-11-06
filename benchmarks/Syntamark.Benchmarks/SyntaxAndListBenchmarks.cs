using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Syntamark;

internal static class SyntaxAndListBenchmarks
{
    private static object? sink;

    internal static void Run(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: --syntax|--list <source-label> <new-results.json>");
        var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
        Func<string, object> parse = raw => MarkdownParser.Parse(raw, "benchmark", "fixed.md", options: options);
        Func<object, MarkdownDocument> facts = value => (MarkdownDocument)value;
        var results = args[0] == "--syntax" ? Syntax(parse, facts, options) : Lists(parse, facts);
        var probe = (MarkdownDocument)parse("# identity\n");
        var assembly = typeof(MarkdownParser).Assembly;
        var output = new
        {
            mode = args[0],
            source = args[1],
            timestampUtc = DateTime.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            processors = Environment.ProcessorCount,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            serverGc = System.Runtime.GCSettings.IsServerGC,
            processId = Environment.ProcessId,
            assemblySha256 = Hash(File.ReadAllBytes(assembly.Location)),
            probe.ParserVersion,
            probe.ProfileId,
            probe.OptionsHash,
            semanticExclusions = new[] { "ParserVersion" },
            results
        };
        using var stream = new FileStream(args[2], FileMode.CreateNew);
        JsonSerializer.Serialize(stream, output, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(JsonSerializer.Serialize(new { mode = args[0], status = "COMPLETE", output = args[2] }));
    }

    private static object Syntax(Func<string, object> parse, Func<object, MarkdownDocument> facts, MarkdownParseOptions options)
    {
        var method = typeof(MarkdownParser).GetMethod("ParseWithSyntax", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("The selected assembly does not contain ParseWithSyntax.");
        var rawParameter = Expression.Parameter(typeof(string), "raw");
        var syntaxCall = Expression.Call(method, rawParameter, Expression.Constant("benchmark"), Expression.Constant("fixed.md"),
            Expression.Constant(null, typeof(string)), Expression.Constant(options), Expression.Constant(CancellationToken.None));
        var withSyntax = Expression.Lambda<Func<string, object>>(Expression.Convert(syntaxCall, typeof(object)), rawParameter).Compile();
        var wrapper = Expression.Parameter(typeof(object), "wrapper");
        var syntaxFacts = Expression.Lambda<Func<object, MarkdownDocument>>(
            Expression.Property(Expression.Convert(wrapper, method.ReturnType), "Facts"), wrapper).Compile();
        const string small = "# Heading\n\nParagraph with **bold**, &amp; and [link](/target).\n\n> quoted *text*\n\n```csharp\nx\n```\n";
        var fixtures = new[] { (Id: "small", Raw: small), (Id: "medium", Raw: string.Concat(Enumerable.Repeat(small, 80))) };
        var results = new List<object>();
        foreach (var (id, raw) in fixtures)
        {
            var plain = parse(raw);
            var syntax = withSyntax(raw);
            RequireComplete(facts(plain));
            RequireComplete(syntaxFacts(syntax));
            var plainHash = FactsHash(facts(plain));
            if (plainHash != FactsHash(syntaxFacts(syntax)))
                throw new InvalidOperationException("Facts differ with retained syntax: " + id);
            var root = method.ReturnType.GetProperty("Root")!.GetValue(syntax)
                ?? throw new InvalidOperationException("A Complete syntax parse has no root.");
            var nodes = ((IEnumerable)root.GetType().GetMethod("DescendantsAndSelf")!.Invoke(root, new object[] { CancellationToken.None })!).Cast<object>().Count();
            var operations = id == "small" ? 200 : 5;
            results.Add(new
            {
                fixture = id,
                rawUtf16Length = raw.Length,
                inputSha256 = Hash(Encoding.UTF8.GetBytes(raw)),
                factsSha256 = plainHash,
                status = "Complete",
                syntaxNodes = nodes,
                noSyntax = Measure(operations, () =>
                {
                    for (var i = 0; i < operations; i++)
                        sink = parse(raw);
                }),
                withSyntax = Measure(operations, () =>
                {
                    for (var i = 0; i < operations; i++)
                        sink = withSyntax(raw);
                }),
                retainedNoSyntax = Retained(raw, parse, facts),
                retainedWithSyntax = Retained(raw, withSyntax, syntaxFacts)
            });
        }
        return results;
    }

    private static object Lists(Func<string, object> parse, Func<object, MarkdownDocument> facts)
    {
        var complete = new List<object>();
        foreach (var count in new[] { 200, 400, 800 })
        {
            var raw = "- start\n" + string.Concat(Enumerable.Repeat("continuation\n", count));
            var document = facts(parse(raw));
            RequireComplete(document);
            complete.Add(new
            {
                continuationLines = count,
                rawUtf16Length = raw.Length,
                inputSha256 = Hash(Encoding.UTF8.GetBytes(raw)),
                factsSha256 = FactsHash(document),
                status = document.Status.ToString(),
                parse = Measure(5, () =>
                {
                    for (var i = 0; i < 5; i++)
                    {
                        var parsed = parse(raw);
                        RequireComplete(facts(parsed));
                        sink = parsed;
                    }
                })
            });
        }
        var defaultBoundaryInput = "- start\n" + string.Concat(Enumerable.Repeat("continuation\n", 1600));
        var boundary = facts(parse(defaultBoundaryInput));
        return new
        {
            complete,
            defaultBoundary = new
            {
                continuationLines = 1600,
                rawUtf16Length = defaultBoundaryInput.Length,
                inputSha256 = Hash(Encoding.UTF8.GetBytes(defaultBoundaryInput)),
                status = boundary.Status.ToString(),
                reasons = boundary.Diagnostics.Select(diagnostic => new { diagnostic.Id, diagnostic.Reason }).ToArray(),
                factsSha256 = FactsHash(boundary),
                timed = false
            }
        };
    }

    private static object Measure(int operations, Action action)
    {
        action();
        var samples = new List<Sample>();
        for (var sample = 0; sample < 5; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var start = Stopwatch.GetTimestamp();
            action();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            samples.Add(new Sample(elapsed / operations, (GC.GetAllocatedBytesForCurrentThread() - before) / (double)operations,
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2));
        }
        return new { operationsPerSample = operations, samples };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Retained(string raw, Func<string, object> parse, Func<object, MarkdownDocument> facts)
    {
        var before = GC.GetTotalMemory(true);
        var documents = Enumerable.Range(0, 32).Select(_ => parse(raw)).ToArray();
        foreach (var document in documents)
            RequireComplete(facts(document));
        var after = GC.GetTotalMemory(true);
        GC.KeepAlive(documents);
        return new { documents = documents.Length, retainedBytes = after - before, bytesPerDocument = (after - before) / (double)documents.Length };
    }

    private static void RequireComplete(MarkdownDocument document)
    {
        if (document.Status != MarkdownParseStatus.Complete || document.Diagnostics.Any(diagnostic => diagnostic.Id == "LMD003"))
            throw new InvalidOperationException("Partial/failed parses are excluded from complete parse measurements.");
    }

    private static string FactsHash(MarkdownDocument document)
    {
        var node = JsonSerializer.SerializeToNode(document)!.AsObject();
        node.Remove("ParserVersion");
        return Hash(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
