# Exploring syntax

[English](syntax-design.en.md) | [日本語](syntax-design.md)

`MarkdownParser.ParseWithSyntax` returns `Facts` and a read-only `Root` from one parse. Use it to inspect node kinds and parent-child structure. The ordinary `Parse` entry does not create or retain a public syntax tree.

```csharp
using System;
using System.Linq;
using Syntamark;

const string markdown = "# Guide\n\n[API](/api)\n";
var parsed = MarkdownParser.ParseWithSyntax(markdown, "sample", "guide.md");
if (parsed.Root is { } root)
{
    foreach (var node in root.DescendantsAndSelf()
        .Where(node => node.Kind == MarkdownSyntaxKind.Heading))
        Console.WriteLine(node.GetRawText());
}
```

A non-null `Root` does not imply that `Facts.Status` or `Facts.Coverage` is complete. Do not use incomplete results to conclude that syntax or definitions are absent. See [selective and incremental analysis](selective-analysis.md) for requesting only the outputs you need.

## Nodes and snapshots

`MarkdownSyntaxNode` exposes `Kind`, `RawSpan`, `Children`, and values applicable to its kind. There is no public API for constructing or modifying nodes. Keep a fallback for unknown values when switching on `Kind`, so that consumers can handle future additions.

| Property | Applicable kinds and meaning |
|---|---|
| `HeadingLevel` | Heading level |
| `OrderedListStart` / `IsTightList` / `TaskChecked` | Applicable list or ListItem details |
| `Text` | Text, code, HTML, math, autolink, or line-break text; null for containers |
| `CodeInfo` | Code-block information such as its language |
| `Target` / `Title` / `TargetRawSpan` / `ReferenceLabel` | Applicable link, image, or autolink information |
| `ContentRawSpan` / `Content` | Heading, link, image, or code-block content range and projection |
| `ReferenceLabelRawSpan` | Original label position for links, images, and footnotes |
| `TableCells` | Table cell details; an empty collection for other kinds |
| `Directive` | Syntax details for colon-fenced directives and admonitions; null for GitHub alert quotes |
| `FootnoteNumber` / `FootnoteOccurrence` | Footnote display number and use order for the same label |
| `FootnoteNormalizedLabel` / `FootnoteIsDuplicate` | Footnote lookup label and duplicate-definition state |

Inapplicable nullable values are null. A Paragraph's `Text` and `Content` are also null; use facts' `TextRegions` for prose projections. GFM table `Children` contain inlines in document order, without row or cell syntax nodes. HTML blocks and inlines, directives, admonitions, math, and strikethrough have distinct kinds.

The root covers the entire input, including a BOM and front matter. Front matter remains in facts rather than becoming a child node. `GetRawText()` returns the substring for a node's original range, or null when the position is unknown. It does not regenerate source from syntax.

Retaining a node also retains the original string. There are no parent pointers, persistent node IDs, or node-identity guarantees across edits. Associate diagnostics and edit proposals with `Facts.SourceId` and `Facts.SourceVersion`; check the target version and expected original text before applying a change.

## Original positions and decoded content

`RawSpan` is a half-open UTF-16 range `[Start, End)` in the original .NET string, with `End = Start + Length`. A surrogate pair such as an emoji and a CRLF line ending each count as two code units. These positions are distinct from UTF-8 byte offsets and Unicode code-point counts. An unknown null range is also distinct from a known range with `Length = 0`.

A node's `RawSpan` includes its markers. Containers can include internal prefixes and whitespace; whether the last line ending is included depends on the block kind. HardLineBreak includes a backslash or at least two trailing ASCII spaces and the original line ending.

`ContentRawSpan` encloses the original content. For headings it excludes heading markers and is null when the content occupies separate original ranges. For links and images it excludes the display label's outer `[]`, but is null when a position cannot be established, including multiline labels. For fenced code blocks it excludes fence lines and can include quote or list prefixes inside the block. An indented code block's `ContentRawSpan` is null.

`Content` is a `MarkdownTextProjection` of display text or code content. For example, `# A &amp; 😀` has `Content.Text` equal to `A & 😀`; mapping the `&` character returns its original `&amp;` token. Code and HTML line endings can be normalized. Indented-code `Content` contains text, with `Unknown` original-position precision.

| `Map` result `Precision` | Meaning |
|---|---|
| `Exact` | Character-to-source correspondence is known; `RawFragments` can still contain multiple ranges |
| `CoveringTokens` | Covers tokens such as entities, escapes, or normalized line endings; not an exact character-level replacement range |
| `Partial` | Mixes known original ranges and unknown portions |
| `Unknown` | No original range can be established |

A range passed to `Map` is a UTF-16 range in `Content.Text`. Before using it for an automatic edit, inspect its precision and every `RawFragments` entry. An envelope enclosing several fragments is not necessarily a safe replacement range for the whole interval.

When emphasis in the compatibility profile has contradictory parent, child, or sibling positions, that emphasis node's `RawSpan` is null. Existing AST and fact recognition and positions remain unchanged. This handling does not apply to `commonmark/0.31.2`. Check the projection coverage reported by `Facts.Coverage` separately from whether each node has a known position.

For reference-style links, `ReferenceLabelRawSpan` points to the label content without the outer `[]`. Collapsed and shortcut references use the display-label position. Positions that cannot be established safely, such as multiline link labels, are null. Definitions remain in source order as `ReferenceDefinition` entries in `Facts.Links`; a reference use's `TargetRawSpan` can point outside the use node. Unresolved candidates appear as Text and unresolved link facts; definitions are not syntax children of their uses.

## Table cells

`TableCells.Row` and `Column` are zero-based. A real cell's `RawSpan` excludes leading and trailing whitespace, while an escaped `\|` retains both original characters. `Content` provides decoded cell content and its position projection.

A real empty cell has a zero-length `RawSpan`. An empty cell synthesized to fill a missing column has `RawSpan = null` because it does not exist in the input. Distinguish the presence of an original position even when both cells have empty display text.

```csharp
using System;
using System.Linq;
using Syntamark;

const string markdown = "| Name | Value |\n| --- | --- |\n| item | |\n| tail |\n";
var parsed = MarkdownParser.ParseWithSyntax(markdown, "sample", "table.md");
if (parsed.Root is { } root)
{
    var table = root.DescendantsAndSelf()
        .Single(node => node.Kind == MarkdownSyntaxKind.Table);
    foreach (var cell in table.TableCells)
    {
        var raw = cell.RawSpan is { } span
            ? markdown.Substring(span.Start, span.Length)
            : "<synthetic>";
        Console.WriteLine($"{cell.Row},{cell.Column}: '{raw}'");
    }
}
```

The second column of the `item` row is a real empty cell; the second column of the `tail` row is synthesized. Tables are supported by the compatibility profile and `syntamark-docs/2`. `commonmark/0.31.2` does not parse GFM tables as tables.

## Directive editing information

`syntamark-docs/2` interprets attributes such as `:::Card title="Guide" count=2`. It does not change the meaning of the default `lithosharp-markdown/1` or `commonmark/0.31.2` profile. Older profiles do not interpret attributes, and the compatibility profile retains its existing header-title meaning.

`Directive` exposes `NameRawSpan`, `OpeningMarkerRawSpan`, `HeaderRawSpan`, `ContentRawSpan`, and nullable `ClosingMarkerRawSpan`. The content range excludes fence lines but can include raw container prefixes. Code blocks and literal regions are not reinterpreted as directives.

Names accept ASCII letters and digits, `-`, and `_`; attribute names also accept `:` and `.`. `Attributes` retains source order and duplicates. Each attribute has separate `RawSpan`, `NameRawSpan`, `ValueRawSpan`, `OpeningQuoteRawSpan`, and `ClosingQuoteRawSpan` positions. Duplicate names are compared case-sensitively.

Attributes use `name=value` syntax with single-quoted, double-quoted, or unquoted values. Inside quotes, only escaped matching quotes and backslashes are decoded into `Value.Text`; all other text remains literal. `ValueRawSpan` excludes quote marks, and `Value.Map` maps decoded positions back to original tokens. A missing value has null `Value` and `ValueRawSpan`; an unclosed quote has null `ClosingQuoteRawSpan`.

| Diagnostic ID | `Reason` and meaning |
|---|---|
| `LMD020` | `missingName` / `attributeName`: a missing name or a symbol that cannot be read as an attribute name |
| `LMD021` | `missingValue`: an attribute is missing `=` or a value |
| `LMD022` | `unclosedQuote`: an unclosed quoted value |
| `LMD023` | `duplicateAttribute`: a duplicate attribute name |
| `LMD024` | `unterminated`: an unclosed container |

Diagnostics are available through `Directive.Diagnostics` and the document's `Facts.Diagnostics`. `IsComplete` is true when a closing fence exists and there are no directive diagnostics. Partial results retain attributes that could be parsed. Do not infer missing values or positions when proposing an edit.

```csharp
using System;
using System.Linq;
using Syntamark;

const string markdown = ":::Card title=\"Guide\"\nBody\n:::\n";
var options = new MarkdownParseOptions(profileId: "syntamark-docs/2");
var parsed = MarkdownParser.ParseWithSyntax(markdown, "sample", "page.md",
    sourceVersion: "r1", options: options);
if (parsed.Facts.Status == MarkdownParseStatus.Complete && parsed.Root is { } root)
{
    var directive = root.DescendantsAndSelf()
        .Single(node => node.Kind == MarkdownSyntaxKind.Directive).Directive;
    if (directive is { IsComplete: true })
    {
        var title = directive.Attributes.Single(attribute => attribute.Name == "title");
        if (title.ValueRawSpan is { } span
            && title.OpeningQuoteRawSpan is not null
            && title.ClosingQuoteRawSpan is not null)
        {
            var expected = markdown.Substring(span.Start, span.Length);
            Console.WriteLine($"{parsed.Facts.SourceId}@{parsed.Facts.SourceVersion}: "
                + $"[{span.Start},{span.End}) '{expected}'");
        }
    }
}
```

This example obtains a position and expected text for a value-edit proposal while preserving its quotes. The consumer applies the edit, checks the target version and original text, and ensures that the new value can be represented using that quoting style. C# props binding, component registration, and site-specific diagnostics are outside Syntamark's scope.

## Footnotes

`syntamark-docs/2` recognizes `[^source]` references and `[^source]: ...` definitions. Syntax kinds are `FootnoteReference` and `FootnoteDefinition`. `ReferenceLabel` retains the original label, while `ReferenceLabelRawSpan` points to the label content without `[^` and `]`.

`FootnoteNormalizedLabel` is the lookup value. `FootnoteNumber` follows first resolved-reference order; unresolved uses and unreferenced definitions have null numbers. `FootnoteOccurrence` is the one-based use order for the same label. A definition can have multiple blocks in `Children`, and `FootnoteIsDuplicate` distinguishes duplicate definitions. References use the first definition.

`LMD030` reports duplicate definitions; `LMD031` reports unresolved references. Consumers generate HTML footnote lists and backlinks. To obtain footnote facts without retaining a syntax tree, use `MarkdownParser.AnalyzeFootnotes`, whose default profile is `syntamark-docs/2`. Other entries require that profile to enable footnotes explicitly; other profiles retain their existing literal behavior.

## Limits and cancellation

`Root` is null when input is rejected or a resource limit stops processing before the tree is complete. A complete tree can still be available when projection limitations make facts `Partial`. Inspect `Facts.Status` and `Coverage` together.

Construction and `DescendantsAndSelf(cancellationToken)` use iterative traversal and throw `OperationCanceledException` when canceled. Read-only results support concurrent traversal. Syntax nodes, cell details, and attributes consume additional output budget, and construction consumes scan budget; processing can stop sooner than facts-only analysis with the same limits. Resource stops report `LMD003` and `Partial`, preserving facts that were already projected.

`Syntamark.Source` includes internal `MdParser.ParseWithSyntax`, `MdSyntaxDocument`, `MdSyntaxNode`, and the shared portable implementation. It excludes the runtime's public facade; consumers call internal entries through their own shim. The parser does not depend on Roslyn or a renderer. Visitors, rewriters, ownership of all trivia, and source regeneration from syntax are not provided.
