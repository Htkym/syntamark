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

Subset results use contract `analysis/2`; full facts use `2.0`. Both expose `YamlProfileId = native-yaml/1` separately from the Markdown `ProfileId`. The source API exposes selected properties instead of a full facts document. Use request-aware caches for analysis results. Existing full-facts caches must not accept selected publication data or infer the request from `OptionsHash` alone. Invalidate 1.x parser caches when adopting the native YAML contract.

For editor recovery, use `new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody)` (Source: `MdOptions` and `MdFrontMatterRecovery`). An unterminated front matter envelope then exposes the entire original input as `BodySpan`, while retaining its `Unterminated` state, `LMD004` diagnostic and `Partial` status. The default remains strict. `FrontMatter.FailureSpan` is the known zero-width EOF position; `FailureLine` preserves the delimiter scanner's expected next line, which can be one beyond the physical EOF line. Recovery has a distinct effective `OptionsHash` in the legacy profile; CommonMark has no front matter recovery to apply.

Select `UnsupportedSyntax` explicitly to obtain `UnsupportedSyntax` candidates for extension-like syntax not enabled by the selected profile: footnotes, definition lists, generic attributes and grid tables. Footnotes are recognized by `syntamark-docs/2` rather than reported as unsupported candidates in that profile. Each candidate has a `Kind`, stable `Reason` and original-input `RawSpan` for its marker. Candidates are evidence of literal extension-like syntax, not support for rendering that extension or a product diagnostic. Code, math, HTML tokens and link destinations are excluded using the existing parse. Check availability before concluding absence. `AllFacts = 63` and `All = 127` retain their existing meaning; combine either with `UnsupportedSyntax = 128` when needed. Selecting candidates changes `AnalysisHash`, not `OptionsHash`.

The portable adapter entry `MdParser.AnalyzeWithBlocks` returns the same selected facts plus `Blocks` from that parse for existing renderers. Block positions are relative to `BodySpan.Start`; fact positions are relative to the original input. This entry does not require `Syntax`; it retains no second syntax tree. Once block and inline parsing succeeds, `Blocks` remains available even if later fact or syntax publication reaches a resource limit. The result remains `Partial` with `LMD003`; retained blocks do not certify complete facts. If block parsing itself stops, `Blocks` is null. Cancellation still throws. Keep rendering-result caches separate from ordinary analysis caches, which do not retain blocks. `ParseWithSyntax` and `Analyze` with `Syntax` remain the public ways to obtain typed structure and facts together.

When the input is already a Markdown body, select `new MarkdownParseOptions(MarkdownInputKind.Body)` (Source: `new MdOptions(MdInputKind.Body)`). All parse/analysis entries accept this option. Body input keeps the same profile, skips front matter recognition, reports `FrontMatter.State = Absent` when requested, and has `BodySpan = (0, raw.Length)`, including a leading BOM. A body beginning with `---` is therefore Markdown rather than a document envelope. Body mode has a distinct `OptionsHash` and `AnalysisHash`; default document identities remain unchanged. Front matter recovery is ineffective in body mode and does not change its hash.

```csharp
var analysis = MdParser.AnalyzeWithBlocks(body,
    MdAnalysisParts.Headings | MdAnalysisParts.Links | MdAnalysisParts.UnsupportedSyntax,
    scopeId: "site", sourceId: "body.md", sourceVersion: null,
    options: new MdOptions(MdInputKind.Body), cancellationToken: cancellationToken);
// Use analysis.Blocks with the renderer; inspect Status and each requested fact's availability separately.
```

Use `MarkdownParser.ScanFrontMatter(raw)` (Source: `MdParser.ScanFrontMatter(raw, options, cancellationToken)`) when only header delimiters and source ranges are needed. This independent entry uses contract `frontmatter-scan/1`; `Analyze(FrontMatter)` still recognizes the whole document. The scan returns `State` (`Absent`, `Closed`, `Unterminated`, `Unknown`), header-only `Status`, nullable delimiter/YAML/body ranges, failure position and diagnostics. `Closed` and `Complete` establish delimiters and well-formed UTF-16 in the header prefix. They do not validate YAML or the Markdown body. Validate the original `YamlSpan` with the consumer's YAML/schema rules.

For this entry, `MaxInputUtf16` bounds the header prefix through the closing delimiter's line break, including a leading BOM. `MaxScanUnits` covers opening-marker recognition, delimiter scanning and header UTF-16 validation. A closed header is unaffected by the length, nesting, syntax or Unicode validity of the following body. Non-header input uses a bounded opening-marker probe; `HeaderSpan` then contains only the legacy profile's skipped BOM, if present. An unterminated header candidate is scanned toward EOF and remains subject to header limits. Prefix input overflow reports `LMD002`/`Failed`; a scan budget stop reports `LMD003`/`Partial` with unknown ranges. Neither claims EOF or untermination. Confirmed untermination reports `LMD004`, `FailureSpan` at EOF and the scanner's expected `FailureLine`. Body input options are rejected; editor recovery does not apply to this delimiter-only entry. CommonMark reports absent front matter without inspecting its body.

The result contains no body hash, body coverage or YAML tree. Cache it by its contract, parser/options identity and the caller's document snapshot; do not treat it as selected full-document analysis. For site generation, scan the header first, then analyze the final included/transformed body once with `MdInputKind.Body`. The consumer owns which snapshot supplies reader/loader warnings.

## Analyze edited snapshots

`MarkdownParser.CreateSession` owns one immutable document and syntax snapshot. Pass edits against that snapshot to `ApplyEdits`; its result is a new session, and the original remains available.

```csharp
var previous = MarkdownParser.CreateSession("# Guide\n\nFirst paragraph.\n\nSecond paragraph.\n",
    "docs", "guide.md", sourceVersion: "1");
var next = previous.ApplyEdits(previous.Document.TextHash,
    new[] { new MarkdownTextEdit(new RawSpan(9, 5), "Updated") }, sourceVersion: "2");
Console.WriteLine(next.Mode);
Console.WriteLine(next.ReusedBlocks);
```

Edit ranges use the complete previous input's UTF-16 coordinates, including any front matter and BOM. Supply sorted, non-overlapping edits with distinct starting positions. Out-of-range edits, null replacements, and a mismatched `expectedTextHash` are rejected. A null options argument retains the previous effective options; changing options produces a full parse. Cancellation and invalid edits do not modify the old session.

`Mode` is `Full` or `Delta`. `Fallback` records `InitialParse`, `PreviousNotComplete`, `OptionsChanged`, `UnsupportedSyntax`, `UnsupportedEdit`, `NoReusableBlocks`, or `ResourceBoundary`; a successful delta uses `None`. Reuse currently applies to unchanged, isolated, single-line plain-text paragraphs whose blank-line boundaries remain valid. Other blocks, reference resolution, fact and syntax publication, and input hashing still run through the canonical parser. A document may contain headings, front matter, code fences, or references while its eligible paragraphs are reused. Ambiguous boundaries and resource stops use a full parse.

`ReusedBlocks` and `RebasedBlocks` count reused eligible paragraphs, including those whose source offsets changed. `ParsedBlocks` counts newly parsed eligible paragraphs; it is not a total count of all document blocks. `ScanUnits` and `OutputItems` report logical resource-budget charges, including charges retained for reused paragraphs. They do not measure elapsed time, allocations, or the amount of work skipped. Measure those separately before claiming a performance improvement.

Each result has positions and identities for its new input snapshot. Syntax node reference identity across sessions is not a public guarantee. Keep the parser/profile/options/input identities in caches and discard diagnostics or edits for a stale source version.
