# Syntamark

SyntamarkはMarkdownから見出し、リンク、front matter、元テキストの位置などを取り出すライブラリです。LithoSharpやSharpDepsを参照せずに利用できます。runtime packageの対象は.NET 10で、公開namespaceはSyntamarkです。

NuGetからバージョンを固定して追加します。

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

compiler host向けにはSyntamark.Sourceを用意しています。netstandard2.0/C# 13のportable sourceを明示的にCompileへ取り込むpackageです。runtime facadeとfriend metadataは含みません。runtime/sourceのバージョンを揃え、YamlDotNetも正確な18.1.0を直接参照します。

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

現在の解析契約は1.0、既定のdialect profileはlithosharp-markdown/1です。

開発中のソースには `commonmark/0.31.2` の明示選択を追加しています。既定値は従来のままです。この追加は公開済み1.0.1には含まれません。

```csharp
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var standard = MarkdownParser.Parse(raw, "example", "input.md", options: options);
```

標準 profile と互換 profile の機能・制約は[対応表](docs/commonmark-support.md)を参照してください。解析が完了したことは、すべての構文や facts の意味・位置精度を保証するものではありません。

開発版の `MarkdownParser.ParseWithSyntax` は一回の解析から既存 facts と読み取り専用の構文 view を返します。構造が必要な caller だけが明示的に使用します。[構文 API](docs/syntax-design.md)、[prose の利用例](docs/prose-consumer.md)、[Roslyn/source package の統合例](docs/roslyn-integration.md)に使い方と位置情報の限界を記載しています。

問い合わせの初期化と保持メモリの制約は[Map と行位置](docs/performance.md)を参照してください。

プロジェクトのライセンスはMITです。著作権、第三者データと依存ライブラリの通知はLICENSE、THIRD-PARTY-NOTICES.md、licenses/に記載しています。
