# 問い合わせの性能検証

2026-11-06 の SM05 検証。`MarkdownTextProjection.Map(RawSpan)` は、公開 segment を内部 DTO に毎回コピーせず、内部の Map と同じ処理へ readonly struct の reader を渡す。`MarkdownDocument.GetLinePosition(int)` は、2回目の非0 offset の問い合わせで文書所有の行 index を作る。最初の問い合わせは従来の走査を保ち、offset 0 だけの問い合わせでは index を作らない。global cache は追加していない。

公開署名と既定の options hash は維持した。既存の内部 `MdSourceMapping.Map(MdTextProjection, MdRawRange)` も残し、共通処理を別名 `MapSegments` にした。既存の reflection による `Map` の検索も曖昧にしない。CRLF の LF 位置を次行の1列目とする従来の公開契約は、内部 `SourceText` の規則との差を補正して保持する。

## 固定した比較

基準は `de0a64272e5a75faceb1ff093d29d6bd10105984`。候補は同じ commit に [問い合わせの差分](../benchmarks/Syntamark.Benchmarks/Results/shared-helper/query.patch) だけを適用した独立 clone である。並行して進めた CommonMark profile・構文実装・公開 syntax API の変更は、この比較に含めていない。候補の package は発行していない。

測定専用 clone の generated ParserVersion は基準値を保持した。候補の識別には commit、差分 patch の hash、実際に load した assembly の hash を使う。この実験を、最終製品の canonical stamp や runtime/source pair の一致を確認した証拠にはしない。

SDK 10.0.403、.NET 10.0.14、Windows 10.0.26300、Intel Family 6 Model 189、8 logical processors。Release build、`DOTNET_TieredCompilation=0` で、基準→候補→候補→基準の順に4つの独立 process を実行した。query 本体と callback を warmup し、各 process で各 workload を5回測った。下表は各版の全10 sample の中央値と最小–最大であり、不都合な sample も除いていない。CPU affinity は固定しておらず、専用機での測定ではない。

測定コード・再実行手順は [benchmark README](../benchmarks/Syntamark.Benchmarks/README.md)、全 sample・GC 回数・fixture hash・assembly hash・options hash は [結果ディレクトリ](../benchmarks/Syntamark.Benchmarks/Results/shared-helper) にある。差分 patch の SHA-256 は `89523824d6ae1fc32ef3d1c44fe7b06246b30b57eb393a0e8d31e7787af1e644`。

small は63 UTF-16単位、medium は15360単位。両方の parse は Complete で、resource 中断はない。Unknown mapping の query は indented code の意図的な Unknown projection を別に使い、完全な parse の速度として扱っていない。query の時間に parse は含めず、parse を別の workload にした。初回 query は、JIT warmup 後の新しい document への最初の問い合わせを意味する。process 起動時間ではない。

両版の全 offset、全 projection の全 subrange・0幅 query・Unknown query の結果 hash は `d03842829c409e51f2bc068a7188fd949b0044ac4575029c04b11ee0d06309a9` で一致した。fragment と precision、並行した初期化も [QueryChecks](../tests/Syntamark.Portability/QueryChecks.cs) で確認する。これは固定した fixture の同値性であり、全入力の正しさを証明するものではない。

## 結果

時間の単位は ns/op。範囲は括弧内、割り当ては B/op。

| 問い合わせ | 基準の時間 | 候補の時間 | 基準の割り当て | 候補の割り当て |
|---|---:|---:|---:|---:|
| Map、Linear、反復 | 266.8 (253.4–292.3) | 98.4 (91.3–108.9) | 792 | 320 |
| Map、Atomic、反復 | 271.2 (263.9–301.4) | 99.9 (92.1–117.4) | 792 | 320 |
| Map、0幅、反復 | 286.0 (276.4–316.5) | 128.2 (119.0–151.7) | 944 | 472 |
| Map、Unknown、反復 | 165.6 (152.4–180.1) | 42.0 (36.6–53.9) | 400 | 152 |
| Map、初回 | 282.8 (256.2–318.8) | 121.9 (103.1–209.4) | 792 | 320 |
| 行位置、ランダム、反復 | 4453.2 (4214.1–4899.1) | 46.0 (35.3–60.4) | 0 | 0 |
| 行位置、昇順、反復 | 2873.7 (2649.3–3057.5) | 20.4 (18.6–24.5) | 0 | 0 |
| 行位置、初回、先頭付近 | 46.9 (37.5–109.4) | 82.8 (71.9–196.9) | 0 | 0 |
| 行位置、初回、末尾 | 9362.5 (8978.1–10296.9) | 10396.9 (9134.4–11756.2) | 0 | 0 |
| 行位置、2回目、index 作成 | 9371.9 (8762.5–10665.6) | 8995.3 (8300.0–47818.8) | 0 | 12328 |

Map の DTO 再構築をなくす効果と、反復した行位置問い合わせの改善を確認した。初回の行位置問い合わせは field の初期化管理が増えるため、この測定でも遅くなった。文書末尾への初回 query に速度改善は主張しない。index 作成は一度だけ12328 Bを割り当て、以後の query は割り当て0であった。

parse-small の中央値は11.59→11.71 µs、割り当ては23737.3→23753.3 B。parse-medium は2.42→2.12 msだったが、時間の範囲は重なるため、parser 自体の高速化とは判断しない。medium の割り当ても3435620→3435669 Bであり、全体の allocation 削減を意味しない。問い合わせの削減効果と parse のコストを区別する。

## 保持メモリと選ばなかった案

同じ raw string を共有する32 document を保持し、明示 GC 後の managed heap 差分を測った。raw string の新規 allocation は含めない。これは近似的な heap 観測であり、object layout の厳密な合計ではない。

| medium 文書への操作 | 基準 B/document | 採用候補 B/document |
|---|---:|---:|
| 問い合わせなし | 279168 | 279184 |
| 全 projection に Map | 279174 | 279190 |
| 行 index 作成後 | 279168 | 283088 |

保持増加は問い合わせなしで16 B/document、index 作成後で3920 B/documentであった。Map を1つだけ呼んだ観測には GC に伴う約257 B/documentの揺れがあり、微小なメモリ削減の証拠にはしない。

最初に試した lazy core cache は、反復 Map が792→352 B/query、約244→83 nsとなった。一方、全 projection を問い合わせた後の保持は279174→443025 B/documentと58.7%増えたため、採用しなかった。[却下案の差分と全 sample](../benchmarks/Syntamark.Benchmarks/Results/lazy-cache) を残している。採用した reader は cache と segment の二重保持を作らず、初回から DTO 再構築を省く。reader による値の読み取りにはコストがあるが、今回の結果では保持量と割り当ての両方で適切だった。

公開結果の Span 化、pool、body/行の slice 化は行っていない。今回測定した支配的な query コストは DTO の作り直しと入力先頭からの再走査であり、それらは追加依存なしで削減できた。本文 parse の内部 Span 化、process 起動、IDE 全体、実際の consumer、別 OS/CPU の性能は未検証である。

## 構文を保持する費用

D5 の判断として、同じ作業中 DLL の `Parse` と `ParseWithSyntax` を比較した。profile は `commonmark/0.31.2`、同じ既定予算・入力・binding を使う。2つの独立 process、各5 sampleで、各processではParseを先に測った。syntax 呼び出し用 delegate は事前に構築し、timed 範囲に reflection は含めない。facts の全公開値は ParserVersion だけを除いて一致し、両経路とも Complete だった。入力は見出し、強調、entity、link、quote、fenceから成り、medium はsmallを80回繰り返す。括弧内は全sampleの最小–最大、allocationも全sampleの中央値を丸めた値である。

| 入力 | syntax node数 | Parseの時間中央値 | ParseWithSyntaxの時間中央値 | allocation B/parse | 保持 B/document、概数 |
|---|---:|---:|---:|---:|---:|
| small、97 UTF-16単位 | 19 | 15.70 (13.08–22.46) µs | 18.69 (15.86–22.60) µs | 32194 → 42538 | 3072 → 7280 |
| medium、7760単位 | 1441 | 1.069 (0.845–1.288) ms | 1.403 (1.284–1.610) ms | 1896078 → 2646659 | 155168 → 475016 |

構文を使わない caller にこの保持費用を負わせないため、別 entry point による opt-in を採用する根拠となった。これは2入力の観測であり、構文の正しさや全 consumer の性能を証明するものではない。全 sample と最小–最大は [syntax 保持の結果](../benchmarks/Syntamark.Benchmarks/Results/syntax-retention) にある。

この測定は2026-11-06 01:13:32 UTCの私有作業中 DLL、SHA-256 `75aaac05d717d0866f2e4b4adda9bef891650a9dd1713e99126731339977977e` を固定した。de0a642 の query A/Bとは別の実験である。後続の構文位置修正や最終 package identity の確認には、その後の検証を使う。

## list の再走査を減らす

SM07 では CommonMark list の lazy continuation を調べ、各行を加えるたびに item の構造全体を `ParseStructure` で調べ直す経路を確認した。修正前のscan会計は、continuation 200/400/800行で641018/2401618/9282818 unitsであった。list item の paragraph tail を一度確認した後は、次の行も検証済みの lazy continuation の場合にだけ結果を再利用する。空行やindent付きの内容を加えた時点でcacheを破棄する。legacy profile にはこのcacheを使わない。

`- start\n` と `continuation\n` の反復だけを含む同じ入力を、修正前→修正後→修正後→修正前の4 process、各5 sampleで測定した。各行数について全公開factsのhashが一致し、すべてCompleteだった。時間の単位はms/parse、括弧内は最小–最大、allocationは十進MB/parse。

| continuation 行数 | UTF-16単位 | 修正前の時間中央値 | 修正後の時間中央値 | 修正前のallocation | 修正後のallocation |
|---|---:|---:|---:|---:|---:|
| 200 | 2608 | 0.849 (0.732–0.935) | 0.331 (0.218–0.504) | 3.419 | 0.353 |
| 400 | 5208 | 2.810 (2.670–2.967) | 0.516 (0.431–0.649) | 12.809 | 0.692 |
| 800 | 10408 | 10.084 (9.808–10.314) | 0.965 (0.931–1.129) | 49.528 | 1.367 |

1600行は、同じ既定scan予算で修正前が `Partial/LMD003/scan`、修正後がCompleteとなった。この差は安全に解析できる範囲の改善として記録し、速度比較から除外した。parseの仕事が異なる結果を同じ速度表へ混ぜていない。修正後の内部scan unitsは、この実験では計測していない。

[全 sample・GC・semantic hash・入力hash](../benchmarks/Syntamark.Benchmarks/Results/list-tail) を保存した。修正前DLLは上記D5と同じsnapshot、修正後は01:15:41 UTCのsnapshot、SHA-256 `451e51537b807302b3d33c40472091694d1dea4a303b6c7ddfa79e53e535f137`。source labelとassembly hashで両者を固定し、後続のlist位置修正やlegacy probe修正とは分けている。この結果を一般文書、Markdig、別hostの速度へ一般化しない。

ここでは Span を導入せず、測定で支配的だった構造の再生成と再走査を削減した。追加依存やpoolは不要だった。ほかの文法カテゴリや本文全体のallocation最適化は、別のworkloadと同値性確認を用意してから検討する。
