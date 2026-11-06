# Syntamark の限定 benchmark

SM05 の公開 Map/行位置 query の比較用 runner。製品に新しい依存を追加しない。YamlDotNet は製品と同じ18.1.0を固定し、比較する Syntamark assembly は `SyntamarkAssembly` で指定する。比較対象を同時に load せず、各版を独立 process で動かす。

問い合わせの使い方と費用は [問い合わせと構文保持の費用](../../docs/performance.md)を参照する。選んだ版の全 sample は `Results/shared-helper`、採用しなかった二重保持 cache は `Results/lazy-cache` にある。

## 再実行

リポジトリのルートから、書き込み可能な独立 clone を作る。既存の出力を上書きしない名前を使う。

```powershell
git clone --shared . .local/perf-base
git -C .local/perf-base checkout --detach de0a64272e5a75faceb1ff093d29d6bd10105984
git clone --shared . .local/perf-helper
git -C .local/perf-helper checkout --detach de0a64272e5a75faceb1ff093d29d6bd10105984
git -C .local/perf-helper apply (Resolve-Path benchmarks/Syntamark.Benchmarks/Results/shared-helper/query.patch)
dotnet restore .local/perf-base/src/Syntamark --locked-mode
dotnet restore .local/perf-helper/src/Syntamark --locked-mode
dotnet build .local/perf-base/src/Syntamark -c Release --no-restore
dotnet build .local/perf-helper/src/Syntamark -c Release --no-restore
dotnet restore benchmarks/Syntamark.Benchmarks --locked-mode
dotnet build benchmarks/Syntamark.Benchmarks -c Release --no-restore `
  -p:SyntamarkAssembly="$((Resolve-Path .local/perf-base/src/Syntamark/bin/Release/net10.0/Syntamark.dll).Path)" `
  -o .local/perf-run-base
dotnet build benchmarks/Syntamark.Benchmarks -c Release --no-restore `
  -p:SyntamarkAssembly="$((Resolve-Path .local/perf-helper/src/Syntamark/bin/Release/net10.0/Syntamark.dll).Path)" `
  -o .local/perf-run-helper
$env:DOTNET_TieredCompilation = '0'
dotnet .local/perf-run-base/Syntamark.Benchmarks.dll baseline-a de0a642 baseline-a.json
dotnet .local/perf-run-helper/Syntamark.Benchmarks.dll helper-a de0a642+query.patch helper-a.json
dotnet .local/perf-run-helper/Syntamark.Benchmarks.dll helper-b de0a642+query.patch helper-b.json
dotnet .local/perf-run-base/Syntamark.Benchmarks.dll baseline-b de0a642 baseline-b.json
```

`dotnet build` 等は環境の通常の検証 wrapper から呼んでもよい。sandbox の user NuGet.Config/lock へのアクセスが制限される場合は、承認された実行環境または明示した独立 NuGet.Config/cache を使う。依存版は変えない。

runner の引数は `<label> <source-commit-or-label> <new-results.json>`。出力は `CreateNew` のため既存結果を上書きしない。`--snapshot` は query fixture の semantic hash だけを表示する。label/source は記録情報であり、assembly の正体は出力内の SHA-256 と合わせて確認する。

新profile/構文APIを含むassembly向けには、次の別modeもある。使うDLLを `SyntamarkAssembly` に指定して別のoutputへbuildする。query A/Bの基準版de0a642は、これらのAPI/profileをまだ持たないため対象にしない。

```powershell
dotnet <runner>/Syntamark.Benchmarks.dll --syntax <source-label> <new-syntax-results.json>
dotnet <runner>/Syntamark.Benchmarks.dll --list <source-label> <new-list-results.json>
```

`--syntax` は同じDLLの `Parse` と `ParseWithSyntax` を比較し、facts一致、node数、時間、allocation、32 documentの保持量を保存する。reflectionはsetup時だけで、timed範囲では事前に構築したdelegateを呼ぶ。`--list` はCommonMarkのlazy continuation 200/400/800行がCompleteであることを確認して測る。1600行の既定予算の結果は分類だけを保存し、timed結果に含めない。実測の私有snapshotは `Results/syntax-retention` と `Results/list-tail` にあり、source labelと実ロードassembly hashで区別する。

## workload と比較条件

- small は見出し、entity、escape、emoji、link、code span、CRLFからなる63 UTF-16単位の入力。
- medium はsmallとquote2行を160回繰り返す15360単位の入力。生成処理とseedは `Program.cs` に固定する。
- Map は parse 済み projection への Linear、Atomic、0幅、Unknown と、fresh document への最初の query を分ける。Unknown は `    code\n` の projection。中断された解析の速さを測るものではない。
- 行位置は parse 済み medium document への固定seedのランダム/昇順 query、fresh document の先頭付近/末尾への最初の query、2回目の index 作成を分ける。offset 0だけではindexは作らない。
- callback と query 方法を warmup してから5 sampleを保存する。fresh query は毎回新しい32 document を用意し、parse は timerの外で実行する。
- managed allocation、GC0/1/2回数、時間を記録する。結果をstatic sinkへ格納し、処理が省略されないようにする。
- retained は32 documentの問い合わせなし/1 Map/全 Map/行 indexを別々に保持して、明示GC前後のmanaged heap差分を観測する。queryの割り当てと生存メモリを同一視しない。
- resource中断・Failedは測定結果にしない。完全なparseを測るsmall/mediumのstatusはJSONへ保存する。

`QueryChecks` は固定した基準 hash、CRLF境界、全 offset、全 projection subrange、並行した初期化を検証する。性能のwall-clock値をCIの合否条件にしていない。保存した測定時間は専用機以外の限定的な観測であり、別の文書や実行hostへ一般化しない。
