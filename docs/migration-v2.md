# Migrate to Syntamark 2.0

Use the runtime and Source packages from the same immutable pair. The current candidate pair is `2.0.0-preview.20261113`; its existence in a local candidate feed does not mean it has been published to NuGet.org. Existing published `1.2.0` packages remain unchanged. Compiler-host consumers opt in to `Syntamark.Source` as before and continue to use its internal portable API through their own adapter.

## YAML dependencies and behavior

Remove direct YamlDotNet references that existed only to consume Syntamark.Source. Syntamark's native YAML implementation is included in both packages. There is no YamlDotNet fallback. The generic parser uses the `native-yaml/1` profile; its supported grammar and limits are described in [Native YAML](native-yaml.md).

The parser preserves ordered mapping entries and duplicate keys rather than silently selecting the last value. Inspect the result's status and diagnostics before constructing a typed view. Advanced YAML features outside the declared subset remain in the original source and produce diagnostics. Preserving source bytes does not establish that an unsupported document was parsed successfully. Front matter still requires a mapping root, while the generic YAML entry also accepts sequences and scalars. C# naming, required fields, numeric overflow, timestamps and typed conversion belong to the consumer.

## Markdown profiles and footnotes

Ordinary `Parse`, `ParseWithSyntax`, and `Analyze` retain the `lithosharp-markdown/1` default. `commonmark/0.31.2` retains its existing meaning. Select `syntamark-docs/2` explicitly to use footnotes and directive attributes. Include the profile, parser version, effective options and requested analysis parts in cache identities.

```csharp
var notes = MarkdownParser.AnalyzeFootnotes("Text[^source].\n\n[^source]: Source text.\n",
    "docs", "guide.md");
foreach (var reference in notes.References)
    Console.WriteLine($"{reference.Label}: {reference.Number}");
```

`AnalyzeFootnotes` specifically defaults to `syntamark-docs/2`; passing another profile disables that extension. `Analysis` contains the ordinary facts and diagnostics from the same snapshot. Definitions retain `Label`, `NormalizedLabel`, `RawSpan`, `LabelRawSpan`, and separate `ContentRawSpans` for their blocks. Display `Number` follows first-reference order and is null for an unused definition or unresolved reference. Repeated references have a one-based `Occurrence` so renderers can produce distinct backlinks. Duplicate definitions remain visible with `IsDuplicate` and `LMD030`; the first definition resolves references. Unresolved uses remain visible with `LMD031`. Labels and display numbers are separate identifiers.

Definition continuation supports indented text and blank-separated blocks. Escaped markers and markers inside code remain literal. Syntamark provides syntax and facts; HTML footnote markup, source-ID meaning and routing belong to the renderer. Check `Analysis.Status` and availability before drawing absence-based conclusions.

## Editing positions

`RawSpan` keeps its original marker-inclusive UTF-16 meaning. Additional [syntax properties](syntax-design.md) expose content spans, reference labels, table cells and directive attributes. Use each nullable position and mapping precision rather than deriving an edit range from a display string. A covering range can include syntax or excluded gaps and does not establish that replacement is safe.

`SourceMap` composes mappings across multiple snapshots and inputs without file access. A `SourceSnapshot` includes a source ID, version, text and text hash. `SourceMapSegment` relates an output span to an optional `SourceRange` with `Linear`, `Atomic` or `Unknown` correspondence. Compose a downstream map with upstream maps using `Compose`; `Map` returns ordered `Origins`, `Precision`, and `CanEdit`. Generated text has no exact origin, and decoded atomic tokens retain covering precision. Multiple fragments remain separate, even when they come from the same source file.

`SourceEdit` records the target snapshot, expected original text and replacement. Call `IsApplicable` against the current snapshot before applying an edit. It checks source identity, version, hash and expected text; applying files remains the consumer's responsibility. A stale snapshot must not receive a correction prepared for an earlier version.

## Edited snapshots and proofreading

Use [`CreateSession` and `ApplyEdits`](selective-analysis.md#analyze-edited-snapshots) for opt-in paragraph reuse. Existing parse methods keep their behavior. A session returns positions for the new snapshot and explains full-parse fallbacks; persistent syntax node IDs are not guaranteed. Logical resource counters do not establish a timing improvement.

The [prose sample](prose-consumer.md) can invoke a caller-supplied Yomiyasu lint CLI explicitly. It sends selected text segments, maps line diagnostics back to original source fragments, and performs no automatic edits. Python and proofreading remain optional consumer dependencies. Partial parse information, line-only lint positions and unknown source mappings must not be upgraded to exact edits.
