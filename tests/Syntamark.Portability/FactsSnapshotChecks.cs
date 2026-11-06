using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Compatibility evidence, not a correctness oracle: independent slice assertions live in the facts checks.
internal static class FactsSnapshotChecks
{
    private const string BaselineCommit = "6120fc0f231c952cdf01ed77784bb0f2e4a898d4";
    private const string BaselineParserVersion = "1/7e854150d5cad96e554b90a550251dce74d29b79ceef2964fa9cfa4a226e2051";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static void Run(Assembly runtime, Assembly portable, string[] args)
    {
        var baselinePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "facts-baseline-6120fc0.json");
        if (args.Length != 0)
        {
            if (args.Length != 4 || args[0] != "--capture-facts")
                throw new ArgumentException("Usage: --capture-facts <baseline-runtime-dll> <baseline-portable-dll> <new-output-json>");
            var context = new AssemblyLoadContext("fixed baseline", isCollectible: true);
            context.Resolving += (_, name) => AssemblyLoadContext.Default.LoadFromAssemblyName(name);
            var fixedRuntime = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
            var sourceContext = new AssemblyLoadContext("fixed portable baseline", isCollectible: true);
            sourceContext.Resolving += (_, name) => AssemblyLoadContext.Default.LoadFromAssemblyName(name);
            var fixedPortable = sourceContext.LoadFromAssemblyPath(Path.GetFullPath(args[2]));
            if ((string)Get(Parse(fixedRuntime, ""), "ParserVersion")! != BaselineParserVersion
                || (string)Get(Parse(fixedPortable, ""), "ParserVersion")! != BaselineParserVersion)
                throw new InvalidOperationException("Capture requires the fixed 6120fc0 baseline assemblies.");
            var captured = Capture(fixedRuntime, fixedPortable);
            using var stream = new FileStream(args[3], FileMode.CreateNew);
            JsonSerializer.Serialize(stream, captured, Json);
            Console.WriteLine("Captured fixed baseline without overwriting an existing fixture: " + args[3]);
            return;
        }

        var expected = JsonNode.Parse(File.ReadAllText(baselinePath))!;
        var actual = JsonSerializer.SerializeToNode(Capture(runtime, portable), Json)!;
        // Identity is retained in both records, but a new parser implementation has a new stamp.
        expected["parserVersion"] = actual["parserVersion"]?.DeepClone();
        if (!JsonNode.DeepEquals(expected, actual))
            throw new InvalidOperationException("Full facts compatibility baseline changed. Inspect individual facts; do not regenerate expectations to hide differences.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            task = "SM00",
            status = "PASS",
            baselineCommit = BaselineCommit,
            fixtures = Inputs().Count,
            comparison = "all facts, segments, mapping queries and facade line positions; runtime/portable equal",
            semanticExclusions = new[] { "ParserVersion" }
        }));
    }

    private static object Capture(Assembly runtime, Assembly portable)
    {
        var fixtures = new List<object>();
        string? version = null;
        foreach (var (id, raw) in Inputs())
        {
            var core = Parse(runtime, raw);
            var source = Parse(portable, raw);
            version ??= (string)Get(core, "ParserVersion")!;
            if ((string)Get(core, "ParserVersion")! != (string)Get(source, "ParserVersion")!)
                throw new InvalidOperationException(id + ": runtime/portable parser identity mismatch.");
            var runtimeSnapshot = Snapshot(core);
            var sourceSnapshot = Snapshot(source);
            if (JsonSerializer.Serialize(runtimeSnapshot) != JsonSerializer.Serialize(sourceSnapshot))
                throw new InvalidOperationException(id + ": runtime/portable full facts mismatch.");
            var facade = ParseFacade(runtime, raw);
            foreach (var identity in new[] { "ContractVersion", "ProfileId" })
            {
                var property = core.GetType().GetProperty(identity, Instance);
                if (property is not null && (!Equals(property.GetValue(core), Get(source, identity))
                    || !Equals(property.GetValue(core), Get(facade, identity))))
                    throw new InvalidOperationException(id + ": runtime/source/facade " + identity + " mismatch.");
            }
            var lineMethod = facade.GetType().GetMethod("GetLinePosition")!;
            var positions = Enumerable.Range(0, raw.Length + 1).Select(i => Snapshot(lineMethod.Invoke(facade, new object[] { i }))).ToArray();
            fixtures.Add(new
            {
                id,
                rawText = raw,
                rawUtf16Length = raw.Length,
                factsSha256 = Hash(runtimeSnapshot),
                facadeSha256 = Hash(Snapshot(facade)),
                linePositionsSha256 = Hash(positions)
            });
        }
        return new
        {
            baselineCommit = BaselineCommit,
            profileId = "lithosharp-markdown/1",
            contractVersion = "1.0",
            semanticExclusions = new[] { "ParserVersion" },
            parserVersion = version,
            fixtures
        };
    }

    private static object Parse(Assembly assembly, string raw)
    {
        var options = assembly.GetType("Syntamark.Compilation.MdOptions", true)!;
        var value = options.GetConstructors(Instance).Single().Invoke(new object[] { 1048576, 131072, 200, 16777216, "lithosharp-markdown/1", 1 });
        return assembly.GetType("Syntamark.Compilation.MdParser", true)!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { raw, "pair-scope", "same.md", null, value, CancellationToken.None })!;
    }

    private static object ParseFacade(Assembly assembly, string raw) => assembly.GetType("Syntamark.MarkdownParser", true)!
        .GetMethod("Parse")!.Invoke(null, new object?[] { raw, "pair-scope", "same.md", null, null, CancellationToken.None })!;

    private static object? Snapshot(object? value)
    {
        if (value is null || value is string || value is bool || value is int)
            return value;
        var type = value.GetType();
        if (type.IsEnum)
            return value.ToString();
        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().Select(Snapshot).ToArray();
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.Name is "ParserVersion" or "EqualityContract")
                continue;
            // The v1 core had no identity properties; the same values are already hashed on its public facade.
            if (type.Name == "MdDocumentFacts" && (property.Name is "ContractVersion" or "ProfileId"))
                continue;
            result[property.Name] = Snapshot(property.GetValue(value));
        }
        if (type.Name is "MdTextProjection" or "MarkdownTextProjection")
        {
            var text = (string?)Get(value, "Text");
            var mapMethod = type.Name == "MdTextProjection"
                ? type.Assembly.GetType("Syntamark.Compilation.MdSourceMapping", true)!.GetMethod("Map", BindingFlags.Static | BindingFlags.NonPublic)!
                : type.GetMethod("Map")!;
            var rangeType = type.Assembly.GetType(type.Name == "MdTextProjection" ? "Syntamark.Compilation.MdRawRange" : "Syntamark.RawSpan", true)!;
            var ctor = rangeType.GetConstructors(Instance).Single();
            var queries = new List<object>();
            if (text is not null)
            {
                foreach (var (start, length) in Enumerable.Range(0, text.Length + 1).Select(i => (i, 0))
                    .Concat(Enumerable.Range(0, text.Length).Select(i => (i, 1))).Append((0, text.Length)))
                {
                    var range = ctor.Invoke(new object[] { start, length });
                    var mapped = mapMethod.IsStatic ? mapMethod.Invoke(null, new[] { value, range }) : mapMethod.Invoke(value, new[] { range });
                    queries.Add(new { start, length, result = Snapshot(mapped) });
                }
            }
            result["$queries"] = queries;
        }
        return result;
    }

    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);

    private static string Hash(object? value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static IReadOnlyList<(string Id, string Input)> Inputs() => new (string, string)[]
    {
        ("historical-entity", "# A &amp; 😀\r\n\r\n[link](target.md)\r\n"),
        ("historical-frontmatter", "\uFEFF---\r\ntitle: x\r\n---\r\n## Y\r\n\r\n~~~csharp\r\nx\r\n~~~\r\n"),
        ("historical-unresolved", "# Same\n# Same\n\n[link][missing]\n"),
        ("nested-reference", "> # A\\* &amp; 😀\r\n> [ref][r]\r\n\r\n[r]: /docs?a=1&amp;b=2 \"Guide\"\r\n"),
        ("table-plain", "| A | B |\n| - | - |\n| same [x](/u) | same [x](/u) |\n"),
        ("opaque-partial", "before <Widget>{x}</Widget> after\n\n`a  b`\n"),
        ("code-normalization", "A ` one\r\ntwo ` B\n\n    a😀\r\n    b\r\n"),
    };
}
