# C# 解析基盤の初回改善

2026-11-06 に、[改善計画原本](https://drive.google.com/file/d/1FaSzTIAhAOmxqPergfsEhB8PywUrCbuF/view)の初回範囲を実施した。対象は SM 00・01・02、SM 12 の機能比較、SM 11 の早期 source/Roslyn canary。CommonMark 対応全体や公開構文 API の完成報告ではない。

## 基準と互換性

計画基準と作業開始時の origin/main はともに `6120fc0f231c952cdf01ed77784bb0f2e4a898d4`。関連する open PR はなかった。既存 checkout は clean な main `19137f664ca482df92f86e708406d1a13fa88eb7` だったため、変更せず独立 clone と作業ブランチ `work/syntamark-foundation-20261106` を使用した。

未変更の基準版で既存 Portability を実行し、runtime の net10.0、portable の netstandard2.0、MD-02 の位置・予算・cancel 検査と追加レビュー検査が通った。SDK は 10.0.403、OS は Windows。依存は既存 lock の YamlDotNet 18.1.0。共有 NuGet scratch の権限不足で最初の restore を中止し、一時ディレクトリの専用 cache に切り替えた。sandbox 内の実行は compiler の診断を返さず失敗したため、その結果をコードの失敗とせず、許可された実行環境で baseline を確認した。

既存 profile `lithosharp-markdown/1`、既定 options/hash、contract 1.0、公開 facade の signature と外部 friend grant は維持する。CommonMark profile はまだ導入しない。歴史的な `docs/contracts/markdown-consumer-conformance-v1.json` と immutable package の記録は変更しない。

## 全 facts の固定比較

`tests/Syntamark.Portability/Fixtures/facts-baseline-6120fc0.json` は、未変更の基準版 DLL を保存し、その DLL を明示的に読み込んで作成した。ParserVersion は `1/7e854150d5cad96e554b90a550251dce74d29b79ceef2964fa9cfa4a226e2051`。旧 fixture の3入力に、入れ子の参照、通常 table、opaque による Partial、code 正規化を追加した7入力を使う。

比較対象は全 document facts、見出し・section・link・fence・text region、raw/decoded span、全 source segment、status、coverage、diagnostic、capability、collection の順序。各 projection について全 decoded 境界の0幅 query、各 UTF-16 unit の1幅 query、全体 query を含める。公開 facade の全 property と全 raw offset の行位置も比較する。プロパティ名は ordinal 順、列挙値は名前、配列は元順の JSON として SHA-256 を取る。意味比較から除く値は `ParserVersion` だけで、元の値は fixture の別欄に保持する。runtime と portable の全 facts も同じ正規化で比較する。

これは回帰検出であり、正しい Markdown の独立 oracle ではない。位置の正否は `MarkdownFactsChecks` と `MappingRegressionChecks` の手書き期待値・原文 slice で確認する。捕捉対象の入力や query は有限であり、全入力の互換性を証明しない。期待値の一括再生成はしない。再取得する場合は基準 commit の両 TFM を別領域で build し、次の明示操作を使う。出力先が既存なら失敗し、基準 ParserVersion と異なる DLL も拒否する。

```powershell
dotnet run --project tests/Syntamark.Portability -c Release --no-restore -- `
  --capture-facts <baseline-runtime-dll> <baseline-portable-dll> <new-json>
```

## 検証と残りの順序

位置の再現と修正は SM 02 として確認した。`| a\|b [Guide](/docs) |` の行を持つ最小 table では、リンクの raw 範囲が `[16,30)`、target が `[24,29)` だったが、独立期待値はそれぞれ `[19,33)` と `[27,32)`。`before [x]() [y](<>)` の空 target はどちらも `[0,0)` だったが、期待値は `[11,11)` と `[18,18)`。修正はセルの原文開始位置と escaped pipe の変換幅を保持し、空 span の位置を捨てないようにした。本文値と既存の table 前処理は維持する。`tableCell.RawSpan` は従来どおり table 全体であり、cell 固有の精密 span とは説明しない。

回帰テストは11入力の原文 slice、変換 token の Atomic mapping、0幅 query、CRLF、BOM/front matter、quote/list、反復セル、空セルを確認する。runtime/portable の結果一致も確認する。NUL 置換の追加疑義は不具合ではなく、既存 `PublishText` が原文との差を Atomic にすることを確認した。予算と cancellation は維持するが、新たな table 位置計算の走査も計上するため、極端に小さい scan budget の停止閾値が旧版と完全に同じとは保証しない。

## 現在の consumer

SharpDeps は main `c94c99ed35988be686b6aa8451c008f72c5eac62` の [runtime 参照](https://github.com/Htkym/sharpdeps/blob/c94c99ed35988be686b6aa8451c008f72c5eac62/analyzer/src/SharpDeps.Analysis.Markdown/SharpDeps.Analysis.Markdown.csproj)と、[identity 検査](https://github.com/Htkym/sharpdeps/blob/c94c99ed35988be686b6aa8451c008f72c5eac62/analyzer/src/SharpDeps.Analysis.Markdown/MarkdownRuntimePin.cs)を確認した。Syntamark 1.0.0、parser/canonical hash、runtime DLL hash、profile、contract、YamlDotNet を固定しており、別候補への無条件な差し替えは拒否される。[symbol resolver](https://github.com/Htkym/sharpdeps/blob/c94c99ed35988be686b6aa8451c008f72c5eac62/analyzer/src/SharpDeps.Analysis.Markdown/MarkdownSymbolResolver.cs)は decoded text の `Map` と precision を直接利用する。production の source package 利用は確認していない。

LithoSharp は main `1c1b54c86ef75511f8af30d7f3d3fc445d084909` の [runtime compiler](https://github.com/Htkym/lithosharp/blob/1c1b54c86ef75511f8af30d7f3d3fc445d084909/src/LithoSharp/Content/Compilation/LithoMarkdownCompiler.cs)が friend API の `LithoBlockParser` と内部ノードを利用し、[source host adapter](https://github.com/Htkym/lithosharp/blob/1c1b54c86ef75511f8af30d7f3d3fc445d084909/src/LithoSharp.Generators/MarkdownSourceHostAdapter.cs)が Syntamark.Source 1.0.0 の stamp/profile を検査している。今回 `LithoLineMap(int[] LocalStarts, int[] BodyStarts, int TextLength, int BodyEnd)` と既存 method の signature は維持した。確認した両 consumer の現行 C# に直接の `LithoLineMap` 利用は見つからなかったが、外部 friend 自体の候補版による再ビルドは未実行。

計画中の yomiyasu consumer の所在と API は未確認。`Htkym/yomiyasu` は見つからず、同名のインストール済みスキルを consumer アプリと同一視していない。prose API の要求元として推測で仕様を追加しない。

## 次の工程

最終候補の canonical ParserVersion は `1/a6a14ab542fa607d81208f8dcb67a4243b807ac744d5492cf4e067f872170b86`。Git に正規化された22ファイルの blob から算出し、生成 stamp 自身は hash から除外した。全 facts の旧版比較、両 TFM の Portability、位置の11入力、652公式例の facts invariant、Markdig の共通5入力は通った。公式例の観測結果は521一致・129差分・2観測不可で、基準版と同じだった。独立した静的レビューで見つかった portable stamp 照合と Linux の profile path 取得は修正した。

通常 CI は同じ検証に加え、公開済み source package の canary と、対象 commit から作る未公開の source package を実 compiler host で検査する。`2.0.0-preview.20261106` は専用の一時 feed/cache で使う検証用番号であり、公開候補の発行や immutable registry の更新ではない。manifest の stamp、source target、実ロードした parser stamp を照合する。検証出力は workflow artifact として保持する。構文差分129件を許す inventory と、位置・mapping の invariant 失敗を許さない gate を区別する。

ローカルでは source commit `74ab02314e7d41ef0446ffe270d14e9062e36976` の source package を実際に pack して消費した。package SHA-256 は `3a6ae840e103da88e8fef99406d3bfa0c8155e32329e77273fbad42a8b9514ba`。stage の22ファイルの byte 検査、manifest と実 compiler の stamp 照合、YamlDotNet 18.1.0 の実ロード、空 destination の `[42,42)`、診断の4行3〜15列、編集後の警告消去が通った。検証スクリプトの candidate restore は、Windows で URL が local path と解釈されたため、XML escape した専用 NuGet.Config に修正した。既存 lock と公開 package は変更していない。

公式例の章別対応状況は [CommonMark 対応表](commonmark-support.md)、Markdig の共通出力と差分は [比較結果](library-comparison.md)、compiler host の条件は [Roslyn 統合](roslyn-integration.md)を参照する。

Span 化や cache 最適化の効果は未測定。今回の位置修正を高速化として評価しない。次は公式例で最大の差分カテゴリを選び、現 parser の限定修正と backend adapter の制約を比較した上で、SM 03 の opt-in profile と SM 04 の章別対応量を決める。core への Roslyn/Markdig 依存追加、公開 AST、prose 契約、consumer の採用変更、Rust、merge/release は今回の範囲に含めない。
