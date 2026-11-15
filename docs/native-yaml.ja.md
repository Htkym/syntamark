# ネイティブ YAML を解析する

[English](native-yaml.md) | [日本語](native-yaml.ja.md)

`YamlParser` は YamlDotNet に依存せず、単一文書用の構文プロファイル `native-yaml/1` を提供します。実行時 API は `Syntamark` にあります。`Syntamark.Source` には同じ実装が内部型の `MdYamlParser` と関連型として含まれます。この API は構文とスカラーの意味を解析します。C# オブジェクトの生成、OKF 文書の検証、サイトのコンテンツスキーマの適用は行いません。

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

## 対応する構文

ブロック形式とフロー形式のマッピング・シーケンス、入れ子、空のコレクション、引用符のないスカラー、単一引用符・二重引用符付きのスカラー、引用内のエスケープに対応します。リテラル形式・折りたたみ形式のブロックスカラーでは、インデントと末尾の改行を制御する chomping 指示子も扱います。対応範囲は YAML 文法の一部です。単独 YAML 文書のルートにはマッピング、シーケンス、スカラーを指定できます。文書境界の記号も単一文書の範囲で扱い、複数文書には診断を返します。空の入力は `Root` が `null` のまま正常に完了する場合があります。これは Markdown に frontmatter の囲みがない状態とは別です。

マッピングの `Entries` は各キーと値の組を原文の順序で保持します。キーには null 以外のスカラーを使用します。重複キーは Core スカラーの種類と正規化した値で比較します。`true` と `True`、同じ数値を表す異なる表記は等しく、整数の `1` と文字列の `"1"` は区別されます。文字列キーのコードポイントには Unicode 正規化を行いません。シーケンスの要素は `Children` にあります。スカラーからは復号した `Value`、`ScalarStyle`、`ScalarKind`、`Projection` を取得できます。`GetRawText()` はノードに対応する原文の一部を返します。コンテナの `Value` は `null` です。コンテナのスカラー用プロパティを値の判断に使わないでください。

`ScalarKind` は YAML 1.2 Core の解釈に基づき、`String`、`Null`、`Boolean`、`Integer`、`Float`、`Unknown` を区別します。引用符付きの `"false"` と `"null"` は文字列です。日時のような値も文字列のままです。数値のビット幅、オーバーフロー、非有限値の扱いと、具体的な .NET 型への変換は利用側が決めます。パーサーが暗黙に型を選ぶことはありません。

アンカー、エイリアス、マージの展開、明示タグ、ディレクティブ、複雑なキーは対応範囲外です。通常の文字列キー `<<` は使用できますが、マージを展開する指示にはなりません。未対応の入力は原文を保持し、`Unsupported` の診断を返します。原文を保持できても、その構文を型付きの値として解釈できたことにはなりません。

## 診断と部分結果

`Status` は `Complete`、`Partial`、`Failed` のいずれかです。`Diagnostics`、`Root`、各ノードの `IsComplete` とあわせて確認してください。重複キーは別々のエントリーとして残り、診断が付きます。後の値で黙って上書きすることはありません。部分的に復元できた構文木はエディター表示に使えますが、項目や参照が存在しないことの根拠にはできません。

診断には `Kind`、`Reason`、`Message`、復号後の原文を指す `RawSpan` があります。`YamlDiagnosticKind` の値は次のとおりです。

| 種類 | 意味 |
|---|---|
| `Syntax` | 構文不正、重複キー、不正なキー、原文で許可されない文字 |
| `Unsupported` | エイリアスや明示タグなど、このプロファイルが扱わない構文 |
| `ResourceLimit` | 入力、深さ、ノード、スカラー、処理量の上限を超えた状態 |
| `Canceled` | 汎用 YAML パーサーがキャンセルを検出した状態 |
| `InvalidEncoding` | 不正な UTF-8、または対になる文字がない UTF-16 サロゲート |

この分類が表すのは解析の状態です。診断が空でも、利用側が要求するフィールドや型の条件を満たすとは限りません。

## キャンセル

キャンセル時の結果は呼び出す API とトークンを確認する処理段階で変わります。

| API と処理段階 | 結果 |
|---|---|
| 汎用の `YamlParser.Parse` / `ParseUtf8` がキャンセルを検出したとき | `Failed` の結果、`null` の `Root`、`Canceled` の診断 |
| 汎用解析後に実行時の公開ノード・診断を構築する処理がキャンセルを検出したとき | `OperationCanceledException` |
| frontmatter を含む Markdown の解析 | `OperationCanceledException` |
| `FrontMatter.GetYamlSyntax(cancellationToken)` がキャンセルを検出したとき | 保存済みのビューを返す前の確認も含めて `OperationCanceledException` |

キャンセルされた結果を空の正常な YAML 文書として扱わないでください。これらの API を使うコードでは、対象となる結果と例外の両方を処理します。

## 元のバイト列と位置

```csharp
using System.IO;
using Syntamark;

var original = File.ReadAllBytes("metadata.yaml");
var yaml = YamlParser.ParseUtf8(original);
if (yaml.GetOriginalUtf8() is { } retained)
    File.WriteAllBytes("unchanged.yaml", retained);
```

`ParseUtf8` は厳密な UTF-8 として入力を読み、BOM、コメント、引用形式、キー順、改行形式を含む元のバイト列を保持します。保持するバイト列は呼び出し側の配列から分離され、取得するたびにコピーを返します。`HasOriginalUtf8` と `GetOriginalUtf8()` で保持できたかを確認してください。早い段階での入力拒否やキャンセルによっては保持されません。バイト数の上限を超えた入力は保持されません。保持したバイト列をそのまま出力すれば、未対応の構文も含めて元の文書を保存できます。ただし、未対応の構文を型付きの値として利用できるようにはなりません。

`Parse(string)` は受け取ったテキストを厳密な UTF-8 に符号化してバイト列を作ります。呼び出し側で既に復号したファイルの元の文字エンコーディングは復元できません。元ファイルのバイト列を保つ必要がある場合は `ParseUtf8` を使用してください。

ノードと診断の範囲は、保持された BOM も含めて、復号後の原文の UTF-16 位置を表します。バイト単位のオフセットではありません。復号前に入力を拒否した場合は `RawText` が空になり、診断の範囲が `[0, 0)` になることがあります。この範囲はバイト列中のエラー位置を表すものではありません。

`Projection.Map` は復号したスカラーの範囲を原文の範囲へ対応付け、精度を返します。変換のないテキストは正確に対応する場合があります。引用内のエスケープは、原文のより広いトークン範囲へ対応する場合があります。折りたたみ形式やブロックスカラーでは対応が `Unknown` になる場合があります。編集に使う前に `Precision` と `RawFragments` を確認してください。変換の合成と版を確認して行う編集については、[位置対応の移行ガイド（英語）](migration-v2.md#editing-positions)を参照してください。

この構文パーサーには、汎用の整形機能や任意箇所の YAML 編集機能はありません。無編集の文書は保持したバイト列を出力してください。対応する操作で範囲を編集するときは、元のスナップショットと期待する文字列を確認してから変更します。新しい YAML を書く利用側は引用形式とスカラーの型を明示し、再読込して結果を確認してください。

## 制限

`YamlParseOptions` は単独 YAML の API に適用します。6 個の引数はすべて正の整数で、`maxNestingDepth` は 256 以下にします。不正な値を指定すると、オプションの構築時に `ArgumentOutOfRangeException` が発生します。

| コンストラクター引数 / プロパティ | 既定値 | 意味 |
|---|---:|---|
| `maxInputBytes` / `MaxInputBytes` | 4,194,304 | BOM を含む入力の UTF-8 バイト数 |
| `maxInputUtf16` / `MaxInputUtf16` | 1,048,576 | 復号後の UTF-16 コード単位数 |
| `maxNestingDepth` / `MaxNestingDepth` | 128 | 構文ノードの深さ。ルートを 1 とし、スカラーのキーと値も数える |
| `maxNodes` / `MaxNodes` | 131,072 | 構文ノード数。蓄積する診断数とブロックスカラーの行数にも適用 |
| `maxScalarUtf16` / `MaxScalarUtf16` | 1,048,576 | 復号したスカラーの UTF-16 コード単位数 |
| `maxScanUnits` / `MaxScanUnits` | 16,777,216 | 数値キーの正規化も含む論理的な処理量 |

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

これらは処理の制限であり、実時間や割り当てメモリの測定値ではありません。キャッシュでは `OptionsHash`、`ProfileId`、`ParserVersion` と、呼び出し側が持つ入力の識別情報を使って解析条件を区別します。資源上限で停止した結果を完全な文書として保存しないでください。

## Markdown の frontmatter

Markdown 文書の解析でも同じネイティブパーサーを使います。認識した frontmatter のルートはマッピングに限定します。既存の `MarkdownYamlNode` では、互換用に `Children` へキーと値が交互に並びます。`FrontMatter.GetYamlSyntax()` は同じ解析で得た詳細な `YamlDocument` を返します。再解析は行わず、公開用のビューは要求時に作ります。構文が不正または未対応の場合は不正な状態を報告し、マッピングを取得できれば互換用の構文情報も保持します。型へバインドする前に `FrontMatter.State` と文書の診断を確認してください。

ネイティブの frontmatter 文書の範囲は切り出した YAML の先頭から数えます。ノードや診断の位置を Markdown 全体に戻すときは、その開始位置に `FrontMatter.YamlSpan.Start` を加えます。互換用の `FrontMatter.Root` の範囲は既に入力全体を指しているため、さらに加算しないでください。YAML 解析を行わなかった場合、ネイティブ文書は返しません。frontmatter のビューには独立した UTF-8 のスナップショットがありません。Markdown API はテキストを受け取るため、ファイルのバイト列をそのまま保存するには、呼び出し側で元の Markdown のバイト列を保持してください。`GetYamlSyntax()` が Markdown 全体を別のバイトバッファーにコピーすることはありません。

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

frontmatter の解析には文書の `MarkdownParseOptions` と Markdown 全体で共有する処理予算を使います。`GetYamlSyntax` に渡すトークンはビューの構築に適用します。YAML を再解析したり、別の上限を指定したりするものではありません。

`ScanFrontMatter` が調べるのは区切り記号と原文の範囲だけです。YAML は解析しません。CommonMark は YAML に見えるテキストも通常の Markdown として扱い、`MarkdownInputKind.Body` は文書全体を囲む frontmatter の認識を無効にします。これらの選択と汎用 YAML のプロファイルは別々の設定です。
