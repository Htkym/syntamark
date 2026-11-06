# Syntamark

Syntamarkは、Markdownから見出し、リンク、front matter、元テキストの位置などを取り出すライブラリです。LithoSharpやSharpDepsを参照せずに利用できます。runtime packageの対象は.NET 10で、公開namespaceはSyntamarkです。

NuGetから、バージョンを固定して追加します。1.0.1では、NuGetパッケージのアイコンを追加しました。

```powershell
dotnet add package Syntamark --version 1.0.1
```

```csharp
using Syntamark;

var document = MarkdownParser.Parse("# Hello", "example", "hello.md");
Console.WriteLine(document.Status);
Console.WriteLine(document.Headings.Count);
```

scopeIdとsourceIdは呼び出し元が指定する入力の識別子です。位置のoffsetと長さは元入力のUTF-16 code unitを使います。結果のStatus・Coverage・Diagnosticsを確認して、解析し切れた範囲を判断してください。HTML描画、site診断、graphへの投影は呼び出し元で行います。

compiler host向けにはSyntamark.Sourceを用意しています。netstandard2.0/C# 13のportable sourceを明示的にCompileへ取り込むpackageです。runtime facadeとfriend metadataは含みません。runtime/sourceの版を揃え、YamlDotNetも正確な18.1.0を直接参照します。

```xml
<PropertyGroup>
  <SyntamarkIncludeSource>true</SyntamarkIncludeSource>
  <SyntamarkSourceVersion>1.0.1</SyntamarkSourceVersion>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Syntamark.Source" Version="[1.0.1]"
                    PrivateAssets="all" IncludeAssets="build" />
  <PackageReference Include="YamlDotNet" Version="[18.1.0]" />
</ItemGroup>
```

現在の解析契約は1.0、dialect profileは互換性を保つためlithosharp-markdown/1です。名称変更による構文追加はありません。旧LithoSharp.Markdownの固定候補と、新しいSyntamark候補は別のpackageとして保持します。

開発中のソースには `commonmark/0.31.2` の明示選択を追加しています。既定値は従来のままです。この追加は公開済み1.0.1には含まれません。

```csharp
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var standard = MarkdownParser.Parse(raw, "example", "input.md", options: options);
```

固定した公式652例の構文出力は全例で一致しています。これは全入力の完全準拠や、すべての facts の意味・位置精度を証明するものではありません。[対応表](docs/commonmark-support.md)では、互換 profile と標準 profile、構文比較と位置検証を区別しています。

汎用的な解析基盤に向けた改善状況は、[初回改善の記録](docs/foundation-progress.md)、[CommonMark 0.31.2 対応表](docs/commonmark-support.md)、[Markdig との比較](docs/library-comparison.md)、[Roslyn/source package の統合例](docs/roslyn-integration.md)にまとめています。現在の互換プロファイルは CommonMark の完全準拠を表しません。

ライセンスはMITです。著作権と依存ライブラリの通知はLICENSE、THIRD-PARTY-NOTICES.md、licenses/YamlDotNet.LICENSE.txtに記載しています。eng/markdown/source-history.jsonは移管元の履歴対応を保持します。
