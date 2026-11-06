# CommonMark 実装方式の判断

2026-11-06、SM 01・11・12 の初回 gate 後の判断。既存の bounded portable parser を維持し、CommonMark profile を opt-in で追加する。Markdig の製品 backend 化は今は行わない。

## 根拠

既存 profile の全652公式例は521一致・129差分・2観測不可だった。最大の独立カテゴリは HTML blocks の44例で、現 AST に HTML node がない。7種類の block 開始・終了条件、段落中断、inline token の認識と原文位置を追加する範囲を、HTML renderer や DOM なしで切り出せる。この段階の実装量を全体の再実装と同一視しない。

Markdig 1.5.1 は公開 AST、HTML 構文、精密 token span、entity の `Original`/`Transcoded` を持ち、共通5入力の facts adapter は成立した。しかし、任意の decoded substring から複数 raw fragment と precision に戻す adapter は未実装。空 destination の lexical span と空内容の既知位置にも契約差がある。

Markdig の通常の [parse entry](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Markdown.cs)、[parser loop](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Parsers/MarkdownParser.cs)、[context](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/MarkdownParserContext.cs)には、現在の Syntamark と同じ scan/output budget や解析中 CancellationToken の契約がない。nesting limit だけで同等の中断保証とは扱わない。後段の facts adapter だけではこの差を解消できない。

同版の upstream source は [preview/unsafe の設定](https://github.com/xoofx/markdig/blob/1.5.1/src/Markdig/Markdig.targets)と `field` を使う言語構文を含む。C# 13 の Syntamark.Source にそのまま取り込む変更にはならない。net10.0 の比較 lock では Markdig 自体に追加 package dependency がないが、netstandard2.0 の nuspec は System.Memory 4.6.3 以上を要求する。その下限版は System.Buffers 4.6.1、System.Numerics.Vectors 4.6.1、Unsafe 6.1.2 以上に依存する。この下限 closure は metadata の確認であり、locked restore と compiler host の実ロードは未検証。

## 実装と再評価の条件

旧 profile・既定値・旧 options hash を維持し、`commonmark/0.31.2` を明示選択する。HTML の標準構文はそこで認識し、旧 renderer/friend consumer の文法を黙って変えない。章別の公式例、独立した raw/mapping oracle、予算・cancellation、runtime/source host を gate とする。

主要な差分カテゴリを直すたびに全652例を再分類する。章別修正の維持負担が大きくなる場合、または同等 mapping・budget・C# 13 source host を成立させる具体的な backend adapter 案ができた場合に置換を再検討する。Markdig に置き換えれば必ず速い、または独自実装なら必ず速いとは判断していない。

既存 consumer は 1.0.0 の parser/package hash を固定しているため、自動移行しない。CommonMark と構文 view は追加 API として検証し、実際の採用変更は別の作業領域で行う。新 backend の採用や全面書き直しが必要になれば、見積り・互換性・依存 closure を提示してから方針を決める。
