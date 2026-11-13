# Roslyn の AdditionalFiles から使う

[`samples/Syntamark.Roslyn`](../samples/Syntamark.Roslyn/) は `.md` を `AdditionalFile` として受け取り、`TODO` を含む見出しに警告を出す analyzer の例です。Syntamark の core は Roslyn に依存しません。

## source package と実行環境

sample は `netstandard2.0`、C# 13、Roslyn API `4.14.0` を使います。標準の実行では作業中の `Portable/**/*.cs` を直接取り込みます。候補 package の消費を検査するときは `SyntamarkCandidateMode=true`、`SyntamarkIncludeSource=true` と正確な `SyntamarkSourceVersion` を指定し、package と source のバージョンを照合します。現在の候補は `2.0.0-preview.2026111303` です。正式公開済みのバージョンとは区別してください。

source package は portable source を analyzer assembly に一度だけ取り込みます。runtime の公開 facade と friend metadata は含みません。利用側から internal entry を呼びます。native YAML の実装も同じ source に含まれ、YamlDotNet DLL の配置や参照は不要です。Roslyn 自身の DLL は重複投入しません。

検証スクリプトは CLI build と Roslyn の analyzer driver を対象にします。API の参照バージョン、source をビルドする compiler、analyzer の実行 host は別の条件です。IDE、下位 SDK、Visual Basic、.NET Framework の compiler host は未確認です。

## snapshot と診断位置

`AdditionalText.GetText(cancellationToken)` で得た同じ snapshot を一度 `ToString()` して解析し、`snapshot.Lines.GetLinePositionSpan()` で診断位置に変換します。disk は読み直しません。raw は snapshot 内の UTF-16 位置であり、ファイルの byte offset ではありません。Roslyn の `SourceText` と `Syntamark.Compilation.SourceText` は別の型です。

読めない追加ファイルは `SMC002` とし、空文書とは区別します。sample の scope は固定値、source ID は渡された path を使い、case や相対 path は正規化しません。

構文を使う経路では `commonmark/0.31.2` と `MdParser.Analyze` に必要な `MdAnalysisParts` を指定し、一回の解析から選択した facts と internal syntax を得ます。Heading を探索し、raw range に一致する見出し facts の decoded text を検査します。予算停止などで tree を取得できない場合、構文を必要とするルールを抑制します。Partial/Failed から構文の不在を推測しません。

decoded 見出しの `Map` が返す raw fragment のうち、最初を診断の主位置、残りを `additionalLocations` に使います。quote prefix などの隙間を含む単一範囲には広げません。`Unknown` に精密な位置を付けず、`CoveringTokens` の警告に自動修正は付けません。

`SMC000` は parser version、status、profile、構文・HTML node 数、YAML の状態と `native-yaml/1` を示します。sample の互換 profile による別解析は native YAML と front matter の確認用です。標準 profile の facts と syntax を別々に解析する処理ではありません。CommonMark の front matter は `Absent` とし、互換 profile の YAML の状態と区別します。

## サンプルの実行

PowerShell 7 で実行します。

```powershell
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1
```

候補 package を検査する場合は、同じ候補の manifest に記録された parser version と feed を渡します。

```powershell
$prepared = Get-Content /path/to/prepared.json -Raw | ConvertFrom-Json
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1 -CandidateVersion 2.0.0-preview.2026111303 -CandidateFeed /path/to/candidate-feed -ExpectedParserVersion $prepared.metadata.parserVersion
```

`prepared.json` と feed のパスを実際の候補の配置へ置き換えてください。スクリプトは manifest の `metadata.parserVersion` を実際に compiler が読み込んだ stamp と照合します。

snapshot の編集後に古い警告を残さず、削除・rename・`.md` 対象外化・読込不能・資源制限・キャンセルを扱う例を含みます。parser の各段階でのキャンセル応答、IDE の live diagnostics、全種類の options 変更、最低 SDK は未確認です。

API の詳細: [AdditionalText.GetText](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.additionaltext.gettext?view=roslyn-dotnet-4.14.0)、[RegisterAdditionalFileAction](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.analysiscontext.registeradditionalfileaction?view=roslyn-dotnet-4.14.0)、[Location.Create](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.location.create?view=roslyn-dotnet-4.14.0)。
