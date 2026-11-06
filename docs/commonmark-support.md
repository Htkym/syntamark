# CommonMark 0.31.2 の現状

2026-11-06 に、基準 commit `6120fc0f231c952cdf01ed77784bb0f2e4a898d4` の runtime DLL と今回の位置修正候補で、公式の全 652 例を実行した。両者の各例の構文出力・分類・facts invariant 結果は一致した。実行環境は Windows 11、.NET SDK 10.0.403、Release である。

対象は現在の `lithosharp-markdown/1`。CommonMark 専用プロファイルはまだない。521 例がテスト用 HTML と一致し、129 例に差分があり、2 例は adapter の制約で観測できなかった。skip と未実行は 0。これは互換プロファイルの棚卸しであり、CommonMark 準拠率や確定不具合の件数として扱わない。

[公式 fixture](../tests/Fixtures/CommonMark/0.31.2/README.md) は版、原本 SHA-256、著作者とライセンスを固定した。実行時のネットワーク取得はない。[runner の実行手順](../tests/Syntamark.Conformance/README.md)には例番号での再実行、基準 DLL の指定、終了コードを記載した。

文法は internal AST をテスト専用 renderer で観測する。公開 API の front matter 切り出しと予算停止も別途確認し、それらを構文合格にしない。renderer は HTML 構文を捏造せず、未知の node を捨てない。独立した手製 DTO で escaping・リンク・画像・list・quote などを先に検証する。HTML の差分は adapter の不備も含み得るため、原文と保存した expected/actual を確認してから parser の修正を決める。

raw range の範囲、decoded segment の連続性、Linear segment の原文一致は全例で通った。これは facts の完全性や正しい意味を保証しない。独立した位置期待値と mapping の検証は Portability の回帰テストで行う。HTML の一致と facts の正しさは別の結果である。

| 章 | 一致 | 差分 | 観測不可 | 全例 | 残る例番号 | 影響・担当単位 |
|---|---:|---:|---:|---:|---|---|
| Tabs | 7 | 4 | 0 | 11 | 5, 6, 7, 9 | indent の空白と入れ子。SM04 A/B |
| Backslash escapes | 11 | 1 | 1 | 13 | 21, 24 | 21 は HTML、24 は raw info string の adapter 制約。SM04 E / test adapter |
| Entity and numeric character references | 13 | 3 | 1 | 17 | 25, 26, 31, 34 | HTML5 名、NUL 参照、HTML。34 は raw info string の adapter 制約。SM04 A/E / test adapter |
| Precedence | 1 | 0 | 0 | 1 | — | SM04 A |
| Thematic breaks | 18 | 1 | 0 | 19 | 60 | list との優先順位。SM04 A/B |
| ATX headings | 17 | 1 | 0 | 18 | 79 | 空見出しの closing marker。SM04 A |
| Setext headings | 23 | 4 | 0 | 27 | 88, 93, 96, 98 | thematic break・quote 境界、96/98 は front matter との profile 差。SM03 / SM04 A/B |
| Indented code blocks | 11 | 1 | 0 | 12 | 112 | 空行の空白保持。SM04 B |
| Fenced code blocks | 25 | 4 | 0 | 29 | 126, 127, 137, 139 | 未閉鎖 fence の EOF 改行。SM04 B |
| HTML blocks | 0 | 44 | 0 | 44 | 148–191 | HTML block node がない。本文・リンクの偽 facts を防ぐ必要がある。SM04 E / backend gate |
| Link reference definitions | 20 | 7 | 0 | 27 | 193, 195, 196, 198, 201, 208, 213 | 複数行と定義成立条件。SM04 D |
| Paragraphs | 8 | 0 | 0 | 8 | — | SM04 A |
| Blank lines | 1 | 0 | 0 | 1 | — | SM04 A |
| Block quotes | 20 | 5 | 0 | 25 | 237, 238, 249, 250, 251 | lazy continuation と block 境界。237 は fence EOF も含む。SM04 B |
| List items | 44 | 4 | 0 | 48 | 280, 285, 292, 295 | 空 item、段落中断、継続、indent。SM04 B |
| Lists | 15 | 11 | 0 | 26 | 301, 302, 307–313, 318, 319 | marker 変更、tight/loose、HTML 境界。SM04 B/E |
| Inlines | 1 | 0 | 0 | 1 | — | SM04 C |
| Code spans | 21 | 1 | 0 | 22 | 344 | inline HTML との優先順位。SM04 C/E |
| Emphasis and strong emphasis | 126 | 6 | 0 | 132 | 353, 354, 367, 475, 476, 477 | Unicode flanking、空 list marker、HTML 属性。SM04 B/C/E |
| Links | 77 | 13 | 0 | 90 | 491, 493, 494, 519, 520, 523, 524, 536, 540, 541, 547, 548, 552 | HTML、bracket、参照 label の case-fold・複数行。SM04 D/E |
| Images | 21 | 1 | 0 | 22 | 590 | 参照 label の bracket 条件。SM04 D |
| Autolinks | 16 | 3 | 0 | 19 | 606, 608, 611 | email の escape、裸 URL の拡張。SM03 / SM04 D |
| Raw HTML | 7 | 13 | 0 | 20 | 613–617, 623, 625–631 | 7 一致は主に HTML として成立しない入力であり、HTML 対応済みを意味しない。SM04 E |
| Hard line breaks | 13 | 2 | 0 | 15 | 642, 643 | HTML 属性内の改行。SM04 E |
| Soft line breaks | 2 | 0 | 0 | 2 | — | SM04 C |
| Textual content | 3 | 0 | 0 | 3 | — | SM04 A |
| 合計 | 521 | 129 | 2 | 652 | | |

観測不可の 24/34 は、AST が保持する raw info string をこの renderer が解釈していないためである。parser の未対応とは断定しない。front matter を除く既知差分の多くは互換プロファイルの現行方針と一致する。今回これらの文法を変更していない。

実行すると `.local/commonmark-original/results.json` と `.local/commonmark-candidate/results.json` に、例番号、章、分類、理由、expected/actual HTML、facade status、facts invariant を保存できる。章別の JSON と Markdown も生成する。未選択例も分母に残り `not-run` となる。inventory mode は既知の構文差分を許すが、fixture/adapter 自己検証、実行例外、facts invariant の失敗は終了コードを失敗にする。全例準拠を要求する場合は `--strict` を使う。

次は HTML blocks が最大の独立した不足カテゴリであることを踏まえ、SM12 の Markdig adapter と compiler-host 制約を比較して、独自 parser を継続する範囲を決める。その判断後に SM03 の opt-in profile を導入し、SM04 A の文字・entity・heading、B の code/list/quote、C/D の inline、E の HTML を章別に進める。HTML を後回しに固定せず、backend 判断は profile 実装の前に行う。
