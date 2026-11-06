# 改善計画の実装・検証記録

2026-11-06。原計画の C# 改善系列を、基盤・CommonMark・構文 API・問い合わせの性能改善に分けた。Rust は保留し、公開済み 1.0.1、既存 consumer の pin、main、merge/release は変更していない。これは候補をレビューできる状態の記録であり、全入力の完全準拠や公開承認ではない。

## 到達条件

| 計画 | 実装・検証した範囲 | 記録 |
|---|---|---|
| SM00–01 | 原計画と基準 commit を固定し、公式652例を版・checksum・出典付きで登録。章別と観測不可を区別 | [対応表](commonmark-support.md) |
| SM02 | table の escaped pipe、空 destination を再現後に修正。CR/NUL label の偽 Linear precision と probe の参照定義重複も再現・補正 | [初回記録](foundation-progress.md)、[安全性](safety.md) |
| SM03–04 | opt-in CommonMark、HTML7種、entity・Unicode参照、block/inline境界を追加。既定の互換 profile と旧 hash を保持 | [profile](profile-design.md)、[実装方式](backend-decision.md) |
| SM05・07 | Map reader、遅延行 index、list tail probe を実測して選択。cache案は保持量の増加から不採用。全面Span化は行わない | [性能](performance.md)、保存した生サンプル |
| SM06 | 公開 API だけの日本語 prose sample、元 projection への Map、除外理由、Python/UTF-16 変換。実際の Yomiyasu 2.5.0 の可視範囲を読み取り専用で比較 | [prose](prose-consumer.md) |
| SM08 | 両 profile、4予算境界、216文書、固定seed、実 inline scan 中 cancellation。範囲と Linear 等値も確認 | [安全性](safety.md) |
| SM09 | 両 TFM、既存225公開宣言とfriend宣言、YamlDotNet固定依存、private runtime/source pair、実consumerのcompileとpinを別々に検査 | 下記の候補とconsumer記録 |
| SM10–11 | 一回の解析からimmutable構文/facts、反復探索、null raw位置、resource停止。source側同等entryを実Roslyn AdditionalFiles診断で確認 | [構文](syntax-design.md)、[Roslyn](roslyn-integration.md) |
| SM12 | Markdig固定版の公式例・共通facts・snapshot→Locationsを比較。仕事量と任意decoded mappingの未対応を区別 | [比較](library-comparison.md) |

標準 profile は公式652/652、facts/syntax invariant 違反0、syntax位置不明0。互換 profile は521一致・129差分・2観測不可を保持し、24例の既存 emphasis span の矛盾を25個のnullable syntax rangeとして明示した。skip/未実行は0。HTML出力の一致とfacts精度の証明は分ける。追加の入れ子tab/raw code tabは8例で独立期待値とMarkdigに一致した。

## Commit と候補

| 段階 | Commit | Parser stamp の hash |
|---|---|---|
| 初回基盤 | de0a64272e5a75faceb1ff093d29d6bd10105984 | a6a14ab542fa607d81208f8dcb67a4243b807ac744d5492cf4e067f872170b86 |
| CommonMark | b6af5bd5cbce8b151796ba7220b195e047af9a36 | 2668e129dcb3d9b2f5254b24bd7f5f1963f7d4f75066259380b316bfba805c32 |
| 構文 API | e154149588c780ae4aecff6dde215c123a73e046 | 84e8fc4c12334490ed3587247cb8ba9fdaa12922e333485ce9f6567d7a993af7 |
| 問い合わせ | 5ae109d2495c297ccc80b7ade08af303dc58e0e7 | 729be2576781f2016a0340240c8ae80b902dea9afbbbaaabc7ee829c955bdb5d |

stampは各commitのPortable/Contractsのcanonical Git blobから生成した。runtime facade・packaging入力もpair manifestの別hashで照合する。性能測定のprototypeは各実DLL・差分hashで識別しており、この表の最終packageそのものの速度を測ったとは扱わない。

CommonMark候補のprivate runtime/source 2.0.0-preview.202611061は生成・seal・check-pairと実source hostで成功した。構文候補2.0.0-preview.2026110602もSDK10.0.403の実compiler/driverで、選択profile、Document/HTML、legacy YAML Parsed、4行3〜15列、編集後の診断消去を確認した。元のlockは保持した。SDK10.0.401での構文・API検証も成功したが、同runのhostは長いWindows cacheパスで失敗したため、短い専用領域・SDK10.0.403でhostだけを再検証した。Integration formatは成功終了し、workspace-load警告を記録した。

最終のprivate runtime/source 2.0.0-preview.2026110603は、表の問い合わせcommitから作り、seal/check-pair・両TFM/API・実source compiler/driver・両benchmarkのbuildを確認した。最終の比較benchmark書式違反もSDK formatterで修正し、style/whitespaceを再確認した。修正は空白だけであり、core pairや測定結果は変更していない。

| 最終pair | SHA-256 |
|---|---|
| pair manifest | 80f9d4584c17f53b096f68fa38d5c9f0b72715c93d7bbe921d1f040f41ac6103 |
| runtime nupkg | fa6c399f8a5cdf1fa86d359f8f25986f11765fc11b2398e8a196b55d1523cb87 |
| runtime DLL | 2c2452680ab0b6a5c57e7405adb3fcb16a022f41027f53fad4fed7218b80f869 |
| source nupkg | da7f2979c5eec53c21550c9cc928794a29f6555a99c7cab4163b2af434044d08 |

pairはローカル作業領域の `.local/final-pair-5ae109d/` にbyte照合後のコピーを保持した。manifestのstateはseal時点の `PACKED_NOT_HOST_VERIFIED` を維持し、実host・consumer検証の結果は別の記録へ保存している。公開やimmutable registryの更新ではない。

実consumerは原本を変更せず、独立cloneで検証した。LithoSharpはfriend API・netstandard source hostのbuild、既存portability/render、明示manifestによる3入力の全normalized facts比較が成功した。警告659件は基準版と同じ既存XML文書警告で、エラー0。SharpDepsも実adapter/test APIのbuildが警告・エラー0。旧pinの3検査は新hashを予定どおり拒否し、未変更のRequireLoaded・RequireFacts・ParseForCompiler guardも直接確認した。これは採用成功とは扱わない。基準版のSharpDepsは23 passed/0 failed/0 skippedであり、候補に対する全consumer suiteは実行していない。実DLL/stamp/YamlDotNetの照合と原本clean確認は、task配下の `.local/consumers/consumer-final-summary.json` とログに記録した。

基盤draft #7を保全し、CommonMark [#8](https://github.com/Htkym/syntamark/pull/8)、構文API [#9](https://github.com/Htkym/syntamark/pull/9)、性能改善はその上へ積むdraftとして分ける。#8のPR CI [42147330979](https://github.com/Htkym/syntamark/actions/runs/42147330979)、#9のPR CI [42147759946](https://github.com/Htkym/syntamark/actions/runs/42147759946)は成功した。実装レビューとCIの成功を、GitHub上の承認レビューや公開承認と混同しない。

## 性能判断と残る条件

反復Mapは測定した限定fixtureで792→320 B/query、中央値266.8→98.4 ns。行indexには初期化と保持量の増加があり、最初の問い合わせは遅くなる場合がある。list N800は同じfacts/Completeで10.084→0.965 ms。一方、最大の共通facts workloadではSyntamark1.541 ms/3,110,516 B、Markdig0.341 ms/590,848 Bだった。追加facts・mapping・budgetの仕事量を明示し、parser単体の順位や全面置換の採算にはしない。syntax opt-inの追加時間・割当て・保持量も保存している。

実装の独立静的レビューに残る修正必須の指摘はない。正式なGitHub承認レビュー・merge・package公開は別段階。IDE、Visual Basic、下位SDK/.NET Frameworkのcompiler host、全入力の正しさ、すべてのregex/library call内の中断遅延、cold startやnative memoryは未検証。Yomiyasu本体への採用・実文書/CLI全体の互換性も未検証である。内部Line recordは旧constructorを維持したが、新しい標準解析の列情報はrecordのequality/hashと内部layoutに影響する。
