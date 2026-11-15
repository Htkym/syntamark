# Bounded Markdig comparison

This non-shipping .NET 10 runner pins Markdig 1.5.1 and Roslyn Common 4.14.0. It references an explicit isolated Syntamark DLL, with no product ProjectReference or YamlDotNet dependency. The saved 1.x results retain their historical YamlDotNet 18.1.0 environment; they do not measure the native YAML candidate. This runner does not measure a compiler/analyzer host or a formal package release.

From the repository root, copy a verified Release build first:

```powershell
New-Item -ItemType Directory .local/comparison-benchmark-current -Force
Copy-Item src/Syntamark/bin/Release/net10.0/Syntamark.dll .local/comparison-benchmark-current/Syntamark.dll
$env:NUGET_PACKAGES = 'C:\Users\h_tky\AppData\Local\Temp\syntamark-nuget-packages'
$env:NUGET_SCRATCH = Join-Path (Get-Location) '.local/comparison-benchmark-nuget-scratch'
$env:NUGET_HTTP_CACHE_PATH = Join-Path (Get-Location) '.local/comparison-benchmark-nuget-http'
dotnet restore benchmarks/Syntamark.Comparison --locked-mode
dotnet build benchmarks/Syntamark.Comparison -c Release --no-restore -m:1
pwsh -NoProfile -File benchmarks/Syntamark.Comparison/Run.ps1 -OutputDirectory .local/comparison-benchmark-results/new-run
pwsh -NoProfile -File benchmarks/Syntamark.Comparison/Summarize.ps1 -ResultsDirectory .local/comparison-benchmark-results/new-run
```

Use the repository's applicable build wrapper when required by local instructions. `SyntamarkAssembly` can override the HintPath. The default copy is intentional: rebuilding another project cannot silently change the benchmark input after copying. Each result records actual assembly hashes and informational versions, so a stale generated parser stamp cannot identify a modified candidate alone.

To compare `Analyze(Headings | Links | Fences)` with the same bounded Markdig records, use a DLL with the analysis API and enable its optional benchmark code:

```powershell
dotnet build benchmarks/Syntamark.Comparison -c Release --no-restore -p:SyntamarkAnalysis=true
$env:DOTNET_TieredCompilation = '0'
dotnet benchmarks/Syntamark.Comparison/bin/Release/net10.0/Syntamark.Comparison.dll A requested-a .local/comparison-benchmark-results/requested-a.json --requested-common
```

Run separate A/B processes with fresh output paths and the same pinned assemblies. Requested mode validates the request identity, availability, common facts and locations before timing. It measures common facts and locations only; representation and preparsed substring mapping are excluded. Full and requested modes perform different work. Default builds retain the original three-argument full mode and can reference older DLLs; they reject `--requested-common` rather than substituting a full parse.

`Run.ps1` starts four independent processes in A/B/B/A order, where A measures Syntamark first and B measures Markdig first within each stage/fixture. Each workload gets one complete untimed batch and five measured batches. Tiered compilation is disabled. Validation and JIT happen before measurement; no result is called cold-start latency. Files are created once, never overwritten, and no completion polling is used.

The three stages are deliberately distinct:

1. `representation-unequal-work`: Syntamark `ParseWithSyntax` produces facts plus a read-only syntax view; Markdig produces an AST with precise locations. Work differs, so this cannot establish an equivalent parse-only ranking.
2. `common-facts`: both parse and produce the same heading/link/fenced-code records. Markdig's traversal/decoding/copying adapter is timed. Syntamark's unselected facts, mapping metadata, diagnostics and resource accounting remain unavoidable extra work; their counts and coverage are recorded.
3. `snapshot-to-locations`: both materialize the same genuine Roslyn `SubText` snapshot with `ToString`, perform stage 2, and construct the same Roslyn `Location[]` with raw spans and line spans. Snapshot creation is outside timing, and validation warms its line index. The result records whether `ToString` reused the original string.

The corpus is generated deterministically at 1/24/96 units. It contains entity/escape/emoji heading and label text, inline and reference links, inline code, top-level closed code fences, and CRLF. Expected decoded values are explicit; raw tokens are unique per unit. Every common record and every Location is compared before timing with an independent expected record and UTF-16 CR/LF scanner. Hashes, counts, Complete status and resource-stop observations are saved. A difference fails the process before measurements.

Fenced-code comparison is limited to unindented closed fences with no closing trailing whitespace and the single plain info word `csharp`. Common logical code normalizes CRLF to LF and omits exactly one final line separator. Empty destinations, indented/nested/unclosed fences and arbitrary info strings are outside this equal-output subset. Adapters never intersect mismatched ranges to manufacture equality.

Arbitrary decoded-substring `Map` is excluded from both common workloads because a matching Markdig adapter is not implemented. A separate `Syntamark-only/additional-preparsed-substring-map` workload measures one entity query on an existing projection; it is neither a parse cost nor an equivalent zero-cost Markdig operation.

Timing includes allocated bytes for the current thread and GC collection deltas. Retained managed memory uses a small eight-output cohort after full collections; pre-existing raw text/snapshot/pipeline roots are excluded. These heap deltas include neither native memory nor total process RSS, and small differences are noisy. Stage 1 retains each engine's representation, whereas stages 2/3 retain only their equal common outputs; temporary parse/facts allocation still appears in allocation results. No timing or memory metric is a CI gate.

Run on a non-dedicated workstation: OS scheduling, other agent activity, CPU frequency and concurrent builds are uncontrolled. Preserve all five samples and all four process reports. Summaries should report the workload and internal-work differences before a ratio, and should avoid general library or production-backend rankings from this bounded corpus.
