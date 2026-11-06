# Prose consumer composition sample

SM 06 の prepared consumer は [`samples/Syntamark.Prose`](../samples/Syntamark.Prose/) にある。公開 `ParseWithSyntax` の結果を一度だけ受け取り、既存 facts と syntax を組み合わせる。core の API、既存 `TextRegions`、Yomiyasu 本体は変更しない。

```powershell
dotnet run --project samples/Syntamark.Prose/Syntamark.Prose.csproj -c Release
```

サンプルは架空の日本語 fixture を検証して JSON を出力する。日本語の推敲、個人文書の処理、Yomiyasu への採用変更は行わない。これは特定 caller の prose 選択方針であり、自然言語の一般的な意味を parser に追加する契約ではない。

## 選択と位置対応

`ProsePolicy.Select` は heading の `Text`、paragraph と legacy GFM `tableCell` の `TextRegions.Content` を文書順で選ぶ。paragraph は list/quote 内のものも含む。link label は既存の投影に残り、destination/title と Markdown marker は既存投影の規則に従う。

syntax の code block、inline code、image の全体（alt を含む）、autolink、HTML block/token、math を理由付きで除外する。認識済み front matter と reference definition も facts から除外理由を返す。HTML inline token の間の本文は残す。空白だけの部分は返さず、その他の前後空白は保持する。

返す各 `ProseSegment` は元の `MarkdownTextProjection` とその decoded 部分範囲を保持する。ローカル `Map` は範囲を検査し、decoded の開始 offset を足して元の `Projection.Map` に委譲する。source segment を再構築せず、Atomic token、精度、複数 raw fragment を保持する。

除外された部分をまたいで segment を連結しない。例えば `A` + inline code + `B![](/image)C` は `A`、`B`、`C` であり、架空の `ABC` 文を作らない。HTML token や空 alt のように投影上の文字数が0の除外も raw の隙間で区切る。複数 segment を結んで検査するなら、その意味と境界の扱いは caller が別途決める。

位置不明な protected node があれば選択を止め、理由と null range を返す。未知の投影部分は選ばない。tree が得られなければ `HasSyntax=false` と `syntax-unavailable` を返し、正常な空本文として扱わせない。元の `Facts.Status` と `Facts.Coverage` はそのまま保持する。HTML を意図して除外した sample でも、core の decoded coverage を Complete に書き換えない。

独立 fixture は entity `&amp;`、escape `\*`、絵文字、CRLF、quote prefix、link/image、code、HTML、reference を含む。期待する prose 文字列を固定し、entity の raw `[6,11)`（1行7列）、escape の raw `[22,24)`（3行4列）、絵文字の raw `[13,15)`、code 後のローカル offset、quote 内改行をまたぐ複数 fragment を検証する。元の facts と通常の `Parse` の結果の一致、キャンセル、input rejection も確認する。

front matter は legacy profile の認識済み facts だけを使う。`commonmark/0.31.2` では YAML らしい先頭部分も通常の Markdown として残り、この sample は別の YAML/front-matter scanner を追加しない。legacy の GFM table は cell ごとの prose を返す。標準 profile では同じ table らしい入力を、pipe/separator を含む通常 paragraph として返す。この差を fixture で明示する。

## Yomiyasu の実際の消費経路

調査した実体は `~/.agents/skills/yomiyasu/scripts/markdown_visibility.py`、`__version__ = "2.5.0"`。調査時の SHA-256 は `b4031bc1a960d86ab233394f81aad7620007b4b8df015ff7cc8e1b2957102236`。Python 標準ライブラリだけを使い、現時点で C# / Syntamark 参照はない。スクリプトや個人文書をこの repo に複製しない。

| 経路 | Yomiyasu 2.5.0 の source | この sample |
|---|---|---|
| 構造の取得 | `analyze_markdown` の対象を絞った scanner | 一度の `ParseWithSyntax` と facts |
| 本文 | `line_visible_text` が raw の protected 位置を空白に置換 | decoded projection の独立した部分範囲 |
| escape | `inline_protected_spans` が raw の escape 全体を保護 | `\*` を decoded `*` として選び、元 token 全体へ Map |
| URL | autolink と bare URL を raw で保護 | syntax の autolink を除外。標準 profile の bare URL は残る |
| table | scanner が table を認識し、raw 形式の可視部分を扱う | legacy cell の decoded 値。標準では literal paragraph |
| 位置の単位 | Python string の code point、半開区間 | caller が渡した元文字列の UTF-16、半開区間 |
| 入力変換 | `decode_source` が厳密な UTF-8 decode、先頭 BOM 1個除去、CRLF/CR→LF | parser 自体の入力はそのまま。任意の座標 adapter を別に置く |

従って decoded prose を Yomiyasu の raw mask にそのまま差し替えられない。bare URL の判定や escape を推敲から保護する方針を core へ移していない。Yomiyasu の scanner と Syntamark の profile/投影を同等とみなす比較結果もない。

`YomiCoordinates` は任意の座標変換例で、入力は既に decode 済みの .NET string。BOM 1個除去と改行正規化の各境界を記録し、正規化後の Python code-point 範囲と元 raw UTF-16 範囲を変換する。surrogate pair の内部、CRLF の内部、除去した BOM の範囲は逆変換を拒否する。byte decode や Yomiyasu の実行は担当しない。`BOM + A😀 + CRLF + B + CR + C + LF` の fixture で、絵文字が Python 1単位 / UTF-16 2単位であることを独立に検証する。

## 読み取り専用の実行結果

2026-11-06 に、登録済みの `pythoncore-3.14-64/python.exe`（Python **3.14.9**）を絶対パスで実行した。`-I -B` により user site を使わず、bytecode cache を書き込まずに、実際の `markdown_visibility.py` の関数だけをロードした。editorial rewrite や lint/diff/check CLI は呼んでいない。Yomiyasu source の実行前後 SHA-256 はともに `b4031bc1a960d86ab233394f81aad7620007b4b8df015ff7cc8e1b2957102236` で、原本・consumer は変更していない。

同じ架空の日本語 fixture を UTF-8 / CRLF で与えた。fixture の SHA-256 は `5a3baa9a0e473df52185577824f70c296b4e287ec6c832bedfcf400cddc60163`、元の UTF-16 長は237、`decode_source` 後の Python code-point 長は210。`analyze_markdown` と `line_visible_text` を実行し、以下を確認した。

| 確認箇所 | 実際の Yomiyasu 可視範囲 | 既に検証した C# sample |
|---|---|---|
| entity / escape | `&amp;` は raw のまま。`\*` の2文字は空白になる | decoded `&` / `*` を返し、元 token へ Map |
| link / reference | inline link の label は残り destination は隠れる。参照 use は `[参照][r]。` の raw 表記が残る | label を decoded `参照。` として返す |
| code / image / HTML | コード本文、画像 alt、HTML block は空白。inline tag の間の `中` は残る | 対象を除外し、tag の前後も独立 segment として保持 |
| URL | bare URL は空白になる。この fixture の angle autolink は URL 部分が隠れ、閉じ `>` は可視のまま残る | 標準 profile の bare URL は残し、syntax autolink は全体を除外 |
| table | 可視 header は pipe を含む元の1行 | legacy は decoded cell、標準は literal paragraph |

閉じ `>` などの差は、この入力で観測した可視範囲の違いとして記録する。ここから確定不具合や scanner 全体の互換性を判断していない。

別の `BOM + A😀 + CRLF + B + CR + C + LF` probe では、実際の `decode_source` が BOM を除去し、mixed EOL を LF にした。正規化後は Python 7 code point / UTF-16 8単位、元は UTF-16 10単位。絵文字の Python `[1,2)` は元 raw UTF-16 `[2,4)`、次の LF の Python `[2,3)` は元 CRLF `[4,6)` に対応し、C# 座標 adapter の独立期待値と一致した。

ローカル証跡は `.local/yomiyasu-visibility-comparison.json` と同名 `.log`、`.local/compare-yomiyasu-visibility.py`、`.local/yomiyasu-fixture.md` に保存した。C# 側は `.local/prose-sample.log` の成功結果と比較しており、製品の再ビルドや consumer の変更は行っていない。

## 完了範囲

公開 API の composition で、この限定した選択と診断位置の保持は実現できるため、新しい core `ProseRegion` API は追加しない。上記の限定した Python visibility 比較は成功した。Yomiyasu への実際の採用、lint/diff/check CLI、実文書での比較、網羅的な互換性は未検証。採用する場合は、対象 caller の保護規則と座標変換、配布と .NET 依存の選択を別に判断する。速度・割当て・校正品質の改善は主張しない。
