# 本文を選ぶサンプル

[`samples/Syntamark.Prose`](../samples/Syntamark.Prose/) は一回の `ParseWithSyntax` の結果から、校正対象にするテキストと元位置を取り出すサンプルです。

```powershell
dotnet run --project samples/Syntamark.Prose/Syntamark.Prose.csproj -c Release
```

架空の日本語入力を処理し、選んだテキストと除外理由を JSON で出力します。これは利用側の選択方針を示すもので、自然言語としての本文を一般的に定義する API ではありません。

## 選ぶ範囲と除く範囲

`ProsePolicy.Select` は heading の `Text`、paragraph と互換 profile の GFM `tableCell` の `TextRegions.Content` を文書順で選びます。list/quote 内の paragraph と link label も含みます。destination、title、Markdown 記号は既存の投影規則に従います。

code block、inline code、image 全体と alt、autolink、HTML block/token、math を理由付きで除外します。認識済み front matter と reference definition も除外理由を返します。inline HTML tag の間の本文は残し、空白だけの部分は返しません。それ以外の前後空白は保持します。標準 profile の bare URL は残るため、その保護は利用側で判断してください。

除外部分をまたいで segment を連結しません。`A` と inline code、その後の `B![](/image)C` は `A`、`B`、`C` に分かれます。HTML tag や空の alt のように、投影上の文字数が0の除外でも区切ります。

front matter は互換 profile の認識済み facts だけを使います。`commonmark/0.31.2` では YAML らしい先頭部分も通常の Markdown として残り、このサンプルは別の front-matter scanner を追加しません。GFM table は互換 profile では cell ごとのテキスト、標準 profile では pipe や separator を含む通常の paragraph になります。

## 元位置への対応

各 `ProseSegment` は元の `MarkdownTextProjection` と decoded 部分範囲を保持します。ローカル `Map` は範囲を検査し、開始 offset を足して元の `Projection.Map` に委譲します。Atomic token、精度、複数の raw fragment をそのまま扱えます。

位置不明の除外対象があれば選択を止め、理由と null range を返します。未知の投影部分は選びません。tree が得られなければ `HasSyntax=false` と `syntax-unavailable` を返します。元の `Facts.Status` と `Facts.Coverage` を確認し、空の結果を完全な本文の不在とみなさないでください。既存の `TextRegions` は変更しません。

## Yomiyasu と組み合わせる場合

Yomiyasu 2.5.0 は Python 標準ライブラリで動き、Syntamark を参照していません。このサンプルを採用済みの実装や、そのまま差し替えられる処理として扱わないでください。

| 対象 | Yomiyasu の可視範囲 | このサンプル |
|---|---|---|
| 本文 | raw の保護範囲を空白に置換 | decoded projection の部分範囲 |
| entity / escape | entity は raw のまま、escape を保護 | decoded 値を選び、元 token へ Map |
| URL | autolink と bare URL の対象範囲を保護 | syntax の autolink を除外。標準 profile の bare URL は残す |
| table | raw 形式の可視部分 | 互換 profile の decoded cell。標準では literal paragraph |
| 位置 | 正規化後の Python code point | 元文字列の UTF-16 |

Yomiyasu の `decode_source` は厳密に UTF-8 を解き、先頭 BOM を1個除去し、CRLF/CR を LF にそろえます。Syntamark の raw 位置と直接交換することはできません。

任意の座標変換例 `YomiCoordinates` は decode 済みの .NET string を受け取り、同じ BOM・改行変換の境界を記録します。正規化後の Python code-point 範囲と元の UTF-16 範囲を変換し、surrogate pair の内部、CRLF の内部、除去した BOM の範囲は逆変換を拒否します。例えば `BOM + A😀 + CRLF + B + CR + C + LF` では、絵文字の Python `[1,2)` は元 UTF-16 `[2,4)`、次の LF の Python `[2,3)` は元 CRLF `[4,6)` です。byte decode や Yomiyasu の実行は行いません。

実際に組み合わせる場合は、利用側の保護規則、座標変換、配布と .NET 依存の要否を決めてください。Yomiyasu の採用、lint/diff/check CLI、実文書での利用、網羅的な互換性は確認していません。
