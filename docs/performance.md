# 問い合わせと構文保持の費用

facts だけが必要な場合は `MarkdownParser.Parse`、構文を探索する場合は `MarkdownParser.ParseWithSyntax` を使う。構文 view は追加の node と元の文字列を保持し、構築にも時間と割り当てが必要になる。保持した node を解放するまで、その文書の元文字列も保持される。

## mapping と行位置

`MarkdownTextProjection.Map(RawSpan)` は decoded 範囲に対応する raw fragment と精度を返す。escape、entity、改行正規化などを含む範囲は `CoveringTokens`、対応が一部だけ分かる範囲は `Partial`、対応が不明な範囲は `Unknown` となり得る。結果の精度や fragment を確認せず、一つの連続した原文位置へ置き換えない。

`Map` は問い合わせごとに segment の内部 DTO を作り直さずに処理する。問い合わせ結果自体には割り当てがあり、割り当てが常に0になる API ではない。

`MarkdownDocument.GetLinePosition(int)` は raw UTF-16 offset を行・列へ変換する。raw offset は0始まり、返す行・列はいずれも1始まりである。CRLF の LF の位置は従来の公開契約に従い次行の1列目となる。

行位置の最初の非0 offset の問い合わせは入力を走査し、2回目に文書専用の行 index を作る。offset 0 だけの問い合わせでは index を作らない。初回や index 作成時には費用がかかり、index 作成後は反復した検索の再走査を減らせる。index は文書とともに保持され、global cache は使わない。

## 費用の選び方

構文 view と行 index は用途に応じて利用する。問い合わせを一度しか行わない場合や小さな文書では、初期化費用に見合う改善があるとは限らない。速度・割り当て・保持メモリは入力、問い合わせ範囲、profile、runtime によって変わる。

`ParseWithSyntax` は追加の node に output budget、構築の走査に scan budget を使う。同じ入力と予算でも、facts だけの解析より先に停止する場合がある。停止時は `Facts.Status` と diagnostics を確認し、null の `Root` を成功した空の木として扱わない。

構文 view は性能改善のための entry point ではない。利用方法と保持範囲は [構文 view](syntax-design.md)、解析上の制約は [profile](profile-design.md)を参照。
