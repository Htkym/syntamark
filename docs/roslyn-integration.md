# Roslyn AdditionalFiles と source package の canary

SM 11 の早期確認として、[sample analyzer](../samples/Syntamark.Roslyn/MarkdownTodoAnalyzer.cs) は `.md` を `AdditionalFile` として受け取り、`TODO` を含む見出しに警告を出す。これは配布用の lint ルール集ではなく、snapshot・位置・依存ロードを確かめる最小 consumer である。core に Roslyn 依存は追加していない。

## 入力と位置の契約

`AdditionalText.GetText(cancellationToken)` の同じ snapshot を、一度 `ToString()` して解析し、`snapshot.Lines.GetLinePositionSpan()` で診断位置に変換する。disk は読み直さない。読めない追加ファイルは `SMC002` とし、空文書とは区別する。scope は sample 内の固定値、source ID は caller が渡す path をそのまま使う。case や相対 path の正規化は行わない。

decoded 見出し全体の `Map` が返す raw fragment を使う。最初の fragment を主位置、残りを `additionalLocations` とし、prefix を含めて一つの範囲へ拡大しない。`Unknown` には偽の精密位置を与えない。`CoveringTokens` の警告に自動修正は付けない。構文がないことを根拠とするルールや process/global cache は追加していないため、Partial/Failed から不在を推測しない。

`SMC000` は source parser の stamp、parse status と実ロードした YamlDotNet の informational version を出す。driver の診断 properties でも assembly identity を確認する。compiler canary では YAML front matter を実際に解析するので、コンパイルだけ通って依存がロードされていない状態を成功にしない。

## 配布と host の条件

- sample は `netstandard2.0`、言語版は C# 13、Roslyn API 参照は **4.14.0** の exact pin と lock file。
- 標準の確認経路は実際の **Syntamark.Source 1.0.1** package。明示的な `SyntamarkIncludeSource=true` と package/source version の照合を使う。
- source payload は portable source を sample analyzer の assembly に一度だけ取り込む。runtime の公開 facade と friend metadata は含まれない。既存の internal entry を sample が呼び、公開 AST/API を追加していない。
- YamlDotNet は exact **18.1.0**。compiler consumer は analyzer DLL と同じ出力先の YamlDotNet DLL を `Analyzer` 項目へ明示する。Roslyn 自身の DLL を compiler に重複投入しない。
- 実 host の宣言範囲は **.NET SDK 10.0.403 の CLI build**。この環境の C# compiler は 5.9.0、driver に実ロードした Roslyn は 4.14.0。API floor、source をビルドする compiler、実 analyzer host は別の条件である。
- IDE、下位 SDK、Visual Basic、.NET Framework の compiler host は未検証。TFM だけを根拠にそれらの対応を主張しない。

## 再実行

PowerShell 7 を使う。公開済み package の canary は lock を変更せず実行する。

```powershell
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1
```

script は integration/CompilerHost を locked restore し、driver の検査後、実 `dotnet build` を二回実行する。初回は BOM・CRLF・emoji・entity・空 destination を含む Markdown、次は同じファイルを `# Done` に編集した snapshot。compiler server は無効にし、SARIF 2.1 の診断位置と古い警告の消去を確認する。結果と元 SARIF は `.local/roslyn-canary/` に置く。端末や CI を反復監視する処理はない。

未公開 candidate source package を既存の prepare/pack 手順で local feed に用意した場合は、次で同じ host を確認できる。publish はしない。

```powershell
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1 `
  -CandidateVersion <未公開候補版> -CandidateFeed <local-feed> `
  -ExpectedParserVersion <prepared-manifestのparserVersion>
```

候補の復元は `obj/candidate.packages.lock.json` を使い、公開済み 1.0.1 の lock を書き換えない。candidate mode では、空 destination が raw `[42,42)` にあることも独立期待値で検査する。prepared manifest の parser stamp を必須とし、source target の stamp と実 compiler がロードした stamp の一致を確認する。公開済み package の結果は `published-result.json` にも保存する。

## 確認できたことと残件

2026-11-06、Windows / SDK 10.0.403 で公開済み source 1.0.1 の driver と実 compiler canary が通った。sample build は警告・エラーとも 0。compiler consumer の初回は意図した `SMC000` / `SMC001` の2警告、編集後は `SMC000` の1警告で、load failure / analyzer exception はなかった。

driver は BOM を含む snapshot の raw `[24,36)`、Roslyn の zero-based `(3,2)` を確認する。実 compiler の診断は1-based の4行3列から15列。file reader が BOM を encoding marker として消費する場合も、解析と診断が同じ snapshot を使う。raw はファイルの byte offset ではない。

driver では編集・削除・rename・`.md` 対象外化・options 変更・読込不能・scan budget 停止・quote prefix をまたぐ複数位置・8並列解析・事前キャンセル・snapshot 読取時のキャンセルを確認した。`Syntamark.Compilation.SourceText` と Roslyn の `SourceText`、payload の `IsExternalInit` の一度だけの定義も確認する。

実行途中の parser の各段階でのキャンセル応答、IDE の live diagnostics lifecycle、全種類の options 変更、最低 SDK の探索は未検証。source 1.0.1 の既知の空 destination 位置は修正せず、candidate mode と元位置回帰テストで確認する。SM 10 の公開構造に依存する本格的な Roslyn sample は後続作業である。

API の根拠: [AdditionalText.GetText](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.additionaltext.gettext?view=roslyn-dotnet-4.14.0)、[RegisterAdditionalFileAction](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.analysiscontext.registeradditionalfileaction?view=roslyn-dotnet-4.14.0)、[Location.Create](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.location.create?view=roslyn-dotnet-4.14.0)。
