# Syntamark

Syntamark は Markdown と YAML の構文、見出し、リンク、本文、元テキストの位置を取り出すライブラリです。HTML の描画やサイトの規則、C# 型へのバインドは利用側で行います。runtime package は .NET 10、compiler host 向けの portable source は netstandard2.0/C# 13 を対象とし、公開 namespace は `Syntamark` です。

## 導入

`Syntamark` と `Syntamark.Source` は内容を固定した同じバージョンの組として利用します。以下の例では利用するバージョンと feed を指定してください。バージョン番号だけで NuGet.org に公開されていると判断しないでください。公開済みの `1.2.0` からの変更は[移行ガイド](docs/migration-v2.md)を参照してください。

```powershell
dotnet add package Syntamark --version 2.0.0-preview.2026111303 --source /path/to/candidate-feed
```

```csharp
using Syntamark;

var document = MarkdownParser.Parse("# Hello", "example", "hello.md");
Console.WriteLine(document.Status);
Console.WriteLine(document.Headings.Count);
```

`scopeId` と `sourceId` は呼び出し元が指定する入力の識別子です。位置の offset と長さは元入力の UTF-16 code unit を使います。結果の `Status`、`Coverage`、`Diagnostics` を確認して、解析できた範囲を判断してください。

`Syntamark.Source` は portable source を明示的に `Compile` へ取り込む package です。runtime の公開 facade と friend metadata は含みません。runtime と Source を同時に使う場合は、同じバージョンの組を使用してください。YAML は同梱の実装で解析するため、YamlDotNet の参照は不要です。

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

## 解析方法

既定の Markdown profile は `lithosharp-markdown/1` です。`commonmark/0.31.2` を明示的に選ぶと標準 Markdown、`syntamark-docs/2` を選ぶと脚注とディレクティブ属性を含む文書向けの構文を使用できます。既存の profile の識別子は維持しています。各 profile の対応範囲は[対応表](docs/commonmark-support.md)と[移行ガイド](docs/migration-v2.md)を参照してください。

```csharp
var options = new MarkdownParseOptions(profileId: "syntamark-docs/2");
var syntax = MarkdownParser.ParseWithSyntax(raw, "docs", "guide.md", options: options);
```

`Parse` は facts、`ParseWithSyntax` は同じ解析から facts と読み取り専用の構文を返します。必要な情報だけを取得する `Analyze`、header の区切りだけを調べる `ScanFrontMatter`、すでに変換済みの本文を渡す `MarkdownInputKind.Body`、編集差分を渡す `CreateSession` も利用できます。解析が完了したことと、すべての構文や位置情報が完全であることは別です。

| 目的 | ガイド |
|---|---|
| 選択解析、Body 入力、header scan、増分解析 | [選択・増分解析](docs/selective-analysis.md) |
| 構文の探索、内容・ラベル・属性の位置 | [構文 API](docs/syntax-design.md) |
| 脚注、profile、位置対応、依存の移行 | [2.0 への移行](docs/migration-v2.md) |
| 汎用 YAML、原文保持、診断、制限 | [Native YAML](docs/native-yaml.md) |
| 本文の抽出、Yomiyasu の明示実行、診断位置の復帰 | [校正連携サンプル](docs/prose-consumer.md) |
| compiler host と Source package | [Roslyn 統合](docs/roslyn-integration.md) |
| 処理予算、キャンセル、入力の制限 | [安全性](docs/safety.md) |
| Map、行位置、測定条件 | [性能](docs/performance.md) |

プロジェクトのライセンスは MIT です。著作権と第三者データの通知は `LICENSE`、`THIRD-PARTY-NOTICES.md`、`licenses/` に記載しています。
