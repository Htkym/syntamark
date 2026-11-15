# Migrate to Syntamark 2.0

[日本語](migration-v2.ja.md)

Use the runtime and Source packages from the same immutable pair. The installation examples use `2.0.0-preview.2026111303`; select a pair that includes the APIs you need. A pair in a local candidate feed has not necessarily been published to NuGet.org. Existing published `1.2.0` packages remain unchanged. Compiler-host consumers opt in to `Syntamark.Source` as before and continue to use its internal portable API through their own adapter.

The planned stable pair is `2.0.0`. Switch both package references and the expected parser stamp together when a formal pair is available.

## YAML dependencies and behavior

Remove direct YamlDotNet references that existed only to consume Syntamark.Source. Syntamark's native YAML implementation is included in both packages. There is no YamlDotNet fallback. The generic parser uses the `native-yaml/1` profile; its supported grammar and limits are described in [Native YAML](native-yaml.md).

The parser preserves ordered mapping entries and duplicate keys rather than silently selecting the last value. Inspect the result's status and diagnostics before constructing a typed view. Advanced YAML features outside the declared subset remain in the original source and produce diagnostics. Preserving source bytes does not establish that an unsupported document was parsed successfully. Front matter still requires a mapping root, while the generic YAML entry also accepts sequences and scalars. C# naming, required fields, numeric overflow, timestamps and typed conversion belong to the consumer.

## Markdown profiles and footnotes

Ordinary `Parse`, `ParseWithSyntax`, and `Analyze` retain the `lithosharp-markdown/1` default. `commonmark/0.31.2` retains its existing meaning. Select `syntamark-docs/2` explicitly to use footnotes and directive attributes. Include the profile, parser version, effective options and requested analysis parts in cache identities.

```csharp
using System;
using Syntamark;

var notes = MarkdownParser.AnalyzeFootnotes("Text[^source].\n\n[^source]: Source text.\n",
    "docs", "guide.md");
foreach (var reference in notes.References)
    Console.WriteLine($"{reference.Label}: {reference.Number}");
```

`AnalyzeFootnotes` specifically defaults to `syntamark-docs/2`; passing another profile disables that extension. `Analysis` contains the ordinary facts and diagnostics from the same snapshot. Definitions retain `Label`, `NormalizedLabel`, `RawSpan`, `LabelRawSpan`, and separate `ContentRawSpans` for their blocks. Display `Number` follows first-reference order and is null for an unused definition or unresolved reference. Repeated references have a one-based `Occurrence` so renderers can produce distinct backlinks. Duplicate definitions remain visible with `IsDuplicate` and `LMD030`; the first definition resolves references. Unresolved uses remain visible with `LMD031`. Labels and display numbers are separate identifiers.

Definition continuation supports indented text and blank-separated blocks. Escaped markers and markers inside code remain literal. Syntamark provides syntax and facts; HTML footnote markup, source-ID meaning and routing belong to the renderer. Check `Analysis.Status` and availability before drawing absence-based conclusions.

## Editing positions

`RawSpan` keeps its original marker-inclusive UTF-16 meaning. Additional [syntax properties](syntax-design.en.md) expose content spans, reference labels, table cells and directive attributes. Use each nullable position and mapping precision rather than deriving an edit range from a display string. A covering range can include syntax or excluded gaps and does not establish that replacement is safe.

### Snapshots and segments

`SourceMap` composes mappings across snapshots and inputs without file access. A `SourceSnapshot(sourceId, version, text)` is immutable and exposes `SourceId`, `Version`, `Text` and `TextHash`. Snapshot identity is the combination of source ID, version and text hash, even when two snapshot objects are different instances. The caller supplies IDs and versions; a source ID does not cause the API to open a file. A `SourceRange` pairs one snapshot with a UTF-16 `RawSpan`; `RawSpan.Start` and `RawSpan.Length` describe a half-open range.

Construct a map with `new SourceMap(outputSnapshot, segments)`. Each `SourceMapSegment` describes a nonempty span in that output snapshot. Segments must be in output order, in bounds and nonoverlapping. Gaps are allowed and have unknown correspondence.

| `MarkdownSegmentKind` | Origin and correspondence |
| --- | --- |
| `Linear` | Requires a `SourceRange` with the same length and identical UTF-16 text. A queried subrange maps to the corresponding source subrange. |
| `Atomic` | Requires a `SourceRange` for a whole source token, such as `&amp;` decoded to `&`. A nonempty queried portion covers that whole token; it is not an exact character mapping. |
| `Unknown` | Requires a null origin. Use it for generated text or text whose origin is unavailable. |

### Composition and edit precision

`downstream.Compose(upstreamMaps)` returns a new map. The downstream map describes transformed output; an upstream map's `Output` must match the intermediate snapshot named by an origin. Composition follows all supplied maps and their existing upstream maps transitively, including multiple input sources. Snapshot IDs alone are insufficient to join maps: versions and text hashes must also match.

`Map(outputSpan)` returns `Origins` in output order, plus `Precision` and `CanEdit`. Adjacent source ranges are coalesced when they belong to the same snapshot and the preceding range ends where the next begins. Repeated, noncontiguous, reordered or different-source fragments remain separate; the API does not replace them with one enclosing range.

| `MarkdownMappingPrecision` | Meaning |
| --- | --- |
| `Exact` | Every queried part has a linear origin. The result can still contain multiple origin fragments. |
| `CoveringTokens` | All queried parts have origins, but at least one passes through an atomic token mapping. |
| `Partial` | Some origins are known, while another part is unknown or ambiguous. |
| `Unknown` | No origin can be returned for the query. |

`CanEdit` is true only for `Exact` with one origin fragment whose endpoints do not split a UTF-16 surrogate pair. An `Exact` result spanning two inputs is therefore not one editable replacement. A zero-length query at a join between different origins is ambiguous and cannot be edited. Generated text and gaps prevent an exact result for a query that includes them.

A cycle through snapshot identities contributes unknown correspondence for that path. A query consisting only of a cycle returns `Unknown`; known origins from other paths can yield `Partial`. Conflicting maps for the same snapshot are rejected instead of choosing one: duplicate upstream outputs are rejected by `Compose`, and transitive conflicts are rejected by `Map`.

### Compose a map and guard an edit

Run each C# example in a console project referencing the selected `Syntamark` runtime package. The following example includes `Hello` from `guide.md`, adds a generated prefix and maps the visible word back through both stages. It constructs an updated string in memory and performs no file writes.

```csharp
using System;
using Syntamark;

var original = new SourceSnapshot("guide.md", "1", "Hello");
var included = new SourceSnapshot("included.md", "1", "Hello");
var output = new SourceSnapshot("view.txt", "1", "> Hello");

var includeMap = new SourceMap(included, new[]
{
    new SourceMapSegment(new RawSpan(0, 5),
        new SourceRange(original, new RawSpan(0, 5)), MarkdownSegmentKind.Linear),
});
var composed = new SourceMap(output, new[]
{
    new SourceMapSegment(new RawSpan(0, 2), null, MarkdownSegmentKind.Unknown),
    new SourceMapSegment(new RawSpan(2, 5),
        new SourceRange(included, new RawSpan(0, 5)), MarkdownSegmentKind.Linear),
}).Compose(new[] { includeMap });

var mapped = composed.Map(new RawSpan(2, 5));
if (!mapped.CanEdit)
    throw new InvalidOperationException("The mapped range cannot be edited directly.");

var edit = new SourceEdit(mapped.Origins[0], "Hello", "Hi");
var current = new SourceSnapshot("guide.md", "1", "Hello");
if (!edit.IsApplicable(current))
    throw new InvalidOperationException("The source changed; analyze it again.");

var span = edit.Target.Span;
var revised = current.Text.Remove(span.Start, span.Length).Insert(span.Start, edit.Replacement);
Console.WriteLine(revised); // Hi
Console.WriteLine(composed.Map(new RawSpan(0, 7)).Precision); // Partial
Console.WriteLine(edit.IsApplicable(new SourceSnapshot("guide.md", "2", "Hello"))); // False
```

`SourceEdit(target, expectedText, replacement)` validates that `expectedText` exactly equals the target snapshot's text at that range. It also rejects a range that splits a surrogate pair and a replacement with malformed Unicode. Constructing an edit does not check a map's precision: require `mapped.CanEdit` before using a mapped origin as one replacement.

Call `IsApplicable(currentSnapshot)` against the current source snapshot before applying the proposal. It checks the source ID, version, text hash, bounds and expected original text. A changed version fails even when the text is unchanged; changed text fails even when a caller reuses the version. Syntamark neither reads the current file nor locks or writes it. The consumer owns the current snapshot and the transaction that prevents changes between checking and applying an edit.

For comparison, decoding an entity gives a covering origin and disables direct editing:

```csharp
using System;
using Syntamark;

var encoded = new SourceSnapshot("entity.md", "1", "&amp;");
var decoded = new SourceSnapshot("decoded.txt", "1", "&");
var map = new SourceMap(decoded, new[]
{
    new SourceMapSegment(new RawSpan(0, 1),
        new SourceRange(encoded, new RawSpan(0, 5)), MarkdownSegmentKind.Atomic),
});
var mapped = map.Map(new RawSpan(0, 1));
Console.WriteLine(mapped.Precision); // CoveringTokens
Console.WriteLine(mapped.CanEdit); // False
```

## Editing link destinations

`MarkdownLinkEdits.TryCreateDestinationEdit` accepts a `SourceSnapshot`, its `MarkdownAnalysisResult`, an index into that result's `Links`, and a destination before Markdown escaping. The returned `SourceEdit` replaces only the destination contents; it preserves labels, image alt text, titles, reference labels and surrounding delimiters. It does not read or write files, resolve site routes, normalize URIs, or parse the document again. This API requires a candidate containing the destination editor; the earlier `2.0.0-preview.2026111303` pair does not contain it.

Request `Links` explicitly. The source ID, explicit source version and text hash must match the analysis; both analysis status and Links availability must be `Complete`. The index selects a link from that exact result, so a detached link cannot be paired with an unrelated snapshot. No syntax output is required for this operation. Null arguments throw; a cancelled token throws `OperationCanceledException`. Other refusals return false, a null edit and a `MarkdownLinkEditFailure` value.

```csharp
using System;
using Syntamark;

var source = new SourceSnapshot("guide.md", "1", "[Guide](old.md \"Title\")");
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var analysis = MarkdownParser.Analyze(source.Text, MarkdownAnalysisParts.Links,
    "docs", source.SourceId, source.Version, options);
if (!MarkdownLinkEdits.TryCreateDestinationEdit(source, analysis, 0,
    "https://example.test/a(b)?x=1&y=2", out var edit, out var failure))
    throw new InvalidOperationException(failure.ToString());
if (!edit!.IsApplicable(source))
    throw new InvalidOperationException("The source changed.");
var span = edit.Target.Span;
var revised = source.Text.Remove(span.Start, span.Length).Insert(span.Start, edit.Replacement);
var checkedResult = MarkdownParser.Analyze(revised, MarkdownAnalysisParts.Links,
    "docs", source.SourceId, "2", options);
Console.WriteLine(checkedResult.Links![0].Target); // https://example.test/a(b)?x=1&y=2
Console.WriteLine(checkedResult.Links[0].Title); // Title
```

Inline links, images and resolved reference uses/definitions support bare and angle-delimited destinations in all three Markdown profiles. Character references protect Markdown delimiters, backslashes, ampersands, whitespace and table pipes while preserving the decoded destination, including Unicode, query, fragment and existing percent escapes. An empty original destination can be replaced. A new empty destination is rejected as `EmptyDestination`; malformed Unicode and control characters, including tabs and newlines, are `InvalidDestination`. Autolinks are `UnsupportedLinkKind` because their label and destination are coupled. Unresolved references and unknown destination ranges are rejected separately. An unchanged unresolved literal can remain in the caller's output without being edited.

`SnapshotMismatch`, `AnalysisIncomplete`, `LinksUnavailable` and `LinkNotFound` identify input or analysis failures. `UnsupportedProfile` prevents applying these rules to an unknown profile. A complete parse alone does not make a null position editable, and `SourceEdit.IsApplicable` alone does not validate Markdown syntax.

A reference use points to its shared definition. Multiple uses can therefore produce the same edit. Collect proposals against one snapshot, collapse identical target/replacement pairs, reject conflicting or intersecting edits, and apply the remaining disjoint edits in descending offset order. Reanalyze the final text once with the same profile and intended input kind to validate the combined result; do not reparse the whole document for each link. After an include or component transformation, use a distinct snapshot and `MarkdownInputKind.Body` with the original profile. Map an edit back to an origin only when `SourceMapResult.CanEdit` is true; multiple origins or covering mappings are not one exact replacement.

The Source package provides the same operation as `Syntamark.Compilation.MdLinkEdits.TryCreateDestinationEdit`, using `MdSourceSnapshot`, `MdAnalysisResult`, `MdSourceEdit` and `MdLinkEditFailure`. Both paths share the destination encoder and rejection rules; Source does not include the runtime facade.

## Edited snapshots and proofreading

Use [`CreateSession` and `ApplyEdits`](selective-analysis.md#analyze-edited-snapshots) for opt-in paragraph reuse. Existing parse methods keep their behavior. A session returns positions for the new snapshot and explains full-parse fallbacks; persistent syntax node IDs are not guaranteed. Logical resource counters do not establish a timing improvement.

Editors that need selected facts and unsupported-syntax candidates should use `CreateAnalysisSession`. Its distinct `MarkdownAnalysisSession` exposes availability-aware `Analysis` rather than a full-document facade. The portable `MdParseSession.CreateForAnalysis` result also retains legacy rendering `Blocks` from that parse. Requested parts survive both delta and full-parse paths; request `UnsupportedSyntax` explicitly and keep document and transformed-body snapshots separate. The earlier `2.0.0-preview.20261113` pair remains unchanged with its original session API. To use selected-analysis sessions, update both package references and the expected parser stamp to a pair containing that API.

The [prose sample](prose-consumer.md) can invoke a caller-supplied Yomiyasu lint CLI explicitly. It sends selected text segments, maps line diagnostics back to original source fragments, and performs no automatic edits. Python and proofreading remain optional consumer dependencies. Partial parse information, line-only lint positions and unknown source mappings must not be upgraded to exact edits.
