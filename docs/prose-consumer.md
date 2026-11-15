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

## Yomiyasu の実行

Python と Yomiyasu を利用側で用意した場合、次の明示的な操作で校正対象を lint CLI へ渡せます。Syntamark 本体は Python や Yomiyasu を参照せず、通常のサンプル実行でも外部プロセスを起動しません。

```powershell
dotnet run --project samples/Syntamark.Prose/Syntamark.Prose.csproj -c Release -- --yomiyasu python /path/to/yomiyasu_lint.py guide.md
```

`python` とスクリプトのパスは実際の配置へ置き換えてください。入力ファイルを省略すると、サンプルに含まれる架空の日本語原稿を使用します。ファイルは厳密な UTF-8 として読み、先頭 BOM を元文字列に残します。

`YomiyasuBridge` は `ProsePolicy.Select` が選んだ各 segment を別々の標準入力として渡します。除外部分をまたいだ文章を作らず、コード、リンク先、HTML、front matter も送りません。各呼び出しには `--json --no-words -` を指定し、プロジェクトの用語一覧を自動では読み込みません。複数 segment にまたがる段落や文書全体の規則は、この経路では検査できません。

| 対象 | Yomiyasu の可視範囲 | このサンプル |
|---|---|---|
| 本文 | raw の保護範囲を空白に置換 | decoded projection の部分範囲 |
| entity / escape | entity は raw のまま、escape を保護 | decoded 値を選び、元 token へ Map |
| URL | autolink と bare URL の対象範囲を保護 | syntax の autolink を除外。標準 profile の bare URL は残す |
| table | raw 形式の可視部分 | 互換 profile の decoded cell。標準では literal paragraph |
| 位置 | 正規化後の Python code point | 元文字列の UTF-16 |

Yomiyasu の `decode_source` は厳密に UTF-8 を解き、先頭 BOM を1個除去し、CRLF/CR を LF にそろえます。Syntamark の raw 位置と直接交換することはできません。

座標変換例 `YomiCoordinates` は decode 済みの .NET string を受け取り、同じ BOM・改行変換の境界を記録します。正規化後の Python code-point 範囲と元の UTF-16 範囲を変換し、surrogate pair の内部、CRLF の内部、除去した BOM の範囲は逆変換を拒否します。例えば `BOM + A😀 + CRLF + B + CR + C + LF` では、絵文字の Python `[1,2)` は元 UTF-16 `[2,4)`、次の LF の Python `[2,3)` は元 CRLF `[4,6)` です。

lint の JSON format 2 は行番号を返すため、サンプルはその行全体を segment の投影で元位置へ戻します。元位置が複数に分かれる場合は `RawFragments` を保ち、行だけが分かる診断を `Exact` として返しません。診断は `CoveringTokens` 以下の精度であり、自動置換には使いません。出力には入力の識別子、内容から計算した `sourceVersion`、解析状態、除外理由、診断を含めます。同じ入力スナップショットに対する診断として扱ってください。

各 CLI 呼び出しは30秒で中止し、非0の終了状態、対応外の JSON 形式、不正な行番号を成功として扱いません。この経路が実行するのは lint だけです。Yomiyasu 自体の diff/check 手順や文章の意味の確認、修正の適用は利用側で行ってください。
