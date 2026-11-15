# Resource limits and cancellation

Markdown parsing has configurable limits for input UTF-16 length, output items,
scan units and nesting depth. All four limits must be positive. Input rejection
returns `Failed` with reason `input`; exhausted work limits return `Partial`
with a resource diagnostic and incomplete raw coverage. Facts already published,
such as headings, remain available after output exhaustion. Inspect status,
coverage and diagnostics before using a partial result.

Cancellation throws `OperationCanceledException` with the supplied token at
observed Markdown scanning boundaries. Cancellation latency is not guaranteed,
and interruption inside every regex/library call, hashing, YAML parsing,
fact-freezing step or public-facade conversion is not established. Resource
limits likewise do not establish metering inside every adversarial syntax path.

Decoded text and raw positions can differ. For example, a CR in a reference
label can become LF in the decoded text. That replacement maps as
`Atomic`/`CoveringTokens`, not `Linear`/`Exact`. Check mapping precision before
proposing an edit; do not treat a normalized character as an exact raw slice.

Native YAML has separate byte, UTF-16, nesting, node, scalar and scan limits.
Its generic parser reports detected cancellation in diagnostics, while the
runtime conversion to public nodes and diagnostics can throw
`OperationCanceledException`. Markdown entries retain the exception behavior.
Read [Native YAML](native-yaml.md) for the API-specific cancellation results
before using a partial tree for schema validation. Input byte retention and a
recovered tree do not establish complete syntax or a safe editing range.

Parser sessions preserve resource checks when reusing paragraphs. Replayed
logical budget charges prevent reuse from turning a resource-limited input into
a complete result solely by skipping parser work. Position maps and proposed
edits retain snapshot identities; an earlier document version must not receive
an edit prepared for another snapshot.
