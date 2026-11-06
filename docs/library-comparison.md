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
