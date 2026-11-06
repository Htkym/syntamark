# Select the analysis outputs

Use `Analyze` when you need only particular artifacts. Existing `Parse` returns all facts; `ParseWithSyntax` returns all facts and syntax.

```csharp
var analysis = MarkdownParser.Analyze(markdown,
    MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links | MarkdownAnalysisParts.Fences,
    scopeId: "docs", sourceId: "guide.md",
    options: new MarkdownParseOptions(profileId: "commonmark/0.31.2"));

if (analysis.GetAvailability(MarkdownAnalysisParts.Links) == MarkdownAnalysisAvailability.Complete)
{
    foreach (var link in analysis.Links!)
        Console.WriteLine(link.Target);
}
```

Select any combination of `FrontMatter`, `Headings`, `Sections`, `Links`, `Fences`, `TextRegions`, and `Syntax`. `AllFacts` and `All` select all facts and all artifacts respectively. `None` performs mandatory parsing and validation without publishing these artifacts. Unrequested properties are `null`. An empty collection with `Complete` availability confirms absence; a partial collection does not. `Root` is also `null` when a resource stop prevents completing syntax. Use `GetAvailability` with one primitive part to distinguish `NotRequested`, `Complete`, `Partial`, and `Unavailable`.

Absence is scoped to the selected profile and its declared capabilities.

Parsing still recognizes blocks and resolves forward references. Selecting fewer outputs skips their publication and derived work; it does not disable input checks, YAML validation in the legacy profile, cancellation, or resource limits. Selected text projections retain their source mappings. Diagnostics and fact coverage describe the requested projections and mandatory validation, and do not certify unrequested projections or every syntax node's position. Inspect each node's nullable `RawSpan`. Syntax-only analysis preserves reference destination positions.

Positions use the original UTF-16 input. Offsets are zero-based; `GetLinePosition` returns one-based lines and columns. The result owns its immutable snapshot, and property access performs no further parsing.

`OptionsHash` retains its existing meaning. Include `AnalysisHash`, `ParserVersion`, and the input identity in analysis cache keys. Different requests have different analysis identities. Existing runtime/source artifact pins require an explicit update when adopting a new parser version.

Subset results use contract `analysis/1`; full facts retain `1.0`. The source API exposes selected properties instead of a full facts document. Use request-aware caches for analysis results. Existing full-facts caches must not accept selected publication data or infer the request from `OptionsHash` alone.
