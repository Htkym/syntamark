# Syntamark

English | [日本語](README.md)

Syntamark extracts Markdown and YAML syntax, headings, links, text and positions in the original source. Consumers provide HTML rendering, site rules and C# type binding. The runtime package targets .NET 10; portable source for compiler hosts targets netstandard2.0/C# 13. The public namespace is `Syntamark`.

## Installation

Use `Syntamark` and `Syntamark.Source` from the same immutable version pair. Specify the version and feed for the pair you use. A version number alone does not establish publication on NuGet.org. See the [migration guide](docs/migration-v2.md) for changes from the published `1.2.0` release.

```powershell
dotnet add package Syntamark --version 2.0.0-preview.2026111303 --source /path/to/candidate-feed
```

```csharp
using Syntamark;

var document = MarkdownParser.Parse("# Hello", "example", "hello.md");
Console.WriteLine(document.Status);
Console.WriteLine(document.Headings.Count);
```

The caller supplies `scopeId` and `sourceId` to identify the input. Offsets and lengths use UTF-16 code units in the original input. Inspect `Status`, `Coverage` and `Diagnostics` to determine what the result establishes.

`Syntamark.Source` explicitly adds portable source to `Compile`. It includes neither the runtime public facade nor friend metadata. When using runtime and Source together, select the same version pair. The included native YAML implementation requires no YamlDotNet reference.

```xml
<PropertyGroup>
  <SyntamarkIncludeSource>true</SyntamarkIncludeSource>
  <SyntamarkSourceVersion>2.0.0-preview.2026111303</SyntamarkSourceVersion>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Syntamark.Source" Version="[2.0.0-preview.2026111303]"
                    PrivateAssets="all" IncludeAssets="build" />
</ItemGroup>
```

## Parsing

The default Markdown profile is `lithosharp-markdown/1`. Select `commonmark/0.31.2` explicitly for standard Markdown, or `syntamark-docs/2` for documentation syntax with footnotes and directive attributes. Existing profile identifiers are retained. The [public feature guide](docs/public-features.md#profiles) and [migration guide](docs/migration-v2.md) describe their scope.

```csharp
var options = new MarkdownParseOptions(profileId: "syntamark-docs/2");
var syntax = MarkdownParser.ParseWithSyntax("# Guide\n", "docs", "guide.md", options: options);
```

`Parse` returns facts; `ParseWithSyntax` returns facts and read-only syntax from the same parse. `Analyze` selects particular outputs, `ScanFrontMatter` scans header delimiters only, `MarkdownInputKind.Body` accepts an already transformed body, and `CreateSession` accepts edits against a previous snapshot. Completion of parsing does not establish complete support for every syntax feature or complete positional coverage.

| Purpose | Guide |
|---|---|
| All public features, paired language guides, APIs and examples | [Public features](docs/public-features.md) |
| Selected analysis, body input, header scan and incremental parsing | [Analysis and editing](docs/public-features.md#analysis) |
| Syntax traversal, content/label positions and directive attributes | [Syntax API](docs/syntax-design.en.md) |
| Footnotes, profiles, source maps and dependency migration | [Migration to 2.0](docs/migration-v2.md) |
| Generic YAML, original input, diagnostics and limits | [Native YAML](docs/native-yaml.md) |
| Prose selection, explicit Yomiyasu invocation and diagnostic mapping | [Proofreading integration](docs/public-features.md#prose) |
| Compiler hosts and the Source package | [Runtime and Source](docs/public-features.md#packages) |
| Resource budgets, cancellation and input checks | [Resources and cancellation](docs/public-features.md#resources) |
| Measuring mapping, line lookup and editing workloads | [Measurement](docs/public-features.md#measurement) |

The project is licensed under MIT. Copyright and third-party data notices are in `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/`.
