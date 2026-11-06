# Markdig と Syntamark の初期比較

2026-11-06 の SM 12 調査。比較 project は `tests/Syntamark.Comparison`。製品には Markdig を追加していない。

Markdig は NuGet **1.5.1** を exact range と lock file で固定した。実ロードした assembly は `Markdig, Version=1.5.0.0`、informational version は `1.5.1+bbf197eaf3c96b9a6ada6532ec970b59751ca9c6`。Syntamark の baseline は `6120fc0f231c952cdf01ed77784bb0f2e4a898d4`、profile は `lithosharp-markdown/1`。Markdig は精密 source location だけを有効にした CommonMark core で、拡張は無効。

## 実行で確認したこと

.NET SDK 10.0.403、Windows の .NET 10 CLI で、共通5入力の見出し・解決済みリンク/画像が、両実装とも独立期待値と一致した。通常の見出し/リンク、entity/escape/emoji/CRLF、list 内の強調リンク、参照定義への forward resolution、画像の alt を含む。見出し level と decoded 値、リンク label/target/title、UTF-16 の raw/target 範囲を確認している。正式な実行方法と共通出力契約は [runner README](../tests/Syntamark.Comparison/README.md) にある。

これは5文書の共通 facts 確認であり、公式全例の適合率ではない。Syntamark の既存 profile と純粋 CommonMark は異なる。Syntamark は共通 subset 外の facts・mapping・診断も生成し、Markdig は AST を保持するため、結果の一致は仕事量や速度の同等性を意味しない。

空 destination は別 probe とした。baseline の `lead 😀\r\n\r\n[x]() [y](<>)\r\n` に対し、独立期待値は `[15,15)` と `[22,22)`。Syntamark baseline は両方 `[0,0)` を返した。元位置修正を含む作業中の候補版では、両方が期待位置に一致した。Markdig は `()` の `UrlSpan` を `(0,-1)` の Empty sentinel とし、`<>` は `[21,23)` の lexical token を返した。Markdig の後者は token の範囲であり、空内容の位置と同じ契約として比較しない。Syntamark の修正判断は別の元位置回帰テストに従う。

`# A&amp;\*😀` の decoded `&` に対し、Syntamark は raw `&amp;` の `[3,8)` と `CoveringTokens` を返す。Markdig の `HtmlEntityInline` にも `Original=&amp;` と `Transcoded=&` があることを実行確認した。Markdig に decoded/raw 情報がないという前提は置かない。任意の decoded substring を複数 raw fragment と precision に戻す共通 adapter は、今回実装していない。

## 機能と残る確認

`supported` は記載した経路の確認、`partial` は制約付き、`unverified` は未検証を表す。ライブラリの全機能の有無を断定する表ではない。

| 観点 | Syntamark | Markdig 1.5.1 | 根拠・制約 |
|---|---|---|---|
| 共通 heading/link/image facts | supported | supported | 共通5入力が独立期待値と一致 |
| 公開 AST と child traversal | partial | supported | Syntamark は公開 facts。Markdig の公開 `Descendants()` で list 内 link を探索。任意の tree の妥当性までは未検証 |
| entity の raw/decoded 値 | supported | supported | `Map` の raw token と `Original`/`Transcoded` の実行確認 |
| 任意 decoded substring → raw fragments/precision | supported | unverified | Syntamark の entity query を確認。Markdig で同じ契約の adapter は未実装 |
| 空 destination の位置 | supported（候補の2例） | partial | baseline と lexical token の差は上記。空と位置不明を分ける |
| 部分結果・coverage・scan/output/depth budget | supported | unverified | Syntamark の契約と既存 Portability gate。Markdig に同じ中断保証を実証していない |
| 実行中 cancellation | supported | unverified | Syntamark の既存契約。比較 runner は bounded な小入力のみ |
| CommonMark 0.31.2 の全公式例 | 別 runner の結果を参照 | unverified | この比較では Markdig の全例を実行していない |
| compiler の source package host | 別 canary の結果を参照 | unverified | netstandard2.0 DLL の存在だけで実 host 対応を主張しない |
| 性能・allocation・保持メモリ | unverified | unverified | 今回は機能比較のみ |

## Backend 判断

見出し・リンクの薄い adapter は成立した。Markdig の AST と token 位置は有力な比較対象だが、Syntamark 固有の mapping precision、resource budget、source 配布を置き換える adapter の保守量は未確定。今回の結果だけでは backend を変更せず、公式例で最大の差分カテゴリを特定した後に、限定した現 parser 修正と adapter の実装量を比較する。

source payload の互換性も別問題となる。Markdig 1.5.1 の [build 設定](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Markdig.targets) は netstandard2.0 を含むが、その target では System.Memory を参照し、source 自体は preview 言語と unsafe を使用する。現行の C# 13 portable source にそのまま取り込めるとは判断しない。採用時には BSD-2-Clause の通知と依存 closure を確認する。

資料は [固定 NuGet 版](https://www.nuget.org/packages/Markdig/1.5.1)、[位置 API](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Syntax/SourceSpan.cs)、[LinkInline](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Syntax/Inlines/LinkInline.cs)、[HtmlEntityInline](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Syntax/Inlines/HtmlEntityInline.cs)。静的確認と上記の実測結果を区別する。Markdig の全機能、IDE host、敵対入力、性能は未検証。

## CommonMark 全公式例の比較

比較 runner には、両実装を同じ版固定の公式652例で観測する専用モードもある。既存の共通5文書の gate はそのまま残す。

```powershell
dotnet run --project tests/Syntamark.Comparison -c Release --no-restore -- --commonmark-output .local/comparison/commonmark
```

Markdig 1.5.1 は精密 source location だけを有効にし、拡張を使わない。Syntamark は opt-in の `commonmark/0.31.2` と十分な明示 budget を使い、Conformance runner と同じテスト専用 `SyntaxHtml` で内部構文を観測する。各実装の HTML は、他方の出力を正解にせず公式 fixture の期待値とそれぞれ比較する。fixture の原本 checksum と例番号1–652を起動時に検証し、実行中に外部取得しない。

`results.json` は全例と章別の一致・差分・観測不可・実行エラーを保存する。raw 入力、期待 HTML、実際の HTML、入力 checksum、実ロード assembly、parser stamp、options hash、runtime/OS、両実装の設定も含む。Markdig の package version と informational version を分け、source commit を含む実際の identity を残す。Syntamark の assembly SHA-256 も保存するため、未コミット候補を generated parser stamp だけで識別しない。

Syntamark の公開 facts は同じ raw 入力から別途解析し、parse status、raw/mapping coverage、unprojected 件数、diagnostic の理由を構文の一致と別欄に残す。Complete でない結果や resource 停止は、HTML が一致していても構文合格数へ入れない。coverage が Complete であることを、期待 facts の完全性や正しい意味の証明とは扱わない。raw/decoded 位置は独立した Portability の oracle と併せて検証する。

この専用モードは棚卸し用であり、構文の差分や adapter の観測不可だけでは exit 1 にしない。fixture/adapter 自己検査の失敗と実行エラーは失敗終了する。従って exit 0 を全例準拠と読み替えない。性能は測定せず、二つの構文観測と facts 解析の仕事量が同じとも主張しない。Markdig の compiler-host/source 消費、同等の resource contract、任意 decoded substring の mapping はこのモードの検証範囲外である。

HTML の比較は空白を正規化しない ordinal の全文比較である。初回の Markdig 実行では619例が完全一致し、33例にリストの `<li>` 直後の改行差があった。公式出力の `<li>\n<p>` 等に対し Markdig は `<li><p>` 等を出したもので、保存した33例はいずれもこの位置の改行だけを除くと一致した。raw HTML や code 内の空白まで一括で除去する処理は追加していない。この33例を Markdig の文法不具合と断定せず、renderer の出力形式の差として説明する。

## 同じ抽出結果と Roslyn 連携の実測

2026-11-06 に、独立した [benchmark project](../benchmarks/Syntamark.Comparison/README.md) で固定 corpus の比較を実行した。Markdig 1.5.1、Roslyn Common 4.14.0 を lock file で固定し、Syntamark は検証済み Release DLL を `.local/comparison-benchmark-current` にコピーして明示的な HintPath から参照した。製品側の依存追加や共有 product build は行わない。測定した候補 DLL の SHA-256 は `d874958d2eda50cdf3ae18ce6f8263a7f02f754fe789b5af2448749b1ad4b2da`。generated parser stamp は作業中の source 変更を識別しないため、実 DLL hash と併記した。正式 package や compiler-host の速度測定ではない。

比較を3段階に分けた。

1. `representation-unequal-work` は Syntamark の `ParseWithSyntax` による全 facts＋公開 syntax view と、Markdig の精密位置付き AST。仕事量が違うため、同等な parse-only の順位には使わない。
2. `common-facts` は同じ見出し・解決済みリンク・fenced code の値と raw UTF-16 範囲を生成する。両側とも parse と adapter の探索・decode・出力生成を含む。Syntamark は選ばれない sections、text regions、mapping metadata、診断、budget 計上も生成するので、同じ出力でも内部の仕事量は同じにならない。
3. `snapshot-to-locations` は同じ Roslyn `SubText` の `ToString()` → parse → 共通 facts → `Location[]`。snapshot の生成と line index 初期化は測定前で、文字列の materialize と Location 生成は測定内。実行時に `SubText` であることと、`ToString()` が元の入力 string を再利用しなかったことを保存した。

corpus は229／5,496／21,984 UTF-16単位の3文書。entity、escape、emoji、CRLF、inline/reference link、inline code、閉じた code fence を含む。各文書の見出しは1／24／96件、リンクは2／48／192件、fence は1／24／96件、Locations は6／144／576件。両実装の全共通出力を、固有 raw token と明記した decoded 値から作る独立期待値に一致させた。line/column も別の raw UTF-16 scanner と一致した後に測定した。全件 `Complete`、resource 診断0、raw/mapping coverage `Complete`。この coverage は、比較に含めない全機能の正しさを証明するものではない。

code の等値契約は top-level・字下げなし・閉鎖済み・closing 後空白なしの fence と単純な info 語 `csharp` に限定した。本文は CRLF を LF にし、終端 line separator をちょうど1個除いた論理内容を比べる。Markdig は `Lines.Count` 個の `Slice` から生成し、配列容量や `ToString()` の改行表現に依存しない。空 destination、複雑な info、字下げ・nested・未閉鎖 fence はこの等値性能比較の対象外である。

4つの独立プロセスを A/B/B/A の順で実行し、A は各 workload で Syntamark から、B は Markdig から測定した。各 workload は完全な1 batch の warmup 後に5サンプル、TieredCompilation=0。下表は各プロセスの中央値を求めた後の4プロセスの中央値で、時間は1文書当たりの µs、allocation は B。生の全サンプル、GC delta、fixture/expected checksum、status、各 assembly identity は [保存結果](../benchmarks/Syntamark.Comparison/results/2026-11-06-current/summary.json) と同じ directory の4つの `run-*.json` にある。

| 文書 | 段階 | Syntamark µs / allocated B | Markdig µs / allocated B |
|---|---|---:|---:|
| small | 共通 facts | 20.754 / 40,971 | 4.801 / 7,264 |
| small | snapshot → Locations | 23.578 / 42,683 | 5.849 / 8,976 |
| medium | 共通 facts | 575.913 / 783,185 | 94.838 / 146,736 |
| medium | snapshot → Locations | 574.469 / 812,034 | 110.081 / 175,544 |
| larger | 共通 facts | 1,541.250 / 3,110,516 | 340.925 / 590,848 |
| larger | snapshot → Locations | 2,090.750 / 3,224,140 | 427.100 / 704,472 |

この bounded な共通出力の仕事では Markdig の adapter 込み時間と allocation が小さかった。Syntamark は追加 facts の計算を含むため、この差から parser のみの倍率や、resource/mapping/source 配布を維持した backend 置換の採算は求めない。`representation-unequal-work` の larger は Syntamark 2,978.525 µs／4,024,976 B、Markdig 310.875 µs／349,616 B で、facts＋syntax の追加負担を含む観測値である。Span 採用による改善量をこの結果だけから推定しない。

保持メモリは full GC 前後に8出力を同時保持した managed heap 差分。larger の1出力当たりの中央値は、representation が715,728 B／290,440 B、共通出力のみが83,812 B／79,972 B、Locations を含む共通出力が139,164 B／135,324 B（Syntamark／Markdig）。入力・snapshot・pipeline は既存 root として除き、temporary parse allocation は上の allocated B に現れる。同じ値でも string の共有・保持方式が違うため保持バイト数まで等値とはしない。RSS、native memory、長寿命 cache の測定ではない。

任意 decoded substring の `Map` は、Markdig の対応 adapter が未実装なので両側の共通段階から除外した。Syntamark の既存 projection に対する entity 1文字の query は別の単独 workload とし、中央値約96.6–104.1 ns、320 B/query を記録した。parse は含まない。この値を Markdig の未実装機能との0 cost 比較に使わない。

環境は .NET 10.0.14、Windows 10.0.26300、`Intel64 Family 6 Model 189 Stepping 1`、8 logical processors、workstation GC。専用環境ではなく、他 agent/build や OS scheduling は制御していない。larger 共通 facts のプロセス別中央値は Syntamark 1.390–1.651 ms、Markdig 0.329–0.351 ms、snapshot 段階は1.776–2.248 ms／0.400–0.556 msだった。medium で snapshot 段階が共通 facts より僅かに短いことも測定変動の範囲として保持し、段階差分を純粋な追加 cost と解釈しない。測定中の GC delta は全 workload 合計で Gen0=100、Gen1=0、Gen2=0。cold start/JIT、敵対入力、IDE、compiler-host、正式 package、任意 decoded mapping の比較は未検証で、wall-clock の CI gate は追加していない。
