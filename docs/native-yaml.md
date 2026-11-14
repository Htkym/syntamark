# Parse native YAML

[English](native-yaml.md) | [日本語](native-yaml.ja.md)

`YamlParser` provides the single-document `native-yaml/1` syntax profile without a YamlDotNet dependency. The runtime API is in `Syntamark`; the same implementation is included as internal `MdYamlParser` and related types in `Syntamark.Source`. This API parses syntax and scalar meaning. It does not construct C# objects, validate an OKF document, or apply a site's content schema.

```csharp
using System;
using Syntamark;

var yaml = YamlParser.Parse("title: Guide\ntags: [docs, api]\nenabled: true\n");
if (yaml.Status == MarkdownParseStatus.Complete && yaml.Root is { Kind: MarkdownYamlKind.Mapping } root)
{
    foreach (var entry in root.Entries)
        Console.WriteLine($"{entry.Key.Value}: {entry.Value.Kind}");
}
```

## Supported grammar

The profile accepts block and flow mappings and sequences, nesting, empty collections, plain and single/double-quoted scalars, quoted escapes, and literal/folded block scalars with indentation and chomping indicators. It implements a documented subset rather than the complete YAML grammar. A generic document root may be a mapping, sequence, or scalar. An optional document boundary belongs to that single YAML document; multiple documents produce a diagnostic. Empty input can complete with a null `Root`; this differs from a missing Markdown front matter envelope.

Mapping `Entries` preserve source order and each key/value pair. Keys must be non-null scalars. Duplicate-key comparison uses the Core scalar kind and canonical value: `true` and `True` are equal, and numeric spellings with the same value are equal, while integer `1` and string `"1"` are distinct. String keys retain their code-point values without Unicode normalization. Sequence elements are in `Children`. Scalars expose their decoded `Value`, `ScalarStyle`, `ScalarKind`, and `Projection`. `GetRawText()` returns the node's original source slice. Container values are null; do not interpret a container's scalar properties as a scalar value.

`ScalarKind` separates `String`, `Null`, `Boolean`, `Integer`, `Float`, and `Unknown` using YAML 1.2 Core interpretation. Quoted `"false"` and `"null"` are strings. Date-like values remain strings. Numeric width, overflow, non-finite values and conversion into a particular .NET type belong to the consumer; the parser does not choose them implicitly.

Anchors, aliases, merge expansion, explicit tags, directives and complex keys are outside this profile. A normal string key `<<` is allowed and does not request merge expansion. Unsupported input keeps its original source and produces an `Unsupported` diagnostic; that is not a successful typed interpretation of the feature.

## Diagnostics and partial results

`Status` is `Complete`, `Partial`, or `Failed`. Inspect it with `Diagnostics`, `Root`, and each node's `IsComplete`. Duplicate mapping keys remain separate entries and produce a diagnostic instead of last-wins behavior. A partially recovered tree can support editor display without proving that a key or reference is absent.

Diagnostics carry `Kind`, `Reason`, `Message`, and a decoded-source `RawSpan`. The `YamlDiagnosticKind` values are:

| Kind | Meaning |
|---|---|
| `Syntax` | Invalid grammar, duplicate keys, invalid keys, or non-printable source characters |
| `Unsupported` | A construct outside this profile, such as an alias or explicit tag |
| `ResourceLimit` | An input, depth, node, scalar, or processing budget was exceeded |
| `Canceled` | The generic parser observed cancellation |
| `InvalidEncoding` | Invalid UTF-8 bytes or an unpaired UTF-16 surrogate |

These categories describe parsing rather than schema validity. An empty diagnostic list does not certify a consumer's required fields or types.

## Cancellation

The result depends on the entry point and the phase that observes the token:

| Entry point and phase | Outcome |
|---|---|
| Generic `YamlParser.Parse` / `ParseUtf8` observes cancellation | `Failed` result, null `Root`, and a `Canceled` diagnostic |
| Runtime public node/diagnostic construction observes cancellation after the generic parse | `OperationCanceledException` |
| Markdown parsing, including front matter parsing | `OperationCanceledException` |
| `FrontMatter.GetYamlSyntax(cancellationToken)` observes cancellation | `OperationCanceledException`, including before returning a cached view |

A canceled result is not an empty valid YAML document. Code using these entry points should handle the relevant result and exception paths.

## Original bytes and positions

```csharp
using System.IO;
using Syntamark;

var original = File.ReadAllBytes("metadata.yaml");
var yaml = YamlParser.ParseUtf8(original);
if (yaml.GetOriginalUtf8() is { } retained)
    File.WriteAllBytes("unchanged.yaml", retained);
```

`ParseUtf8` uses strict UTF-8 and retains the original bytes, including BOM, comments, quote choices, key order and line endings. The byte snapshot and each returned copy are owned separately. `HasOriginalUtf8` and `GetOriginalUtf8()` report whether a snapshot was retained; early rejection or cancellation can prevent retention. An input rejected by the byte limit is not retained. Returning retained bytes unchanged preserves the original document even when its syntax is unsupported. It does not make the unsupported syntax usable as typed values.

`Parse(string)` creates its byte snapshot by encoding the supplied text as strict UTF-8. It cannot recover the original encoding of a file that the caller has already decoded. Use `ParseUtf8` when the original file bytes matter.

Node and diagnostic ranges use the decoded source's UTF-16 coordinates, including a retained BOM. They are not byte offsets. Rejection before decoding can leave `RawText` empty and a diagnostic span of `[0, 0)`; this is not a byte-error offset.

`Projection.Map` maps a decoded scalar range back to source fragments and reports its precision. Unchanged scalar text can map exactly; escaped quoted text can cover a larger source token. Folded or block scalar text can have an `Unknown` mapping. Inspect `Precision` and `RawFragments` before using a range for an edit. For composed transformations and versioned edits, see [the position-mapping migration guide](migration-v2.md#editing-positions).

The syntax parser does not provide a general formatter or arbitrary YAML editor. For an unchanged document, write the retained bytes. For a supported explicit edit, validate the original snapshot and expected text before changing a range. A consumer that writes new YAML must choose quoting and scalar types explicitly and verify the result by reading it again.

## Limits

`YamlParseOptions` configures the standalone YAML entry points. All six arguments must be positive integers; `maxNestingDepth` must also be at most 256. Invalid option values throw `ArgumentOutOfRangeException` when options are constructed.

| Constructor argument / property | Default | Meaning |
|---|---:|---|
| `maxInputBytes` / `MaxInputBytes` | 4,194,304 | Input UTF-8 bytes, including a BOM |
| `maxInputUtf16` / `MaxInputUtf16` | 1,048,576 | Decoded UTF-16 units |
| `maxNestingDepth` / `MaxNestingDepth` | 128 | Syntax-node depth; the root is 1, and scalar keys and values also count |
| `maxNodes` / `MaxNodes` | 131,072 | Syntax nodes; also bounds accumulated diagnostics and block scalar lines |
| `maxScalarUtf16` / `MaxScalarUtf16` | 1,048,576 | Decoded scalar UTF-16 units |
| `maxScanUnits` / `MaxScanUnits` | 16,777,216 | Logical processing budget, including numeric-key normalization |

```csharp
using Syntamark;

var options = new YamlParseOptions(
    maxInputBytes: 1_048_576,
    maxInputUtf16: 262_144,
    maxNestingDepth: 64,
    maxNodes: 32_768,
    maxScalarUtf16: 65_536,
    maxScanUnits: 4_194_304);
var yaml = YamlParser.Parse("title: Guide\n", options);
```

These are processing limits, not elapsed-time or allocation measurements. `OptionsHash`, `ProfileId`, and `ParserVersion` identify effective parsing behavior for a cache; add the caller's input identity as well. A resource-limited result must not enter a cache as a complete document.

## Markdown front matter

Markdown document parsing uses the same native parser for recognized front matter and requires a mapping root. Its existing `MarkdownYamlNode` projection keeps alternating key/value children for compatibility. `FrontMatter.GetYamlSyntax()` exposes the rich `YamlDocument` from that same parse, without parsing again; the public wrapper is created only when requested. Invalid or unsupported front matter retains the mapping projection when one is available and reports an invalid state. Check `FrontMatter.State` and document diagnostics before binding it.

The native front matter document's ranges are local to the YAML slice. Add `FrontMatter.YamlSpan.Start` to a native node or diagnostic start when mapping it to the complete Markdown input. The compatibility `FrontMatter.Root` ranges already refer to the complete input and must not be shifted again. No native document is returned when no YAML parse was performed. A front matter view has no separately retained UTF-8 snapshot. The Markdown API receives text; callers needing byte-for-byte file preservation must retain the original Markdown bytes themselves. `GetYamlSyntax()` does not copy the complete Markdown into a second byte buffer.

```csharp
using System;
using Syntamark;

const string markdown = "\uFEFF---\r\ntitle: 😀\r\n---\r\n# Guide\r\n";
var document = MarkdownParser.Parse(markdown, "site", "guide.md");
if (document.FrontMatter.State == MarkdownFrontMatterState.Parsed
    && document.FrontMatter.YamlSpan is { } yamlSpan
    && document.FrontMatter.GetYamlSyntax()?.Root is { Kind: MarkdownYamlKind.Mapping } root)
{
    var local = root.Entries[0].Value.RawSpan;
    var full = new RawSpan(yamlSpan.Start + local.Start, local.Length);
    Console.WriteLine(markdown.Substring(full.Start, full.Length)); // 😀
}
```

Front matter parsing uses the owning `MarkdownParseOptions` and shared Markdown processing budget. Passing a token to `GetYamlSyntax` controls view construction, not a new YAML parse or a separate set of limits.

`ScanFrontMatter` only identifies delimiters and source ranges. It does not parse YAML. CommonMark treats YAML-looking text as ordinary Markdown, and `MarkdownInputKind.Body` disables document-envelope recognition. These choices are independent of the generic YAML profile.
