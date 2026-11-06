using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static class AllocationChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(Assembly runtime, Assembly portable)
    {
        foreach (var assembly in new[] { runtime, portable })
        {
            Options(assembly);
            Ownership(assembly);
        }
        Console.WriteLine("PASS allocation: concurrent options hashes, invariant canonical values and frozen collection ownership.");
    }

    private static void Options(Assembly assembly)
    {
        var type = assembly.GetType("Syntamark.Compilation.MdOptions", true)!;
        var constructor = type.GetConstructors(Instance).Single();
        var hash = type.GetProperty("Hash", Instance)!;
        var values = new[]
        {
            (65536, "commonmark/0.31.2", "{\"maxInputUtf16\":2048,\"maxNestingDepth\":32,\"maxOutputItems\":256,\"maxScanUnits\":65536,\"optionsSchemaVersion\":1,\"profileId\":\"commonmark/0.31.2\"}"),
            (65536, "lithosharp-markdown/1", "{\"maxInputUtf16\":2048,\"maxNestingDepth\":32,\"maxOutputItems\":256,\"maxScanUnits\":65536,\"optionsSchemaVersion\":1,\"profileId\":\"lithosharp-markdown/1\"}"),
            (65537, "commonmark/0.31.2", "{\"maxInputUtf16\":2048,\"maxNestingDepth\":32,\"maxOutputItems\":256,\"maxScanUnits\":65537,\"optionsSchemaVersion\":1,\"profileId\":\"commonmark/0.31.2\"}"),
        };
        var options = values.Select(value => constructor.Invoke([2048, 256, 32, value.Item1, value.Item2, 1])).ToArray();
        var expected = values.Select(value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Item3)))
            .ToLowerInvariant()).ToArray();
        Parallel.For(0, 32, i =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var selected = i % options.Length;
                Check((string)hash.GetValue(options[selected])! == expected[selected],
                    "Concurrent options reuse changed the profile-specific canonical hash.");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        });
    }

    private static void Ownership(Assembly assembly)
    {
        var freeze = assembly.GetType("Syntamark.Compilation.MdFreeze", true)!;
        var copy = freeze.GetMethod("Copy", Static)!.MakeGenericMethod(typeof(string));
        IReadOnlyList<string> Freeze(IEnumerable<string> source) => (IReadOnlyList<string>)copy.Invoke(null, [source])!;

        var empty = new List<string>();
        var frozenEmpty = Freeze(empty);
        empty.Add("later");
        Check(frozenEmpty.Count == 0, "An empty caller-owned list leaked later additions.");

        var array = new[] { "before" };
        var frozenArray = Freeze(array);
        var frozenWrapper = Freeze(Array.AsReadOnly(array));
        array[0] = "after";
        Check(frozenArray.Single() == "before" && frozenWrapper.Single() == "before",
            "A read-only wrapper over a caller-owned array was treated as owned storage.");

        var list = new List<string> { "first", "second" };
        var frozenList = Freeze(list);
        list.Clear();
        Check(frozenList.SequenceEqual(new[] { "first", "second" }), "A nonempty caller-owned list was retained.");

        var enumerations = 0;
        IEnumerable<string> EmptyIterator()
        {
            enumerations++;
            yield break;
        }
        Check(Freeze(EmptyIterator()).Count == 0 && enumerations == 1,
            "An empty enumerable was skipped or enumerated repeatedly.");

        var range = assembly.GetType("Syntamark.Compilation.MdRawRange", true)!;
        var segment = assembly.GetType("Syntamark.Compilation.MdSourceSegment", true)!;
        var kind = assembly.GetType("Syntamark.Compilation.MdSegmentKind", true)!;
        var projection = assembly.GetType("Syntamark.Compilation.MdTextProjection", true)!;
        var span = range.GetConstructors(Instance).Single().Invoke([0, 1]);
        var original = segment.GetConstructors(Instance).Single().Invoke([span, span, Enum.ToObject(kind, 0)]);
        var replacement = segment.GetConstructors(Instance).Single().Invoke([span, null, Enum.ToObject(kind, 2)]);
        var sourceSegments = Array.CreateInstance(segment, 1);
        sourceSegments.SetValue(original, 0);
        var wrapper = typeof(Array).GetMethod(nameof(Array.AsReadOnly))!.MakeGenericMethod(segment).Invoke(null, [sourceSegments])!;
        var projectionConstructor = projection.GetConstructors(Instance).Single();
        var arrayProjection = projectionConstructor.Invoke(["a", sourceSegments]);
        var wrapperProjection = projectionConstructor.Invoke(["a", wrapper]);
        sourceSegments.SetValue(replacement, 0);
        foreach (var result in new[] { arrayProjection, wrapperProjection })
        {
            var published = (System.Collections.IEnumerable)projection.GetProperty("SourceSegments", Instance)!.GetValue(result)!;
            Check(ReferenceEquals(published.Cast<object>().Single(), original),
                "The existing projection constructor retained a caller-owned segment array.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
