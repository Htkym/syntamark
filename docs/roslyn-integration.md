# Roslyn の AdditionalFiles から使う

[`samples/Syntamark.Roslyn`](../samples/Syntamark.Roslyn/) は `.md` を `AdditionalFile` として受け取り、`TODO` を含む見出しに警告を出す analyzer の例です。Syntamark の core は Roslyn に依存しません。

## source package と実行環境

sample は `netstandard2.0`、C# 13、Roslyn API **4.14.0**、YamlDotNet **18.1.0** を使います。依存バージョンは固定しています。標準の sample は公開済み **Syntamark.Source 1.0.1** を使い、`SyntamarkIncludeSource=true` を明示して package と source の version を照合します。`SyntamarkSourceVersion` は使用する package のバージョンに合わせます。

source package は portable source を analyzer assembly に一度だけ取り込みます。runtime の公開 facade と friend metadata は含みません。利用側から internal entry を呼びます。YamlDotNet DLL は analyzer DLL と同じ出力先に置き、compiler consumer の `Analyzer` 項目へ明示してください。Roslyn 自身の DLL は重複投入しません。

`SyntamarkCandidateMode=true` は `SYNTAMARK_CANDIDATE` を定義し、新しい CommonMark profile と internal syntax を使う部分を選びます。公開済み 1.0.1 では facts-only entry を使います。新しい profile・構文型を使う場合は、それらを含む source package を指定してください。

確認済みの実行環境は **.NET SDK 10.0.403 の CLI build** です。その compiler は5.9.0、driver の Roslyn は4.14.0です。API の参照バージョン、source をビルドする compiler、analyzer の実行 host は別の条件です。IDE、下位 SDK、Visual Basic、.NET Framework の compiler host は未確認です。

## snapshot と診断位置

`AdditionalText.GetText(cancellationToken)` で得た同じ snapshot を一度 `ToString()` して解析し、`snapshot.Lines.GetLinePositionSpan()` で診断位置に変換します。disk は読み直しません。raw は snapshot 内の UTF-16 位置であり、ファイルの byte offset ではありません。Roslyn の `SourceText` と `Syntamark.Compilation.SourceText` は別の型です。

読めない追加ファイルは `SMC002` とし、空文書とは区別します。sample の scope は固定値、source ID は渡された path を使い、case や相対 path は正規化しません。

構文を使う経路では `commonmark/0.31.2` と `MdParser.ParseWithSyntax` により、一回の解析から facts と internal syntax を得ます。Heading を探索し、raw range に一致する見出し facts の decoded text を検査します。予算停止などで tree を取得できない場合、構文を必要とするルールを抑制します。Partial/Failed から構文の不在を推測しません。

decoded 見出しの `Map` が返す raw fragment のうち、最初を診断の主位置、残りを `additionalLocations` に使います。quote prefix などの隙間を含む単一範囲には広げません。`Unknown` に精密な位置を付けず、`CoveringTokens` の警告に自動修正は付けません。

`SMC000` は parser version、status、profile、構文・HTML node 数、YAML の状態とロードした YamlDotNet の version を示します。sample の互換 profile による別解析は YAML 依存の確認用です。標準 profile の facts と syntax を別々に解析する処理ではありません。CommonMark の front matter は `Absent` とし、互換 profile の YAML の状態と区別します。

## サンプルの実行

PowerShell 7 で実行します。

```powershell
pwsh -File tests/Syntamark.Roslyn.Integration/Verify.ps1
```

snapshot の編集後に古い警告を残さず、削除・rename・`.md` 対象外化・読込不能・資源制限・キャンセルを扱う例を含みます。parser の各段階でのキャンセル応答、IDE の live diagnostics、全種類の options 変更、最低 SDK は未確認です。公開済み 1.0.1 の空 destination の位置はそのバージョンの挙動に従います。

API の詳細: [AdditionalText.GetText](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.additionaltext.gettext?view=roslyn-dotnet-4.14.0)、[RegisterAdditionalFileAction](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.analysiscontext.registeradditionalfileaction?view=roslyn-dotnet-4.14.0)、[Location.Create](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.location.create?view=roslyn-dotnet-4.14.0)。
