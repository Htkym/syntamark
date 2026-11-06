# Read-only syntax view

SM 10 は `MarkdownParser.ParseWithSyntax` を追加する。既存の `Parse(string, string, string, string?, MarkdownParseOptions?, CancellationToken)` はそのまま残す。追加 entry は一回の構文解析から `Facts` と `Root` を返し、通常の facts 取得では構文 view を作成・保持しない。

## 公開形の判断

見出し、quote 内の link、list 内の code の三用途を比較した。typed subclass 群では各用途に cast と複数の公開型が必要になる。今回は `Kind`、`RawSpan`、`Children` を共通とし、必要な値だけ nullable の readonly property に持たせる。constructor は internal に閉じ、呼び出し側が無効な組合せを作れないようにする。

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

`HeadingLevel` は Heading、`OrderedListStart` は OrderedList、`IsTightList` は両 list、`TaskChecked` は task の ListItem にだけ値がある。`CodeInfo` は code block の info、`Target`・`Title`・`TargetRawSpan`・`ReferenceLabel` は link/image/autolink の情報である。それ以外の kind では null。将来 kind を追加し得るため、caller の switch は未知の値を処理する分岐を残す。

## 所有権と位置

- 一つの parse が元の owned string と immutable tree/facts を所有する。node を保持するとその元文字列も保持する。node ID、parent pointer、編集をまたぐ identity は設けない。
- `RawSpan` は元入力の UTF-16 半開区間。root は入力全体で、front matter は構文 children へ追加せず既存 facts に残す。BOM や前置き部分による body offset を一度だけ加える。
- node の範囲は marker を含む raw envelope。container は内部の prefix や whitespace も含み得る。既存 parser の block によって末尾の改行を含むかは異なる。`GetRawText()` はその区間をそのまま返し、AST の再構成や round-trip writer ではない。
- HardLineBreak は backslash または末尾の2個以上の ASCII space と元の改行（CRLF は2文字）を含める。互換 profile の内部 AST が改行だけの span を持つ場合も、新しい syntax view では確認できた raw marker を含める。既存 facts/AST の範囲は変更しない。
- decoded 値と raw slice を同じ文字列とはみなさない。`Text` は Text/Code/HTML/Math/Autolink/LineBreak の leaf 値のみ。code/HTML の改行は parser が正規化し得る。Heading/Paragraph/Link の decoded projection は既存 facts を使う。
- 不明な raw span は null とし、既知の0幅とは区別する。content span や marker の帰属を一般化した API は追加せず、fence/link の精密 token 位置は既存 facts を使う。
- 互換 profile の既存 emphasis AST には、子が親の外へ出る、または兄弟と重なる位置を持つ例がある。新しい view は、その矛盾が確認された emphasis の `RawSpan` を null にする。marker 位置を推測して補正せず、元の AST・facts の値と認識結果は保持する。この処理は標準 profile に適用しない。`Facts.Coverage` は facts の投影範囲、node の nullable `RawSpan` は syntax 位置の既知性であり、別の契約である。
- 参照定義は位置順の `Facts.Links` の ReferenceDefinition として残す。use の label だけが syntax child になり、定義へ向く `TargetRawSpan` は semantic relation であるため親子の包含規則の対象にしない。未解決の参照候補は Link と偽装せず、通常の Text と既存の unresolved facts で表す。

GFM table は現在の内部 tree が row/cell の独立 span を持たないため、Table の下に inline を文書順に置く。偽の TableRow/TableCell node は追加しない。CommonMark の native HTML token は block/inline を分ける。互換 profile 固有の directive、admonition、math、strike 等も対応する kind として区別する。

## 不完全な結果と探索

入力拒否、または構文・facts・view の構築が budget 停止した場合、`Root` は null。投影上の制約によって facts が Partial と分類されても、構築が終わっていれば tree は存在し得る。caller は `Facts.Status`・`Coverage` を見て、Partial/Failed から構文や定義が存在しないという診断を作らない。

構築と `DescendantsAndSelf` は反復走査で、document の深さをそのまま call stack に積まない。各 node の走査時に cancellation を確認する。キャンセルは例外であり、成功した空 tree に変換しない。結果は読み取り専用で、並行探索に状態を共有しない。

syntax opt-in は追加の node ごとに output budget、構築の走査に scan budget を課す。facts のみと同じ限度でも、追加出力の分だけ先に止まる場合がある。構築中の停止は既存の resource 診断と Partial を返し、完成していない Root を公開しない。投影済みの facts は保持する。

source package には同じ `MdParser.ParseWithSyntax` と `MdSyntaxDocument` / `MdSyntaxNode` が含まれる。runtime facade は従来どおり source package に含めない。source host からは internal entry を consumer の shim が呼ぶ。Roslyn 自体や renderer を core の依存へ追加しない。

visitor、rewriter、incremental parsing、全 trivia の帰属、安定 node ID、AST からの原文再生成は今回の対象外。syntax opt-in は構文探索のための追加機能であり、性能改善としては扱わない。時間・割当て・保持メモリの追加負担は、[測定記録](performance.md)に示す。

CommonMark 0.31.2 の固定された全652例を `Syntamark.Conformance --syntax` で検査した結果、標準 profile は HTML 652/652、facts/syntax invariant 違反0、位置不明 node 0。互換 profile は従来の HTML 分類 521 passed / 129 failed / 2 not-observable を保持し、facts/syntax invariant 違反0、24例に計25個の位置不明 emphasis node がある。位置検査は known range の入力境界、親子の包含、兄弟の非重複・文書順を調べ、semantic target を除外する。これは node の意味的な完全性を証明する検査ではない。
