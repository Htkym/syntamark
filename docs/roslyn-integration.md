# Roslyn AdditionalFiles と source package の canary

SM 11 の [sample analyzer](../samples/Syntamark.Roslyn/MarkdownTodoAnalyzer.cs) は `.md` を `AdditionalFile` として受け取り、`TODO` を含む見出しに警告を出す。公開済み source 1.0.1 の互換確認に加え、candidate mode は CommonMark の構文探索と facts を実 compiler host で使う。これは配布用の lint ルール集ではなく、snapshot・位置・依存ロードを確かめる最小 consumer である。core に Roslyn 依存は追加していない。

## 入力と位置の契約

`AdditionalText.GetText(cancellationToken)` の同じ snapshot を、一度 `ToString()` して解析し、`snapshot.Lines.GetLinePositionSpan()` で診断位置に変換する。disk は読み直さない。読めない追加ファイルは `SMC002` とし、空文書とは区別する。scope は sample 内の固定値、source ID は caller が渡す path をそのまま使う。case や相対 path の正規化は行わない。

candidate mode は `commonmark/0.31.2` と `MdParser.ParseWithSyntax` を選び、同じ一回の解析から facts と internal syntax を得る。`MdSyntaxKind.Heading` を探索し、その raw range に一致する見出し facts の decoded text を検査する。tree が予算停止などで取得できなければ、facts だけを使う経路へ黙って切り替えず、構文を必要とするルールを抑制する。

decoded 見出し全体の `Map` が返す raw fragment を診断位置に使う。最初の fragment を主位置、残りを `additionalLocations` とし、prefix を含めて一つの範囲へ拡大しない。`Unknown` には偽の精密位置を与えない。`CoveringTokens` の警告に自動修正は付けない。構文がないことを根拠とするルールや process/global cache は追加していないため、Partial/Failed から不在を推測しない。

`SMC000` は source parser の stamp、parse status、profile、構文 node 数、HTML node 数、YAML probe の状態と実ロードした YamlDotNet の informational version を出す。driver の診断 properties でも assembly identity、root/heading raw range、profile ごとの front matter 状態を確認する。

CommonMark には front matter を混入させない。candidate mode では同じ snapshot を明示的な `lithosharp-markdown/1` でも解析し、YAML 依存を実際にロードする別の probe とする。二回の parse は異なる二つの profile を確認するためであり、標準 profile の facts と syntax を別々に解析するものではない。HTML を含む snapshot では legacy probe が投影上の制約から Partial になっても、YAML の `Parsed` と選択した標準解析の `Complete` を別々に記録する。

## 配布と host の条件

- sample は `netstandard2.0`、言語版は C# 13、Roslyn API 参照は **4.14.0** の exact pin と lock file。
- 標準の確認経路は実際の **Syntamark.Source 1.0.1** package。明示的な `SyntamarkIncludeSource=true` と package/source version の照合を使う。
- source payload は portable source を sample analyzer の assembly に一度だけ取り込む。runtime の公開 facade と friend metadata は含まれない。source entry と internal syntax を sample が直接呼ぶ。
- `SyntamarkCandidateMode=true` は `SYNTAMARK_CANDIDATE` を定義する。新しい profile/構文型を使う部分だけをコンパイル条件で選択し、公開済み 1.0.1 の確認では元の facts-only entry を使う。reflection や alias で存在しない新型を回避する構成にはしない。
- YamlDotNet は exact **18.1.0**。compiler consumer は analyzer DLL と同じ出力先の YamlDotNet DLL を `Analyzer` 項目へ明示する。Roslyn 自身の DLL を compiler に重複投入しない。
- 実 host の宣言範囲は **.NET SDK 10.0.403 の CLI build**。この環境の C# compiler は 5.9.0、driver に実ロードした Roslyn は 4.14.0。API floor、source をビルドする compiler、実 analyzer host は別の条件である。
- IDE、下位 SDK、Visual Basic、.NET Framework の compiler host は未検証。TFM だけを根拠にそれらの対応を主張しない。

## 再実行

PowerShell 7 を使う。公開済み package の canary は lock を変更せず実行する。

```powershell
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1
```

script は integration/CompilerHost を locked restore し、driver の検査後、実 `dotnet build` を二回実行する。初回は BOM・CRLF・emoji・entity・空 destination を含む Markdown、次は同じファイルを `# Done` に編集した snapshot。candidate の初回にはさらに HTML block 内の偽 TODO/link と、inline HTML 間の通常の Markdown link を含める。compiler server は無効にし、SARIF 2.1 の診断位置と古い警告の消去を確認する。結果と元 SARIF は `.local/roslyn-canary/` に置き、driver/SARIF/result を `published` と `candidate` ごとにも保存する。端末や CI を反復監視する処理はない。

未公開 candidate source package を既存の prepare/pack 手順で local feed に用意した場合は、次で同じ host を確認できる。publish はしない。

```powershell
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1 `
  -CandidateVersion <未公開候補版> -CandidateFeed <local-feed> `
  -ExpectedParserVersion <prepared-manifestのparserVersion>
```

候補の復元は `obj/candidate.packages.lock.json` を使い、公開済み 1.0.1 の lock を書き換えない。candidate mode では、空 destination が raw `[42,42)` にあること、standard の front matter が `Absent`、legacy YAML probe が `Parsed`、Document root と三つの HTML node があることも独立期待値で検査する。prepared manifest の parser stamp を必須とし、source target の stamp と実 compiler がロードした stamp の一致を確認する。

## 確認できたことと残件

2026-11-06、Windows / SDK 10.0.403 で公開済み source 1.0.1 の driver と実 compiler canary が通った。sample build は警告・エラーとも 0。compiler consumer の初回は意図した `SMC000` / `SMC001` の2警告、編集後は `SMC000` の1警告で、load failure / analyzer exception はなかった。

初期段階では commit `74ab02314e7d41ef0446ffe270d14e9062e36976` から作った未公開 source package も、同じ実 host の facts-only canary で通った。実ロードした parser stamp は `1/a6a14ab542fa607d81208f8dcb67a4243b807ac744d5492cf4e067f872170b86`。これは新しい CommonMark/構文経路の確認結果とは別の履歴である。candidate restore は XML escape した local feed と nuget.org を専用 NuGet.Config に置き、Syntamark.Source の取得を local feed に限定する。検証用番号の package を公開したことは意味しない。

driver は BOM を含む snapshot の raw `[24,36)`、Roslyn の zero-based `(3,2)` を確認する。実 compiler の診断は1-based の4行3列から15列。file reader が BOM を encoding marker として消費する場合も、解析と診断が同じ snapshot を使う。raw はファイルの byte offset ではない。

driver の検査対象は編集・削除・rename・`.md` 対象外化・options 変更・読込不能・scan budget 停止・quote prefix をまたぐ複数位置・8並列解析・事前キャンセル・snapshot 読取時のキャンセルである。candidate mode は構文経路、HTML による偽 facts の抑制、output budget での null root も確認する。`Syntamark.Compilation.SourceText` と Roslyn の `SourceText`、payload の `IsExternalInit` の一度だけの定義も確認する。

今回追加した CommonMark/構文経路は、最新 source commit から private package を用意した後に検証結果を追記する。実行途中の parser の各段階でのキャンセル応答、IDE の live diagnostics lifecycle、全種類の options 変更、最低 SDK の探索は未検証。source 1.0.1 の既知の空 destination 位置は修正せず、candidate mode と元位置回帰テストで確認する。

API の根拠: [AdditionalText.GetText](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.additionaltext.gettext?view=roslyn-dotnet-4.14.0)、[RegisterAdditionalFileAction](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.analysiscontext.registeradditionalfileaction?view=roslyn-dotnet-4.14.0)、[Location.Create](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.location.create?view=roslyn-dotnet-4.14.0)。
