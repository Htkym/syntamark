# CommonMark 0.31.2 の現状

## 現行の二つの profile

2026-11-06、Windows build 26300 / .NET SDK 10.0.403 / runtime 10.0.14 / Release の作業ツリーで、固定した公式全 652 例を profile ごとに実行した。`commonmark/0.31.2` は opt-in であり、既定値は引き続き `lithosharp-markdown/1` である。[profile の契約](profile-design.md)を参照。

| Profile | HTML 一致 | HTML 差分 | 観測不可 | facts invariant 違反 | syntax invariant 違反 | syntax の不明 raw range |
|---|---:|---:|---:|---:|---:|---:|
| `commonmark/0.31.2` | 652 | 0 | 0 | 0 | 0 | 0 |
| `lithosharp-markdown/1` | 521 | 129 | 2 | 0 | 0 | 25 |

両 profile とも skip・未実行は 0。標準 profile は `--syntax --strict` を付けて成功した。legacy の HTML 結果は初回の棚卸しから変わっていない。25 の不明 range は、legacy の内部 emphasis span に包含・順序の矛盾がある場合、新しい構文 view が `RawSpan: null` として示すものである。旧 AST/facts の文法を変更したり、推測した位置を Exact として返したりしていない。

現在の標準 profile の章別結果は次のとおり。すべての章で差分・観測不可・skip・未実行は 0 である。

| 章 | HTML 一致 / 全例 |
|---|---:|
| Tabs | 11 / 11 |
| Backslash escapes | 13 / 13 |
| Entity and numeric character references | 17 / 17 |
| Precedence | 1 / 1 |
| Thematic breaks | 19 / 19 |
| ATX headings | 18 / 18 |
| Setext headings | 27 / 27 |
| Indented code blocks | 12 / 12 |
| Fenced code blocks | 29 / 29 |
| HTML blocks | 44 / 44 |
| Link reference definitions | 27 / 27 |
| Paragraphs | 8 / 8 |
| Blank lines | 1 / 1 |
| Block quotes | 25 / 25 |
| List items | 48 / 48 |
| Lists | 26 / 26 |
| Inlines | 1 / 1 |
| Code spans | 22 / 22 |
| Emphasis and strong emphasis | 132 / 132 |
| Links | 90 / 90 |
| Images | 22 / 22 |
| Autolinks | 19 / 19 |
| Raw HTML | 20 / 20 |
| Hard line breaks | 15 / 15 |
| Soft line breaks | 2 / 2 |
| Textual content | 3 / 3 |
| 合計 | 652 / 652 |

実行結果は `.local/commonmark-verified/` と `.local/commonmark-legacy-verified/` に保存した。これは parser stamp の最終生成と package 発行を済ませたことを示す記録ではない。確認した source と最終 commit/artifact の対応は、生成 stamp と package canary の記録で別途照合する。

## 検証方法と結果の限界

[公式 fixture](../tests/Fixtures/CommonMark/0.31.2/README.md) は版、原本 SHA-256、著作者とライセンスを固定した。実行時のネットワーク取得はない。[runner の実行手順](../tests/Syntamark.Conformance/README.md)には例番号での再実行、基準 DLL の指定、終了コードを記載した。

文法は internal AST をテスト専用 renderer で観測する。公開 API の front matter 切り出しと予算停止も別途確認し、それらを構文合格にしない。renderer は HTML 構文を捏造せず、未知の node を捨てない。独立した手製 DTO で escaping・リンク・画像・list・quote などを先に検証する。HTML の差分は adapter の不備も含み得るため、原文と保存した expected/actual を確認してから parser の修正を決める。

raw range の範囲、decoded segment の連続性、Linear segment の原文一致は全例で通った。これは facts の完全性や正しい意味を保証しない。独立した位置期待値と mapping の検証は Portability の回帰テストで行う。HTML の一致と facts の正しさは別の結果である。

`--syntax` は公開 `ParseWithSyntax` の既知 raw range が入力内にあり、child が既知の parent の範囲内にあり、兄弟の順序・非重複を保つことを別欄で検証する。参照定義への `TargetRawSpan` は構文 child ではない。不明な range の件数も残すため、nullable の位置を合格率へ隠さない。

公式 652 例が通ったことは、あらゆる入力の完全準拠や facts の欠落・位置精度を証明しない。test renderer とその観測可能性にも限界がある。`markdownSyntax` capability は新 profile でも `Partial` を維持し、parse の終了状態 `Complete` と標準全体への適合を同一視しない。HTML renderer、DOM、MDX 実行、incremental parsing、全 consumer の採用互換性は、この結果で検証したものではない。

追加の位置回帰では、未解決参照 label の CR/CRLF/NUL 変換が raw と同じ値の `Linear` segment として残らないことを確認した。値が変わる token は `Atomic` として返し、resource catch 内の保持済み定義に再度の出力課金をしない。また、quote/list の lazy-continuation 構造 probe が参照定義を重複・移動させないことを両 profile で検査した。これらは facts の修正であり、legacy の文法を標準動作へ切り替える変更ではない。

## 初回の legacy 棚卸し

以下は初期段階の記録として保持する。基準 commit `6120fc0f231c952cdf01ed77784bb0f2e4a898d4` の runtime DLL と位置修正候補で全 652 例を実行し、各例の構文出力・分類・facts invariant 結果が一致した。当時は CommonMark 専用 profile の追加前で、legacy は 521 一致・129 差分・2 観測不可だった。この表の残例と担当単位は当初の調査分類であり、現在の opt-in profile の未対応一覧ではない。差分の件数を確定不具合の件数とは扱わない。

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

legacy で観測不可の 24/34 は、AST が保持する raw info string をこの renderer が解釈していないためである。parser の未対応とは断定しない。front matter を除く既知差分の多くは互換プロファイルの現行方針と一致する。legacy の文法は変更していない。標準 profile では parser が decoded info を返し、この二例も観測して一致した。

初期記録の `.local/commonmark-original/results.json` と `.local/commonmark-candidate/results.json` は保持する。runner は指定した出力先に、例番号、章、分類、理由、expected/actual HTML、facade status、facts/syntax invariant を保存し、章別の JSON と Markdown も生成する。未選択例も分母に残り `not-run` となる。inventory mode は既知の構文差分を許すが、fixture/adapter 自己検証、実行例外、facts/syntax invariant の失敗は終了コードを失敗にする。公式全例の一致を gate にする場合は `--strict` を使う。

初回は HTML blocks が最大の独立した不足カテゴリだった。[backend の判断](backend-decision.md)で配布・位置・予算の制約を比較し、opt-in profile の章別改善を進めた。現在は公式例の残差が 0 でも、位置 oracle の拡充、consumer の要求確認、compiler host・配布の実証と安全性の検証を別々に進める。
