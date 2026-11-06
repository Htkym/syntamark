using System.Collections.Generic;
using System.Text.Json;
using Syntamark;

internal static class PublicAllocationChecks
{
    internal static void Run()
    {
        const string input = "# A &amp; B\n\n> *one* [two](/target)\n\n- three\n- four\n\n```cs\nx\n```\n";
        foreach (var profile in new[] { "lithosharp-markdown/1", "commonmark/0.31.2" })
        {
            var options = new MarkdownParseOptions(profileId: profile);
            var syntax = MarkdownParser.ParseWithSyntax(input, "allocation", "owned", options: options);
            var snapshot = JsonSerializer.Serialize(syntax);
            CheckDocument(syntax.Facts);
            foreach (var node in syntax.Root!.DescendantsAndSelf())
                CheckReadOnly(node.Children);
            CheckDocument(MarkdownParser.Parse("", "allocation", "empty", options: options));
            _ = MarkdownParser.ParseWithSyntax("# A different document\n", "allocation", "later", options: options);
            Check(JsonSerializer.Serialize(syntax) == snapshot, "Later parses preserve the retained public graph.");
        }

        CheckDocument(MarkdownParser.Parse("---\ntitle: >\n  folded\n  text\n---\n# Heading\n", "allocation", "yaml"));
        CheckDocument(MarkdownParser.Parse("too long", "allocation", "rejected", options: new MarkdownParseOptions(maxInputUtf16: 1)));
        var defaults = MarkdownParser.Parse(input, "allocation", "defaults");
        var explicitDefaults = MarkdownParser.Parse(input, "allocation", "defaults", options: new MarkdownParseOptions());
        Check(JsonSerializer.Serialize(defaults) == JsonSerializer.Serialize(explicitDefaults), "Shared default options preserve all public results.");
    }

    private static void CheckDocument(MarkdownDocument document)
    {
        CheckReadOnly(document.Headings);
        CheckReadOnly(document.Sections);
        CheckReadOnly(document.Links);
        CheckReadOnly(document.Fences);
        CheckReadOnly(document.TextRegions);
        CheckReadOnly(document.Diagnostics);
        CheckReadOnly(document.Capabilities);
        CheckReadOnly(document.Coverage.UnprojectedRegions);
        CheckReadOnly(document.Coverage.Reasons);
        foreach (var heading in document.Headings)
            CheckProjection(heading.Text);
        foreach (var link in document.Links)
            CheckProjection(link.Label);
        foreach (var fence in document.Fences)
            CheckProjection(fence.Content);
        foreach (var region in document.TextRegions.Concat(document.Coverage.UnprojectedRegions))
            CheckProjection(region.Content);
        if (document.FrontMatter.Root is { } yaml)
            CheckYaml(yaml);
    }

    private static void CheckYaml(MarkdownYamlNode node)
    {
        CheckReadOnly(node.Children);
        CheckProjection(node.Scalar);
        foreach (var child in node.Children)
            CheckYaml(child);
    }

    private static void CheckProjection(MarkdownTextProjection projection)
    {
        CheckReadOnly(projection.SourceSegments);
        if (projection.Text is { } text)
            CheckReadOnly(projection.Map(new RawSpan(0, text.Length)).RawFragments);
    }

    private static void CheckReadOnly<T>(IReadOnlyList<T> values)
    {
        if (values is not IList<T> list)
            return;
        Check(list.IsReadOnly, "Public collections are read-only through mutable interfaces.");
        ThrowsReadOnly(() => list.Add(default!));
        if (list.Count != 0)
            ThrowsReadOnly(() => list[0] = list[0]);
    }

    private static void ThrowsReadOnly(Action action)
    {
        try
        {
            action();
        }
        catch (NotSupportedException)
        {
            return;
        }
        throw new InvalidOperationException("A public collection accepted mutation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
