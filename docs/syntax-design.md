# 構文の探索

[English](syntax-design.en.md) | [日本語](syntax-design.md)

`MarkdownParser.ParseWithSyntax` は1回の解析から `Facts` と読み取り専用の `Root` を返します。ノードの種類や親子関係を調べるときに使います。通常の `Parse` は公開用の構文ツリーを作成・保持しません。

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

`Root` があっても `Facts.Status` や `Facts.Coverage` が完全とは限りません。不完全な結果から、構文や定義が存在しないと断定しないでください。必要な解析情報だけを取得する方法は[選択・増分解析](selective-analysis.md)を参照してください。

## ノードとスナップショット

`MarkdownSyntaxNode` は `Kind`、`RawSpan`、`Children` と、種類に応じた値を持ちます。直接生成・変更する API はありません。将来の種類の追加に備え、`Kind` の分岐には未知の値を扱う処理を残してください。

| プロパティ | 値を持つ種類と意味 |
|---|---|
| `HeadingLevel` | Heading の見出しレベル |
| `OrderedListStart` / `IsTightList` / `TaskChecked` | 該当するリストや ListItem の情報 |
| `Text` | Text、code、HTML、math、autolink、line break の文字列。コンテナーでは null |
| `CodeInfo` | code block の言語などの情報 |
| `Target` / `Title` / `TargetRawSpan` / `ReferenceLabel` | link、image、autolink の該当する情報 |
| `ContentRawSpan` / `Content` | Heading、link、image、code block の内容範囲と投影 |
| `ReferenceLabelRawSpan` | link、image、footnote のラベルの元位置 |
| `TableCells` | Table のセル情報。他の種類では空の一覧 |
| `Directive` | コロンで囲んだ directive / admonition の構文情報。GitHub alert の引用では null |
| `FootnoteNumber` / `FootnoteOccurrence` | 脚注の表示番号と同じラベルへの参照順 |
| `FootnoteNormalizedLabel` / `FootnoteIsDuplicate` | 脚注の解決用ラベルと定義の重複状態 |

該当しない nullable な値は null です。Paragraph の `Text` と `Content` も null なので、本文の投影には facts の `TextRegions` を使います。GFM table の `Children` には inline を文書順に置き、row / cell の構文ノードは作りません。HTML の block と inline、directive、admonition、math、strikethrough は種類を区別します。

root は BOM と front matter を含む入力全体を覆います。front matter は `Children` へ追加せず facts に残します。`GetRawText()` はそのノードの元範囲の文字列を返し、位置不明なら null を返します。原文を構文から再生成する機能ではありません。

ノードを保持すると元文字列も保持します。parent pointer、永続 node ID、編集をまたぐノードの同一性は提供しません。診断や編集候補には `Facts.SourceId` と `Facts.SourceVersion` を関連付け、適用前に対象のバージョンと期待する元文字列を確認してください。

## 元位置とデコード済みの内容

`RawSpan` は元の .NET string に対する UTF-16 の半開区間 `[Start, End)` で、`End = Start + Length` です。絵文字などの surrogate pair と CRLF はそれぞれ2文字として数えます。UTF-8 の byte offset や Unicode code point の数とは区別してください。位置不明の null と、位置が分かっている `Length = 0` の範囲も別です。

ノードの `RawSpan` は記号を含む範囲です。コンテナー内部の接頭辞や空白を含み、末尾改行の有無は block の種類によって異なります。HardLineBreak は backslash または末尾の2個以上の ASCII space と元の改行を含みます。

`ContentRawSpan` は内容を囲む元範囲です。Heading では見出しの記号を除き、内容が複数の元範囲へ分かれる場合は null になります。link / image では表示ラベルの外側の `[]` を除きますが、複数行のラベルなど位置を確定できない場合は null です。fenced code block では fence 行を除き、内側の quote / list の接頭辞を含む場合があります。indented code block の `ContentRawSpan` は null です。

`Content` は表示文字やコード内容の `MarkdownTextProjection` です。例えば `# A &amp; 😀` では `Content.Text` は `A & 😀` になり、`&` の位置を `Map` すると元の `&amp;` token に対応します。コードや HTML の改行は正規化される場合があります。indented code の `Content` は文字列を持ちますが、元位置の精度は `Unknown` です。

| `Map` の `Precision` | 扱い |
|---|---|
| `Exact` | 文字と元範囲の対応が確定している。`RawFragments` が複数になる場合もある |
| `CoveringTokens` | entity、escape、改行変換などの token を含む範囲。文字単位の正確な置換範囲ではない |
| `Partial` | 既知の元範囲と位置不明の部分が混在する |
| `Unknown` | 対応する元範囲を確定できない |

`Map` へ渡す範囲は `Content.Text` の UTF-16 範囲です。自動修正に使うときは精度とすべての `RawFragments` を確認してください。複数の範囲を一つに囲んだ区間は、その全体を安全に置換できる範囲とは限りません。

互換プロファイルの emphasis で親子・兄弟の位置が矛盾する場合、その emphasis の `RawSpan` は null になります。既存 AST・facts の認識結果や位置は変更しません。この扱いは `commonmark/0.31.2` には適用しません。投影の網羅範囲を示す `Facts.Coverage` と、各ノードの位置が既知かどうかは別に確認してください。

参照形式のリンクでは `ReferenceLabelRawSpan` がラベルの内容を指し、外側の `[]` を含みません。collapsed / shortcut reference は表示ラベルの位置を使います。複数行のリンクラベルなど安全に確定できない位置は null です。参照定義は `Facts.Links` の `ReferenceDefinition` に位置順で残り、参照先の `TargetRawSpan` が使用ノードの外を指す場合があります。未解決の候補は Text と未解決の link facts で表し、定義を構文の child にしません。

## 表のセル

`TableCells` の `Row` と `Column` は0から始まります。`RawSpan` は実在するセルの前後の空白を除いた元範囲で、escape された `\|` は元の2文字を含みます。`Content` はデコード済みのセル内容と位置の投影です。

実在する空セルは0幅の `RawSpan` を持ちます。列不足を補った空セルは元入力に存在しないため `RawSpan = null` です。どちらも表示内容が空でも、元位置の有無を分けて扱ってください。

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

この例では `item` の行の2列目は実在する空セルで、`tail` の行の2列目は補完されたセルです。表は互換プロファイルと `syntamark-docs/2` で扱います。`commonmark/0.31.2` は GFM table を表として解析しません。

## ディレクティブの編集情報

`syntamark-docs/2` は `:::Card title="Guide" count=2` のような属性を解釈します。既定の `lithosharp-markdown/1` と `commonmark/0.31.2` の意味は変更しません。旧プロファイルでは属性を解釈せず、互換プロファイルの header は従来の title の意味を保ちます。

`Directive` は `NameRawSpan`、`OpeningMarkerRawSpan`、`HeaderRawSpan`、`ContentRawSpan`、nullable な `ClosingMarkerRawSpan` を返します。内容範囲は fence 行を除きますが、raw のコンテナー接頭辞を含む場合があります。コードブロックや literal として扱う箇所はディレクティブとして再解釈しません。

名前には ASCII の英数字、`-`、`_` を使い、属性名にはさらに `:` と `.` を使えます。`Attributes` は元の順序と重複を保持します。各属性の `RawSpan`、`NameRawSpan`、`ValueRawSpan`、`OpeningQuoteRawSpan`、`ClosingQuoteRawSpan` は別々の位置です。名前の重複は大文字と小文字を区別して判定します。

属性は `name=value` の形で指定し、値は単一引用符、二重引用符、または引用符なしで書けます。引用中は対応する引用符と backslash の escape だけを `Value.Text` へ復号し、他の文字はそのまま保持します。`ValueRawSpan` は引用符を除いた元範囲で、`Value.Map` は復号後の位置を元 token へ戻します。値が欠けた場合の `Value` や `ValueRawSpan`、閉じていない引用符の `ClosingQuoteRawSpan` は null です。

| 診断 ID | `Reason` と内容 |
|---|---|
| `LMD020` | `missingName` / `attributeName`。名前の不足、または属性名として読めない記号 |
| `LMD021` | `missingValue`。`=` や値が不足している属性 |
| `LMD022` | `unclosedQuote`。閉じていない引用符 |
| `LMD023` | `duplicateAttribute`。重複した属性名 |
| `LMD024` | `unterminated`。閉じていないコンテナー |

診断は `Directive.Diagnostics` と文書の `Facts.Diagnostics` から取得できます。`IsComplete` は終了 fence が存在し、ディレクティブ診断がないときに true になります。部分結果でも解析できた属性は保持します。欠けた値や位置を推測して修正しないでください。

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

この例は引用符を残して値の編集候補を作るための位置と期待値を取得します。変更の適用は利用側で行い、対象バージョンと元文字列が一致すること、新しい値をその引用形式で表せることを確認してください。C# props への型バインド、コンポーネント登録、サイト固有の診断は Syntamark の担当範囲に含みません。

## 脚注

`syntamark-docs/2` は `[^source]` の参照と `[^source]: ...` の定義を扱います。構文ノードは `FootnoteReference` / `FootnoteDefinition` です。元のラベルは `ReferenceLabel` に残し、`ReferenceLabelRawSpan` は `[^` と `]` を除いたラベルの内容を指します。

`FootnoteNormalizedLabel` は解決用の値です。`FootnoteNumber` は最初に解決された参照の順で決まり、未解決の参照や参照されていない定義では null です。`FootnoteOccurrence` は同じラベルに対する参照順を1から示します。定義は `Children` に複数の block を持つことができ、`FootnoteIsDuplicate` で重複を区別します。参照先には最初の定義を使います。

`LMD030` は重複定義、`LMD031` は未解決参照の診断です。HTML の脚注一覧や backlink の生成は利用側で行います。構文ツリーを保持せず脚注情報を取得するには `MarkdownParser.AnalyzeFootnotes` を使います。この entry の既定プロファイルは `syntamark-docs/2` です。他の entry で脚注を有効にするときは同プロファイルを明示し、他のプロファイルでは従来の literal としての扱いを保ちます。

## 制限とキャンセル

入力を拒否した場合や、ツリーが完成する前に資源制限へ達した場合は `Root = null` です。投影の制約で facts が `Partial` でも、完成したツリーを取得できる場合があります。`Facts.Status` と `Coverage` を併せて確認してください。

構築と `DescendantsAndSelf(cancellationToken)` は反復走査を使い、キャンセル時は `OperationCanceledException` を返します。読み取り専用の結果は並行探索できます。構文ノードとセル・属性情報には追加の output budget、構築には scan budget を使うため、facts だけの解析と同じ制限でも先に停止する場合があります。資源制限での停止は `LMD003` と `Partial` で示し、投影済みの facts を保持します。

`Syntamark.Source` は internal な `MdParser.ParseWithSyntax`、`MdSyntaxDocument`、`MdSyntaxNode` と共通の portable 実装を含みます。runtime の公開 facade は含まず、利用側の shim から internal entry を呼びます。parser は Roslyn や renderer に依存しません。visitor、rewriter、すべての trivia の帰属、構文からの原文再生成は提供しません。
