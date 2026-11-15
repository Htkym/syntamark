using System.Reflection;

internal static class DependencyChecks
{
    internal static void Run(Assembly portable)
    {
        foreach (var assembly in new[] { typeof(Syntamark.MarkdownParser).Assembly, portable })
        {
            var unexpected = assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name is "YamlDotNet" or "SkiaSharp"
                    || reference.Name?.StartsWith("SkiaSharp.NativeAssets", StringComparison.Ordinal) == true)
                .Select(reference => reference.FullName).ToArray();
            if (unexpected.Length != 0)
                throw new InvalidOperationException("Parser references retired dependencies: " + string.Join("; ", unexpected));
        }
        Console.WriteLine("Runtime and netstandard2.0 parser dependency checks PASS.");
    }
}
