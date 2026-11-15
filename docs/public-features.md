# Public features

English | [日本語](public-features.ja.md)

This guide maps Syntamark's public features to the same English and Japanese explanations. The linked guides provide API details and examples; the sections below describe shared configuration, diagnostics, limitations and migration rules. Syntamark parses owned text. It does not render HTML, access source files, execute MDX, bind a C# content schema or implement site routing.

## Feature and language map

| Feature | Public API | English | 日本語 |
|---|---|---|---|
| Runtime and compiler-host Source | `MarkdownParser`, portable `MdParser`, Source build properties | [Packages](#packages) | [配布](public-features.ja.md#packages) |
| Markdown grammar and profiles | `MarkdownParseOptions.ProfileId`, `Capabilities` | [Profiles](#profiles) | [profile](public-features.ja.md#profiles) |
| Full facts, sections, links and text | `Parse`, `MarkdownDocument` | [Facts and positions](#facts) | [facts と位置](public-features.ja.md#facts) |
| Decoded projections and line positions | `MarkdownTextProjection.Map`, `GetLinePosition` | [Facts and positions](#facts) | [facts と位置](public-features.ja.md#facts) |
| Selected facts and unsupported candidates | `Analyze`, `MarkdownAnalysisParts`, `GetAvailability` | [Analysis](#analysis) | [選択解析](public-features.ja.md#analysis) |
| Header scan, front matter and body input | `ScanFrontMatter`, `MarkdownInputKind`, recovery options | [Document inputs](#inputs) | [文書入力](public-features.ja.md#inputs) |
| Syntax, content/label spans and table cells | `ParseWithSyntax`, `MarkdownSyntaxNode` | [Syntax](syntax-design.en.md) | [構文](syntax-design.md) |
| Directive attributes and diagnostics | `MarkdownDirectiveSyntax`, `LMD020`-`LMD024` | [Directives](syntax-design.en.md) | [ディレクティブ](syntax-design.md) |
| Footnote definitions, references and numbering | `AnalyzeFootnotes`, `MarkdownFootnoteDocument` | [Footnotes](migration-v2.md#markdown-profiles-and-footnotes) | [脚注](migration-v2.ja.md) |
| Composed source maps and guarded edits | `SourceSnapshot`, `SourceMap`, `SourceEdit` | [Source maps](migration-v2.md#editing-positions) | [位置対応](migration-v2.ja.md) |
| Immutable full/selected editing sessions | `CreateSession`, `CreateAnalysisSession`, `ApplyEdits` | [Editing](#editing) | [編集](public-features.ja.md#editing) |
| YAML syntax, scalar meaning, bytes and front matter | `YamlParser`, `YamlParseOptions`, `GetYamlSyntax` | [YAML](native-yaml.md) | [YAML](native-yaml.ja.md) |
| Prose selection and optional Yomiyasu bridge | `samples/Syntamark.Prose` | [Prose](#prose) | [校正連携](public-features.ja.md#prose) |
| Resource budgets, cancellation and result identity | parse options, `Status`, `Coverage`, hashes | [Resources](#resources) | [資源制限](public-features.ja.md#resources) |
| Reproducible performance measurement | benchmark executables, session counters | [Measurement](#measurement) | [測定](public-features.ja.md#measurement) |

<a id="packages"></a>
## Runtime and compiler-host Source

Use the installation examples in the [README](../README.en.md). Both packages use the same native Markdown/YAML implementation and immutable version pair. The runtime package exposes `Syntamark` on .NET 10. Source adds internal portable `Syntamark.Compilation` types to a netstandard2.0/C# 13 consumer when `SyntamarkIncludeSource=true`; pin `SyntamarkSourceVersion` to the exact package reference. Source contains no runtime public facade or assembly-friend declarations. Its consumer owns adapters and host integration; runtime public type names are not the Source entry points.

The [Roslyn sample](../samples/Syntamark.Roslyn/) reads one `AdditionalText` snapshot with its cancellation token and uses that same snapshot for diagnostics. It targets Roslyn API 4.14.0; API reference version, source compiler and analyzer execution host are distinct conditions. It includes unreadable-input, edit, delete and rename cases. The sample's `SMC000` reports parser state, `SMC001` reports a heading containing TODO, and `SMC002` reports unreadable input. No IDE, Visual Basic, .NET Framework compiler-host or minimum-SDK compatibility is implied. Syntamark itself has no Roslyn dependency. See [migration](migration-v2.md) before updating a pair or expected parser stamp.

<a id="profiles"></a>
## Markdown grammar and profiles

`lithosharp-markdown/1` remains the default compatibility profile. It includes its existing front matter, tables, task lists, strikethrough, bare URLs, math, directives/admonitions and opaque HTML/MDX treatment. `commonmark/0.31.2` is opt-in: YAML-looking input remains Markdown, and those extensions are not enabled. HTML blocks/tokens are recognized without HTML rendering or DOM construction. Heading anchors use Syntamark's slug policy, not a requirement of CommonMark. `syntamark-docs/2` extends the compatibility profile with footnotes and directive attributes; definition lists, generic attributes and grid tables remain unsupported.

```csharp
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var document = MarkdownParser.Parse("# Guide\n", "docs", "guide.md", options: options);
Console.WriteLine(document.ProfileId);
```

`AnalyzeFootnotes` alone defaults to `syntamark-docs/2`; ordinary parse/analysis/session entries retain the compatibility default. Inspect `Capabilities`, diagnostics and coverage for the chosen profile instead of inferring full support from its name. The [Conformance runner](../tests/Syntamark.Conformance/README.md) creates an inventory from version-pinned official CommonMark examples; passing known examples does not guarantee every possible input. Unsupported syntax may remain literal text.

<a id="facts"></a>
## Facts, decoded projections and positions

`Parse` returns `MarkdownDocument` with front matter, headings, sections, links, fences and text regions. Headings expose `RawLevel`, `LocalKey`, `Anchor` and text projection. Sections expose `ParentLocalKey`, `HeadingLocalKey`, `DirectBodySpan` and `SubtreeSpan`. Links distinguish inline/autolink/reference-use/reference-definition and `Resolution`; `Image` marks image syntax, and `TargetRawSpan` may refer to definition text outside a use's own span. Fences expose marker/info/content/closing positions and `Closed`. Text regions retain their projection kind. These are syntax facts, not validated routes or external references.

Local keys identify facts within a result, not persistent objects across edits. A section's direct body excludes its own heading and child sections; its subtree includes them. Unresolved references have no resolved target; inspect `Resolution` and nullable `Target` instead of treating null as an empty URL.

`RawSpan` is a zero-based UTF-16 half-open range in the original input, including any BOM and original line endings. CRLF occupies two units; a supplementary Unicode character occupies two units. `GetLinePosition` returns one-based line and column values; an offset at the LF within CRLF maps to the next line, column 1. Null position means unknown or inapplicable, not a known zero-width range. Do not strip a header or normalize newlines before using positions against the original snapshot.

```csharp
var document = MarkdownParser.Parse("# A &amp; B\n", "docs", "guide.md");
var heading = document.Headings[0];
var mapping = heading.Text.Map(new RawSpan(2, 1));
Console.WriteLine(mapping.Precision);
foreach (var fragment in mapping.RawFragments)
    Console.WriteLine(fragment);
```

`MarkdownTextProjection.Text` is decoded text; `SourceSegments` preserve `Linear`, `Atomic` and `Unknown` correspondence. `Map` accepts a decoded range and returns ordered raw fragments with `Exact`, `CoveringTokens`, `Partial` or `Unknown` precision. Entities/escapes can map to whole tokens; excluded markup can split origins. Keep separate fragments rather than replacing their enclosing range. Parser completion, projection coverage and exact editability are separate properties. For transformations across multiple snapshots and stale-edit checks, use [source maps and edits](migration-v2.md#editing-positions).

<a id="analysis"></a>
## Selected facts and unsupported candidates

`Analyze` publishes only requested outputs. `FrontMatter`, `Headings`, `Sections`, `Links`, `Fences`, `TextRegions` and `Syntax` are primitive parts. `AllFacts=63` and `All=127` retain their meanings; neither includes `UnsupportedSyntax=128`. `None` still performs mandatory parsing/validation. Selecting fewer outputs does not disable input checks, front matter validation, reference resolution, cancellation or budgets.

```csharp
var analysis = MarkdownParser.Analyze("# Guide\n\n[API](api.md)\n",
    MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links,
    "docs", "guide.md");
if (analysis.GetAvailability(MarkdownAnalysisParts.Links) == MarkdownAnalysisAvailability.Complete)
    Console.WriteLine(analysis.Links!.Count);
```

Pass exactly one primitive part to `GetAvailability`. It distinguishes `NotRequested`, `Complete`, `Partial` and `Unavailable`; unrequested/unavailable properties are null. Only a complete empty output establishes absence within that profile's supported scope. Syntax can be unavailable after a resource stop while other selected facts remain partial. `UnsupportedSyntax` explicitly requests literal extension-like footnote, definition-list, generic-attribute and grid-table candidates; it is not a rendering extension or a product diagnostic. Enabled docs-profile footnotes are not unsupported candidates. Code/math/HTML tokens and link destinations are excluded using the existing parse.

The portable adapter `MdParser.AnalyzeWithBlocks` retains legacy rendering blocks from the same parse without requiring `Syntax`. Those internal block positions are body-relative; public facts/candidates are complete-input-relative. Blocks can survive a later projection limit but are null if block parsing did not finish. This payload is not a second public AST. [Selected analysis](selective-analysis.md) provides further English examples.

<a id="inputs"></a>
## Front matter, header scan and body input

Document input recognizes front matter according to the selected profile. Check `FrontMatter.State`: `Absent`, `Parsed`, `Empty`, `Invalid`, `Unterminated` and `Unknown` are distinct. Invalid/partial YAML is not a valid typed content object. Native syntax and compatibility-node coordinates differ as described in [YAML front matter](native-yaml.md#markdown-front-matter).

```csharp
var header = MarkdownParser.ScanFrontMatter("---\ntitle: Guide\n---\n# Content\n");
Console.WriteLine(header.State);
```

`ScanFrontMatter` only scans delimiters and header UTF-16 under `frontmatter-scan/1`. It returns nullable marker/YAML/body/failure ranges and diagnostics. `Closed`/`Complete` does not validate YAML or the body. Input and scan budgets apply to the header prefix, so a closed header is independent of later body size or syntax. Budget exhaustion is not confirmed untermination; confirmed untermination reports `LMD004` and an EOF failure span. The scanner's expected next `FailureLine` can exceed the physical EOF line. Body-mode options are rejected by this entry.

`new MarkdownParseOptions(MarkdownInputKind.Body)` disables envelope recognition on an already transformed body; `BodySpan=(0, raw.Length)`, including a BOM. `new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody)` instead recovers an unterminated document envelope as body, retaining `Unterminated`, `Partial` and `LMD004`; strict recovery remains the default. Recovery does not apply to CommonMark or body input. Keep original document and transformed-body identities separate.

<a id="editing"></a>
## Immutable editing sessions

`CreateSession` returns a full facts/syntax session. `CreateAnalysisSession` returns a distinct selected-analysis session with `Analysis` and no full `Document` facade. Its original part selection survives both reuse and full fallback. `ApplyEdits` takes the previous text hash and sorted, disjoint UTF-16 edits with distinct starts against that one previous complete input. Out-of-range edits, null replacements and a mismatched hash are rejected. Null options retain previous options; cancellation/failure leaves the old session unchanged. Normalize successive LSP buffer changes to one snapshot or apply them sequentially.

```csharp
var previous = MarkdownParser.CreateAnalysisSession("Alpha\n\nBeta\n\nGamma\n",
    MarkdownAnalysisParts.UnsupportedSyntax, "docs", "guide.md", sourceVersion: "1");
var next = previous.ApplyEdits(previous.Analysis.TextHash,
    new[] { new MarkdownTextEdit(new RawSpan(7, 4), "Beta {#section}") }, sourceVersion: "2");
Console.WriteLine(next.Analysis.GetAvailability(MarkdownAnalysisParts.UnsupportedSyntax));
```

`Mode` and `Fallback` explain `Delta` or canonical `Full` parsing. Reuse is conservative: isolated single-line plain-text paragraphs can be reused while reference resolution, other blocks, publication and hashing still run. Options changes, incomplete predecessors, unsupported edits and resource boundaries can force full parsing. Results have new-snapshot coordinates; cross-edit node identity is not guaranteed. `ReusedBlocks`, `RebasedBlocks`, `ParsedBlocks`, `ScanUnits` and `OutputItems` are reuse/logical-budget counters, not timing or allocations. The portable `MdParseSession.CreateForAnalysis` additionally retains the internal body-relative blocks described above. See [editing examples and fallback reasons](selective-analysis.md#analyze-edited-snapshots).

<a id="prose"></a>
## Prose selection and optional proofreading

The [prose sample](../samples/Syntamark.Prose/) demonstrates a consumer policy over one syntax/facts snapshot. It selects heading and paragraph text, compatibility-profile table cells and link labels. It excludes code, images/alt, autolinks, HTML tokens/blocks, math, recognized front matter and reference definitions, with reasons. It keeps text between inline HTML tags. CommonMark bare URLs remain text for the consumer to protect. Segments are not joined across excluded ranges, even zero-width exclusions; unknown mappings stop selection rather than inventing text.

```powershell
dotnet run --project samples/Syntamark.Prose/Syntamark.Prose.csproj -c Release -- --yomiyasu python /path/to/yomiyasu_lint.py guide.md
```

The caller supplies Python and the lint script; normal parsing/sample execution starts no proofreading process. Explicit invocation sends each selected decoded segment separately using `--json --no-words -`. Whole-document rules and project word lists are not applied automatically. The bridge is a sample, not a general definition of natural-language prose or an automatic correction API.

Yomiyasu normalizes one leading BOM and CRLF/CR to LF and uses Python code-point positions. Syntamark uses original UTF-16. The sample's `YomiCoordinates` records these boundaries and rejects reverse positions inside surrogate pairs, CRLF or the removed BOM. Line-only lint JSON diagnostics map to ordered original fragments with at most `CoveringTokens` precision, never exact automatic edits. Calls time out after 30 seconds; nonzero exit, unsupported JSON and invalid line numbers are failures. Consumers own semantic proofreading, stale-version checks and edit application. See the [sample policy and coordinate examples](prose-consumer.md) in Japanese and the [guarded edit guide](migration-v2.md#editing-positions).

<a id="resources"></a>
## Resource budgets, cancellation and identities

`MarkdownParseOptions` defaults are `MaxInputUtf16=1048576`, `MaxOutputItems=131072`, `MaxNestingDepth=200` and `MaxScanUnits=16777216`, all positive. Input rejection is `Failed`; a later work-budget stop is `Partial` with resource diagnostics and incomplete coverage. Already published facts may survive. `LMD002` identifies input rejection and `LMD003` a resource stop. Inspect each output's availability and each nullable position; `Coverage` does not guarantee all syntax ranges. Markdown cancellation throws `OperationCanceledException`; invalid options and ranges are argument errors. Generic YAML has a separate phase-dependent cancellation contract and six budgets in [Native YAML](native-yaml.md#limits).

| Diagnostic | Meaning |
|---|---|
| `LMD001` | Unpaired UTF-16 surrogate in the inspected input |
| `LMD002` | Input limit rejection |
| `LMD003` | Work/output/depth resource stop |
| `LMD004` | Confirmed unterminated front matter |
| `LMD005` | Invalid or unsupported front matter YAML, including a non-mapping root |
| `LMD006` | Informational incomplete decoded-to-raw mapping |

Directive diagnostics `LMD020`-`LMD024` are described in the [syntax guide](syntax-design.en.md); footnote diagnostics `LMD030`/`LMD031` are described in [migration](migration-v2.md#markdown-profiles-and-footnotes). Generic YAML diagnostics use their own `Kind` and `Reason` rather than these Markdown codes.

Results identify scope/source/version, text hash, parser stamp, Markdown/YAML profiles and effective options. Full facts use contract `2.0`; selected analysis uses `analysis/2`, with analysis schema 1. Header-only scan has its own contract. Cache by input snapshot, parser/contract/profile/options and selected request (`AnalysisHash`), not `OptionsHash` alone. Body mode and recovery can change effective identities. Invalidate 1.x results on migration to the native YAML contract. Partial, unknown, unrequested and unavailable are not successful absence. The [portable safety checks](../tests/Syntamark.Safety/) exercise bounded and canceled parsing without introducing a production dependency.

<a id="measurement"></a>
## Measurement and migration

The [benchmark projects](../benchmarks/) measure parsing, syntax, projection mapping and line lookup on declared inputs. Compare equivalent profiles, outputs, packages, process configuration and baselines; collect elapsed time, allocations and retained memory separately. Logical session counters do not establish a speedup, and new YAML/syntax work must be distinguished from regressions. See the [benchmark invocation guide](../benchmarks/Syntamark.Benchmarks/README.md) and [measurement notes](performance.md).

Use the [paired migration guide](migration-v2.md) for the major-version dependency, profile, footnote, mapping and editing changes. Package version, parser stamp, profile identifier, contract version and options schema are separate identities. A private candidate or private stable verification does not establish formal publication; update runtime/Source references and expected stamps together when adopting an issued pair.
