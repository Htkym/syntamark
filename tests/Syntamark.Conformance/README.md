# CommonMark inventory runner

Run from the repository root:

```powershell
dotnet restore tests/Syntamark.Conformance --locked-mode
dotnet run --project tests/Syntamark.Conformance -c Release --no-restore -- --output .local/commonmark
dotnet run --project tests/Syntamark.Conformance -c Release --no-build -- --example 191 --output .local/commonmark-191
dotnet run --project tests/Syntamark.Conformance -c Release --no-build -- --profile commonmark/0.31.2 --syntax --output .local/commonmark-standard
```

The default command inventories known differences and exits zero when the runner completes.
`--strict` exits nonzero for syntax failures, renderer limitations, or facts invariant failures.
`--revision LABEL` records the source revision/working-tree label supplied by the caller; no Git command is executed by the runner.
`--assembly PATH` loads a previously built runtime `Syntamark.dll` in an isolated assembly context;
the same syntax adapter, facade checks, and facts invariants then run against that revision.
This supports baseline/candidate comparison without modifying a historical fixture or product friend declarations.
Unknown options, fixture/adapter self-check failures, execution exceptions, and facts invariant failures always fail the process.
An example selection keeps all 652 identities in the report and marks unselected rows `not-run`.
`--syntax` additionally calls the public `ParseWithSyntax` entry and reports independent syntax invariants:
known raw bounds, child containment, sibling order/non-overlap, and the count of unknown raw positions.
Semantic destination ranges are not treated as children. A missing entry on a historical DLL is `not-observable`.
Syntax invariant failures fail inventory mode too; these checks do not count as HTML passes.

`results.json` contains per-example expected and actual HTML, classification, public facts status,
and a separate facts invariant result. `summary.md` contains chapter counts with the full denominator.
`passed`, `failed`, `not-observable`, `skipped`, and `not-run` are separate; this version does not skip examples.

The default profile is **lithosharp-markdown/1**, which includes extensions.
`--profile commonmark/0.31.2` selects the opt-in standard profile and records its identity in the output;
use separate output directories so the results are not mixed.
The grammar observation uses `ParseFactsBlocks` with explicit input/output/depth/scan limits and a test-only renderer.
The public facade is also called to detect front-matter preprocessing and resource diagnostics; these cannot count as a syntax pass.
Facade `Partial` due to coverage/mapping is reported independently from the internal grammar result.
The additional parse is test instrumentation, not a proposed product architecture or performance workload.

The renderer understands every standard node represented by the current internal AST. It does not invent HTML nodes or discard unknown nodes.
Extension/unknown nodes it cannot represent produce `not-observable`, never a pass.
In the legacy profile, info strings containing backslash escapes or entities are also `not-observable`:
the AST stores raw info and this adapter does not implement another Markdown decoder to judge it.
In the standard profile the parser supplies decoded info, which is rendered directly.
Hand-built test DTOs exercise HTML escaping, Unicode destination encoding, headings, code, list wrappers,
quote wrappers, links/images, and line breaks before parser examples run.
HTML mismatches may still arise from adapter mistakes. Inspect the saved output before classifying them as parser defects.
There is no broad whitespace or DOM normalization that could hide differences.

Facts checks cover raw range bounds, ordered decoded segment coverage, and Linear raw/decoded equality.
These invariants complement independently expected raw slices and mapping fixtures in Portability;
they do not establish missing facts, exact node spans, reference semantics, or complete source mappings.
An HTML match is not a facts accuracy result. No test adapter is included in product packages.
