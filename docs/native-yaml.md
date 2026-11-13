# Parse native YAML

`YamlParser` provides the single-document `native-yaml/1` syntax profile without a YamlDotNet dependency. The runtime API is in `Syntamark`; the same implementation is included as internal `MdYamlParser` and related types in `Syntamark.Source`. This API parses syntax and scalar meaning. It does not construct C# objects, validate an OKF document, or apply a site's content schema.

```csharp
using Syntamark;

var yaml = YamlParser.Parse("title: Guide\ntags: [docs, api]\nenabled: true\n");
if (yaml.Status == MarkdownParseStatus.Complete && yaml.Root is { Kind: MarkdownYamlKind.Mapping } root)
{
    foreach (var entry in root.Entries)
        Console.WriteLine($"{entry.Key.Value}: {entry.Value.Kind}");
}
```

## Supported grammar

The profile accepts block and flow mappings and sequences, nesting, empty collections, plain and single/double-quoted scalars, quoted escapes, and literal/folded block scalars with indentation and chomping indicators. A generic document root may be a mapping, sequence, or scalar. An optional document boundary belongs to that single YAML document; multiple documents produce a diagnostic. An empty document remains distinct from a missing front matter envelope.

Mapping `Entries` preserve source order and each key/value pair. Duplicate-key comparison uses the Core scalar kind and canonical value: `true` and `True` are equal, and numeric spellings with the same value are equal, while integer `1` and string `"1"` are distinct. String keys retain their code-point values without Unicode normalization. Sequence elements are in `Children`. Scalars expose their decoded `Value`, `ScalarStyle`, `ScalarKind`, and `Projection`. `GetRawText()` returns the node's original source slice. Container values are null; do not interpret a container's scalar properties as a scalar value.

`ScalarKind` separates `String`, `Null`, `Boolean`, `Integer`, `Float`, and `Unknown` using YAML 1.2 Core interpretation. Quoted `"false"` and `"null"` are strings. Date-like values remain strings. Numeric width, overflow, non-finite values and conversion into a particular .NET type belong to the consumer; the parser does not choose them implicitly.

Anchors, aliases, merge expansion, explicit tags, directives and complex keys are outside this profile. A normal string key `<<` is allowed and does not request merge expansion. Unsupported input keeps its original source and produces an `Unsupported` diagnostic; that is not a successful typed interpretation of the feature.

## Diagnostics and partial results

`Status` is `Complete`, `Partial`, or `Failed`. Inspect it with `Diagnostics`, `Root`, and each node's `IsComplete`. Duplicate mapping keys remain separate entries and produce a diagnostic instead of last-wins behavior. A partially recovered tree can support editor display without proving that a key or reference is absent.

Diagnostics carry `Kind`, `Reason`, `Message`, and an original-input `RawSpan`. Kinds distinguish syntax errors, unsupported grammar, resource limits, cancellation, and invalid UTF-8. These categories describe parsing rather than schema validity. An empty diagnostic list does not certify a consumer's required fields or types.

Cancellation recognized by the generic YAML parser is retained as a `Canceled` diagnostic and an incomplete result, including a token canceled before parsing. Cancellation while constructing the runtime's public node view can instead throw `OperationCanceledException`. The Markdown parse and front matter entry points retain their existing cancellation exception contract. Check the entry point and phase rather than treating every canceled operation as an empty valid document.

## Original bytes and positions

```csharp
var original = File.ReadAllBytes("metadata.yaml");
var yaml = YamlParser.ParseUtf8(original);
if (yaml.GetOriginalUtf8() is { } retained)
    File.WriteAllBytes("unchanged.yaml", retained);
```

`ParseUtf8` uses strict UTF-8 and retains the original bytes, including BOM, comments, quote choices, key order and line endings. The byte snapshot and each returned copy are owned separately. `HasOriginalUtf8` and `GetOriginalUtf8()` report whether a snapshot was retained; an input rejected by the byte limit is not retained. Returning the bytes unchanged preserves the original document even when its syntax is unsupported. It does not make the unsupported syntax usable as typed values.

Node and diagnostic ranges always use the decoded source's UTF-16 coordinates, including a retained BOM. They are not byte offsets. `Projection.Map` maps a decoded scalar range back to original source fragments and reports its precision. Escaped or folded values can cover source tokens or multiple fragments. Do not replace a covering range as if it were one exact spelling. For composed transformations and versioned edits, see [the position-mapping migration guide](migration-v2.md#editing-positions).

The syntax parser does not provide a general formatter or arbitrary YAML editor. For an unchanged document, write the retained bytes. For a supported explicit edit, validate the original snapshot and expected text before changing a range. A consumer that writes new YAML must choose quoting and scalar types explicitly and verify the result by reading it again.

## Limits

All option values are positive; nesting depth must also be at most 256.

| Option | Default | Meaning |
|---|---:|---|
| `MaxInputBytes` | 4,194,304 | Input UTF-8 bytes |
| `MaxInputUtf16` | 1,048,576 | Decoded UTF-16 units |
| `MaxNestingDepth` | 128 | Collection nesting |
| `MaxNodes` | 131,072 | Produced syntax nodes |
| `MaxScalarUtf16` | 1,048,576 | Decoded scalar UTF-16 units |
| `MaxScanUnits` | 16,777,216 | Logical processing budget |

Use `YamlParseOptions` to change these limits. `OptionsHash`, `ProfileId`, and `ParserVersion` identify effective parsing behavior for a cache; add the caller's input identity as well. A resource-limited result must not enter a cache as a complete document.

## Markdown front matter

Markdown document parsing uses the same native parser for recognized front matter and requires a mapping root. Its existing `MarkdownYamlNode` projection keeps alternating key/value children for compatibility. `FrontMatter.GetYamlSyntax()` exposes the rich `YamlDocument` from that same parse, without parsing again; the public wrapper is created only when requested. Invalid or unsupported front matter retains the mapping projection when one is available and reports an invalid state. Check `FrontMatter.State` and document diagnostics before binding it.

The native front matter document's ranges are local to the YAML slice. Add `FrontMatter.YamlSpan.Start` to a native node or diagnostic start when mapping it to the complete Markdown input. The compatibility `FrontMatter.Root` ranges already refer to the complete input and must not be shifted again. No native document is returned when no YAML parse was performed. The surrounding Markdown snapshot owns original-byte preservation; `GetYamlSyntax()` does not copy the complete Markdown into a second byte buffer.

`ScanFrontMatter` only identifies delimiters and source ranges. It does not parse YAML. CommonMark treats YAML-looking text as ordinary Markdown, and `MarkdownInputKind.Body` disables document-envelope recognition. These choices are independent of the generic YAML profile.
