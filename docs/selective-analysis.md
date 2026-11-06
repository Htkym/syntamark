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

For editor recovery, use `new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody)` (Source: `MdOptions` and `MdFrontMatterRecovery`). An unterminated front matter envelope then exposes the entire original input as `BodySpan`, while retaining its `Unterminated` state, `LMD004` diagnostic and `Partial` status. The default remains strict. `FrontMatter.FailureSpan` is the known zero-width EOF position; `FailureLine` preserves the delimiter scanner's expected next line, which can be one beyond the physical EOF line. Recovery has a distinct effective `OptionsHash` in the legacy profile; CommonMark has no front matter recovery to apply.

Select `UnsupportedSyntax` explicitly to obtain `UnsupportedSyntax` candidates for footnotes, definition lists, generic attributes and grid tables. Each candidate has a `Kind`, stable `Reason` and original-input `RawSpan` for its marker. Candidates are evidence of literal extension-like syntax, not support for rendering that extension or a product diagnostic. Code, math, HTML tokens and link destinations are excluded using the existing parse. Check availability before concluding absence. `AllFacts = 63` and `All = 127` retain their existing meaning; combine either with `UnsupportedSyntax = 128` when needed. Selecting candidates changes `AnalysisHash`, not `OptionsHash`.

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
