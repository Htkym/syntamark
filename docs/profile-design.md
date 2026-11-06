# Markdown profile の選択

既定値は拡張を含む互換 profile `lithosharp-markdown/1` である。CommonMark 0.31.2 を使う場合は `commonmark/0.31.2` を指定する。

```csharp
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var document = MarkdownParser.Parse(raw, "scope", "document.md", options: options);
Console.WriteLine(document.ProfileId);
```

## 入力の解釈

CommonMark profile は front matter を暗黙に取り除かず、入力全体を body として扱う。冒頭の `---` も Markdown の文法で解釈する。BOM や改行を削除・正規化して raw offset を変えることはない。

table、task marker、strike、裸 URL、数式、directive、MDX の特別扱いは無効であり、Markdown として成立する部分は通常どおり解釈する。heading slug はライブラリの派生 policy で、CommonMark が規定する識別子ではない。

HTML は block と inline token として認識する。block 内から Markdown の heading や link を取り出さず、inline のタグ間にある Markdown は解析する。HTML 属性は link facts に含めない。raw HTML は自然文へ展開せず、`rawHtmlBlock` / `rawHtmlInline` の unprojected region に元の range を示す。HTML renderer、DOM、MDX の実行機能は提供しない。ESM や JSX expression を MDX 固有の不透明領域として扱うこともない。

## 結果の識別と状態

解析結果の `ProfileId` は選択した profile を表す。両 profile の `ContractVersion` は `1.0`、options schema は `1` である。未知の profile/schema と不正な予算値は拒否する。CommonMark の仕様版、package version、parser source stamp はそれぞれ別の識別子である。

options hash は profile を含む、既定値を展開した6項目の canonical JSON から決まる。同じ raw 入力でも profile が異なれば hash は異なる。既定 options の SHA-256 は `970d603ab09c239dde63511f42543a76b80b8897a323e335a4cf57685d1c8591` である。

CommonMark profile の `Status` は解析の終了状態を表す。HTML を自然文へ投影しないことや decoded mapping が不明であることだけでは `Partial` にしない。それらの制約は `Coverage` の `DecodedMapping: Partial`、unprojected regions、理由、情報診断で確認できる。scan/output/depth の予算停止は `Partial`、入力拒否は `Failed`、キャンセルは例外となる。互換 profile の status と coverage の意味は異なる場合があるため、profile と併せて確認する。

`markdownSyntax` capability は CommonMark profile でも `Partial` である。`Complete` や profile の選択を、全入力への完全準拠と同一視しない。[対応範囲と制約](commonmark-support.md)を参照。

## runtime と source 配布

runtime と source は同じ options/profile を扱う。artifact metadata の `profileId` は既定 profile を表し、`defaultProfileId` と `supportedProfileIds` でも既定値と利用可能な profile を確認できる。個々の document の profile は解析時に決まる。

CommonMark profile で YAML を解析しない場合も、source 配布には互換実装が含まれるため、固定された YamlDotNet 依存を含めて導入する。
