# Parser session candidate

This opt-in prototype is not a released package or a SharpDeps pin update. Existing `Parse` and `ParseWithSyntax` signatures, DTOs, option hashes, diagnostics and resource contracts remain unchanged. The session oracle is `ParseWithSyntax` with the same source, IDs, source version and options.

## API

- `MarkdownParser.CreateSession(rawText, scopeId, sourceId, sourceVersion, options, cancellationToken)` creates an immutable snapshot with canonical `Document` facts and optional `Root` syntax.
- `session.ApplyEdits(expectedTextHash, edits, sourceVersion, options, cancellationToken)` returns a new snapshot. Edits use old-source UTF-16 ranges and must be sorted, disjoint and have non-null replacements. The expected hash must equal `session.Document.TextHash`, including null for an input-rejected snapshot. Null options retain the old options. Null source version is published as null.
- Hash/range/version validation errors and cancellation propagate. The old session is never replaced or mutated.
- `Mode` is `Delta` only when an old paragraph parse payload was actually reused. Otherwise it is `Full`. `Fallback` is a fixed enum, not exception text.
- `ReusedBlocks`, `RebasedBlocks` and `ParsedBlocks` count eligible paragraphs only. `RebasedBlocks` identifies reused suffix payloads whose absolute spans were rebuilt; unchanged-offset nodes retain reference identity. Counts do not describe unsupported full documents. `ScanUnits` and `OutputItems` are logical canonical resource debits, including replayed work, not executed work, allocations or timings.

## Proven subset

The complete old source and new source must consist only of single-line plain paragraphs separated by at least one empty line. Nonempty lines cannot start with whitespace. Allowed content is ASCII letters, digits and spaces, plus well-formed non-whitespace non-BOM Unicode. ASCII Markdown punctuation, tabs, front matter, references, headings, setext, fences, lists, containers, HTML, MDX and unknown constructs are outside this candidate.

Each edit must be contained within an existing paragraph, including insertions at its start or end, and must not replace or insert CR/LF. Length changes are allowed. A change inside a paragraph invalidates that whole paragraph. Untouched prefix and suffix paragraphs keep their line shape and blank lookahead. Prefix blocks at unchanged offsets retain node identity. For shifted suffixes, parsed raw/text payloads and captured block/inline resource debits are retained while absolute block/inline spans, line-map offsets and raw source segments are rebuilt immutably. CommonMark inline spans shift, while legacy paragraph-local text spans stay local and their raw projection segments shift. Unrecognized retained inline shapes take full fallback. No reference or container state is reused. The parser still scans/splits the complete body and reconstructs canonical facts, sections, syntax and hashes. These steps remain O(N); there is no incremental source buffer or line-index claim.

Unsupported source/edit/options or a non-Complete old parse takes canonical full parsing. If a reuse candidate reaches a resource limit, it is discarded and the canonical parser is run without reuse, preserving its exact partial collections, diagnostic and coverage. Cached complete work cannot bypass `MaxScanUnits` or `MaxOutputItems`. Partial or Failed results never seed a reusable complete session. Unchanged new options with the same effective hash retain eligibility.

## Validation and remaining gates

The existing portability executable includes session checks for both runtime and netstandard2.0 facades. `--parser-session` selects them alone. The checks compare all public facts and syntax with full parsing, all line positions, canonical logical resource totals, and retained internal resolved-block reference identity. They cover continuous equal-length and length-changing edit/undo, branching snapshots, insertions/deletions, cumulative multi-edits, Unicode/CRLF/CR, unsupported syntax and boundary edits, invalid Unicode/hash/ranges/version, resource boundaries and cancellation. A deliberately changed oracle snapshot must be detected.

Required ordinary correctness, conformance, safety and formatting results are recorded separately in the local review bundle. No performance, overhead or model comparisons are part of this change. Multiline paragraphs, richer checkpoint/state support, performance/allocation/memory/fallback-rate gates, an independently reviewed immutable runtime/source release, and SharpDeps integration remain unfinished. Do not call this restricted prototype completion of all incremental parsing requirements.

## Runtime/source delivery

`Portable/MarkdownSession.cs` belongs to the existing canonical source file set and is included automatically in both package payloads. `Public/MarkdownSession.cs` adds the runtime facade only; source hosts use the corresponding internal `MdParseSession`/`MdTextEdit` API. The existing artifact producer must be run from the reviewed committed candidate with a fresh approved version after the remaining gates. Its canonical-source hash and generated parser stamp must agree. No official immutable-version record, runtime/source package pin or release is changed here.
