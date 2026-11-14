# Resource limits and cancellation

`tests/Syntamark.Safety` compiles the exact `Portable/**/*.cs` files into its own
net10.0, C# 13 executable with the native YAML parser. It has no runtime project
reference, friend assembly grant, copied parser implementation, or production test
hook. Run it with:

```sh
dotnet restore tests/Syntamark.Safety --locked-mode
dotnet run --project tests/Syntamark.Safety -c Release --no-restore
```

The legacy and CommonMark profiles exercise input UTF-16 length immediately below, at, and above its
configured threshold. All four limits reject zero and negative values. Output,
scan, and depth cases discover the smallest limit that completes a fixed input,
then check the adjacent limits. This tests the
observable contract without treating internal accounting totals as stable API.
Input rejection is `Failed` with reason `input`; work exhausted after parsing
starts is `Partial` with a resource diagnostic and incomplete raw coverage.
Already-published headings survive output exhaustion.

The deterministic running-cancellation case parses a prefix and an image reference,
then a custom read-only reference dictionary cancels the token during the second
successful lookup. Images return directly to the scanner loop, so its next real
`MdParseContext.Scan` observes cancellation. The test requires
`OperationCanceledException` with the original token and proves that the later
reference was never visited. A control run resolves all three references. No timer,
thread race, per-scan callback, or parser modification is involved.

Fixed, bounded fixtures cover nested brackets, incomplete destinations, unmatched
backtick runs, HTML attributes/comments, containers, and unresolved reference labels
with CR/CRLF line endings or NUL replacement. Each runs with default limits and a
deliberately small scan limit, which can stop during preprocessing; this does not
prove metering inside every adversarial syntax path. A fixed-seed corpus adds 32 short
malformed combinations per profile. All returned facts are checked for bounded raw
spans and complete ordered decoded-segment coverage; every Linear segment must
equal its original raw slice.

The generated corpus exposed an existing unresolved-reference label problem:
legacy input `[a\rb]\n` publishes label `a\nb`, but its raw `[2,3)` CR was
incorrectly marked Linear/Exact. Validation after masking and shifting now marks
that same raw interval Atomic/CoveringTokens. The text, positions, and OptionsHash
stay the same; only the false precision claim changes. Both profiles also check
CRLF and NUL replacement, including reference-definition labels. The correction
keeps the original segment accounting and does not emit already-charged segments
again.

This verifies cancellation at a real inline scanning boundary. It does not measure
latency or prove interruption inside every regex/library call, hashing, YAML
parsing, fact freezing, or public-facade conversion. Pre-cancellation and Roslyn
snapshot-read cancellation are covered separately by Portability and the Roslyn
canary. Runtime/netstandard2.0 equivalence remains the Portability gate.

Native YAML has separate byte, UTF-16, nesting, node, scalar and scan limits.
Its generic entry reports cancellation in diagnostics; Markdown entries retain
the exception behavior described above. Read [Native YAML](native-yaml.md) before
using a partial tree for schema validation. Input byte retention and a recovered
tree do not establish complete syntax or a safe editing range.

Parser sessions preserve these checks when reusing paragraphs. Replayed logical
budget charges prevent reuse from turning a resource-limited input into a
complete result solely by skipping parser work. Position maps and proposed edits
also retain snapshot identities; an earlier document version must not receive
an edit prepared for another snapshot.
