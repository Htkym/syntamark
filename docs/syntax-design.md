# 構文の探索

`MarkdownParser.ParseWithSyntax` は一回の解析から `Facts` と読み取り専用の `Root` を返します。通常の `Parse(string, string, string, string?, MarkdownParseOptions?, CancellationToken)` は公開する構文 view を作成・保持しません。

```csharp
var parsed = MarkdownParser.ParseWithSyntax(markdown, "sample", "example.md");
if (parsed.Root is { } root)
{
    var headings = root.DescendantsAndSelf().Where(n => n.Kind == MarkdownSyntaxKind.Heading);
    var quoteLinks = root.DescendantsAndSelf().Where(n => n.Kind == MarkdownSyntaxKind.BlockQuote)
        .SelectMany(n => n.DescendantsAndSelf()).Where(n => n.Kind == MarkdownSyntaxKind.Link);
    var listCode = root.DescendantsAndSelf().Where(n => n.Kind == MarkdownSyntaxKind.OrderedList)
        .SelectMany(n => n.DescendantsAndSelf()).Where(n => n.Kind == MarkdownSyntaxKind.FencedCodeBlock);
}
```

## ノードと位置

ノードは `Kind`、`RawSpan`、`Children` と、種類に応じた値を持ちます。直接生成・変更する API はありません。将来の種類の追加に備え、`Kind` の分岐には未知の値を扱う処理を残してください。

| プロパティ | 値を持つ種類 |
|---|---|
| `HeadingLevel` | Heading |
| `OrderedListStart` | OrderedList |
| `IsTightList` | OrderedList / UnorderedList |
| `TaskChecked` | task の ListItem |
| `CodeInfo` | code block |
| `Target` / `Title` / `TargetRawSpan` / `ReferenceLabel` | link / image / autolink の該当する情報 |
| `ContentRawSpan` / `Content` | heading、paragraph、code などの内容範囲と投影 |
| `ReferenceLabelRawSpan` | link / image / footnote のラベル。外側の `[]` を含まない |
| `TableCells` | Table の行・列・元位置・テキスト投影 |
| `Directive` | directive / admonition の開始・終了記号、名前、属性、構文診断 |
| `FootnoteNumber` / `FootnoteOccurrence` | documentation profile の脚注番号と参照回数 |
| `FootnoteNormalizedLabel` / `FootnoteIsDuplicate` | 脚注の解決用ラベルと定義の重複状態 |

該当しない値は null です。`Text` は Text、Code、HTML、Math、Autolink、LineBreak の葉の値です。Heading、Paragraph、Link のデコード済みテキストには既存 facts の投影を使います。code や HTML の改行は正規化される場合があります。

`RawSpan` は渡した元文字列の UTF-16 半開区間で、記号を含みます。位置不明の null と、位置が分かっている0幅の範囲は別です。コンテナーには内部の接頭辞や空白を含み、末尾改行の有無は block の種類によって異なります。HardLineBreak は backslash または末尾の2個以上の ASCII space と元の改行を含み、CRLF は2文字です。

`ContentRawSpan` は内容の連続した元範囲が確定する場合の範囲です。heading の内容が複数の元範囲へ分かれる場合は null になります。code fence の内容範囲は、その内側にある quote/list の接頭辞を含む場合があります。表示文字や復号された内容の位置には `Content.Map` を使い、精度と複数の fragment を確認してください。範囲を一つに囲むことと、その全体を安全に置換できることは同じではありません。

root は BOM や front matter を含む入力全体を覆います。front matter は children に追加せず facts に残します。`GetRawText()` は該当範囲の元文字列を返し、位置不明なら null を返します。構文から原文を再生成する機能ではありません。ノードを保持すると元文字列も保持します。parent pointer、node ID、編集をまたぐ同一性はありません。

互換 profile の emphasis で親子・兄弟の位置が矛盾する場合、その emphasis の `RawSpan` は null になります。既存 AST・facts の認識結果や位置は変更しません。この扱いは標準 profile には適用しません。`Facts.Coverage` が示す投影の網羅範囲と、各ノードの位置が既知かどうかは別に確認してください。

参照定義は `Facts.Links` の ReferenceDefinition に位置順で残ります。参照 use の child は label であり、定義への `TargetRawSpan` はノードの外を指す場合があります。これは親子関係ではありません。未解決の候補は通常の Text と unresolved facts で表します。

GFM table の下には inline を文書順に置き、row/cell の構文ノードは作りません。cell の情報は `TableCells` から取得します。元入力にない補完用の空 cell は `RawSpan=null` です。HTML は block と inline を分け、互換 profile の directive、admonition、math、strike も種類を区別します。

## ディレクティブの編集情報

`Directive` は名前、開始記号、header、内容、終了記号の範囲を別々に返します。`Attributes` は元の順序と重複を保持し、属性名、値、引用符の範囲を返します。`syntamark-docs/2` profile では `name=value` を解釈し、single quoted、double quoted、unquoted の値を扱います。引用中は対応する引用符と backslash の escape を復号し、それ以外の文字を保持します。従来の profile では header の title としての意味を維持し、属性を空の一覧として返します。

`Diagnostics` の `LMD020` は名前の不足、`LMD021` は値の不足、`LMD022` は閉じていない引用符、`LMD023` は重複属性、`LMD024` は閉じていないコンテナーを示します。部分結果では `IsComplete` と個々の nullable な位置を確認し、欠けた値や位置を推測して修正しないでください。コンポーネントの型バインドやサイト固有の診断は利用側の責務です。

## 制限とキャンセル

入力拒否や、構文・facts・ツリー構築中の資源制限による停止では `Root` が null になります。投影の制約で facts が Partial でも、完成したツリーが存在する場合があります。`Facts.Status` と `Coverage` を確認し、不完全な結果から構文や定義が存在しないと断定しないでください。

構築と `DescendantsAndSelf` は反復走査で、キャンセル時は例外を返します。読み取り専用の結果は並行探索できます。構文ノードには追加の output budget、構築には scan budget がかかるため、facts だけの解析と同じ制限でも先に停止する場合があります。停止時は既存の資源制限診断と Partial を返し、投影済み facts を保持します。

source package には internal な `MdParser.ParseWithSyntax`、`MdSyntaxDocument`、`MdSyntaxNode` が含まれます。runtime の公開 facade は含まれず、利用側の shim から internal entry を呼びます。core は Roslyn や renderer に依存しません。編集差分を渡す API は[選択・増分解析](selective-analysis.md)を参照してください。visitor、rewriter、全 trivia の帰属、構文からの原文再生成は提供しません。
