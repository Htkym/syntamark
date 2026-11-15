# Syntamark 2.0 への移行

[English](migration-v2.md)

実行時パッケージと Source パッケージには同じ不変のペアを使用してください。検証済みの候補ペアは `2.0.0-preview.2026111303` です。ローカルの候補フィードに存在していても、NuGet.org に公開済みとは限りません。公開済みの `1.2.0` パッケージは変更しません。コンパイラーホスト側では従来どおり `Syntamark.Source` を明示的に導入し、独自のアダプターを介して内部の Portable API を使用します。

予定している安定ペアは `2.0.0` です。準備処理のバージョン検査はこの安定バージョンと従来のプレビューバージョンを受け付けます。ローカルの非公開検証では正式ペアを発行しません。正式な準備には、レビュー済みの最新 `main`、そのコミットに対する通常 CI の成功、未使用のバージョン、ワークフローの初回実行が必要です。正式な準備の実行が失敗した場合も、そのバージョンは使用済みになります。公開には、さらに対応するソースタグ、レビュー済みの生成処理の成功、確定済みマニフェストのハッシュ、明示的な公開承認が必要です。正式ペアを利用できるようになったら、両方のパッケージ参照と期待するパーサースタンプを一緒に切り替えてください。

## YAML の依存関係と動作

Syntamark.Source の利用だけを目的に追加した YamlDotNet の直接参照は削除してください。Syntamark のネイティブ YAML 実装は両パッケージに含まれます。YamlDotNet へのフォールバックはありません。汎用パーサーが使用する `native-yaml/1` プロファイルの対応文法と制限は、[ネイティブ YAML](native-yaml.ja.md) を参照してください。

パーサーはマッピングのエントリー順と重複キーを保持し、最後の値を自動的には選択しません。型付きの値に変換する前に、結果の状態と診断を確認してください。対応する文法の範囲外にある YAML の高度な機能は元のソースに残り、診断が返ります。ソースの保持は未対応の文書の解析に成功したことを意味しません。フロントマターのルートには引き続きマッピングが必要ですが、汎用 YAML API ではシーケンスとスカラーも使用できます。C# の命名、必須フィールド、数値のオーバーフロー、タイムスタンプ、型変換は利用側で扱います。

## Markdown のプロファイルと脚注

通常の `Parse`、`ParseWithSyntax`、`Analyze` の既定値は `lithosharp-markdown/1` のままです。`commonmark/0.31.2` の意味も変わりません。脚注とディレクティブの属性を使用する場合は、`syntamark-docs/2` を明示的に選択してください。キャッシュの識別情報には、プロファイル、パーサーのバージョン、実効オプション、要求した解析項目を含めてください。

```csharp
using System;
using Syntamark;

var notes = MarkdownParser.AnalyzeFootnotes("Text[^source].\n\n[^source]: Source text.\n",
    "docs", "guide.md");
foreach (var reference in notes.References)
    Console.WriteLine($"{reference.Label}: {reference.Number}");
```

`AnalyzeFootnotes` の既定値は `syntamark-docs/2` です。別のプロファイルを渡すと、この拡張機能は無効になります。`Analysis` には、同じスナップショットから得た通常の解析情報と診断が含まれます。脚注の定義は `Label`、`NormalizedLabel`、`RawSpan`、`LabelRawSpan` と、各ブロックに対応する個別の `ContentRawSpans` を保持します。表示番号の `Number` は最初の参照順で決まり、未使用の定義や未解決の参照では null になります。同じ脚注への繰り返し参照には 1 から始まる `Occurrence` が付き、レンダラーが異なる戻りリンクを作成できます。重複した定義も `IsDuplicate` と `LMD030` によって確認でき、参照の解決には最初の定義を使用します。未解決の参照も残り、`LMD031` が返ります。ラベルと表示番号は別の識別子です。

脚注の定義には、字下げした継続行と空行で区切ったブロックを使用できます。エスケープしたマーカーとコード内のマーカーは文字列として扱います。Syntamark は構文と解析情報を提供し、脚注の HTML、ソース ID の意味、ルーティングはレンダラー側で扱います。情報が存在しないと判断する前に、`Analysis.Status` と各項目の取得状態を確認してください。

## 編集位置

`RawSpan` は従来どおり、マーカーを含む UTF-16 の位置を表します。追加された[構文プロパティ](syntax-design.md) からは、内容の範囲、参照ラベル、表のセル、ディレクティブの属性を取得できます。表示文字列から編集範囲を推測せず、各位置が null かどうかと対応の精度を確認してください。全体を囲む範囲には構文記号や除外した部分が含まれる場合があり、その範囲を安全に置換できるとは限りません。

### スナップショットとセグメント

`SourceMap` はファイルにアクセスせず、複数のスナップショットや入力にまたがる位置対応を合成します。`SourceSnapshot(sourceId, version, text)` は不変で、`SourceId`、`Version`、`Text`、`TextHash` を公開します。スナップショットはソース ID、バージョン、テキストハッシュの組み合わせで識別するため、異なるオブジェクトでも同じスナップショットを表せます。ID とバージョンは呼び出し側が指定します。ソース ID を渡しても API はファイルを開きません。`SourceRange` は特定のスナップショットと UTF-16 の `RawSpan` を組み合わせたもので、`RawSpan.Start` と `RawSpan.Length` が半開区間を表します。

`new SourceMap(outputSnapshot, segments)` でマップを作成します。各 `SourceMapSegment` はその出力スナップショット内の空でない範囲を表します。セグメントは出力順に並べ、範囲内に収め、互いに重ならないように指定してください。セグメント間に隙間を残すこともでき、その部分の対応は不明になります。

| `MarkdownSegmentKind` | 原文と対応関係 |
| --- | --- |
| `Linear` | 同じ長さで、UTF-16 のテキストも一致する `SourceRange` が必要です。出力範囲の一部を問い合わせると、対応する原文範囲の一部が返ります。 |
| `Atomic` | `&amp;` を `&` に復号する場合など、原文トークン全体の `SourceRange` が必要です。空でない出力範囲の一部を問い合わせても原文トークン全体が返り、文字単位の厳密な対応にはなりません。 |
| `Unknown` | 原文は null にします。生成したテキストや原文が分からないテキストに使用します。 |

### 対応の合成と編集精度

`downstream.Compose(upstreamMaps)` は新しいマップを返します。下流のマップは変換後の出力を表し、上流のマップの `Output` は対応元が示す中間スナップショットと一致する必要があります。合成では渡したすべてのマップと、それらがすでに保持している上流のマップを複数の入力ソースにまたがって順にたどります。マップ同士の接続にはソース ID だけでなく、バージョンとテキストハッシュの一致も必要です。

`Map(outputSpan)` は `Origins` を出力順に返し、`Precision` と `CanEdit` も返します。同じスナップショットの隣接範囲は、前の範囲の終端と次の範囲の開始が一致する場合に結合されます。繰り返し使った範囲、連続していない範囲、順序を入れ替えた範囲、異なるソースの範囲は個別に残り、全体を囲む単一の範囲には置き換わりません。

| `MarkdownMappingPrecision` | 意味 |
| --- | --- |
| `Exact` | 問い合わせたすべての部分が `Linear` の原文範囲に対応します。結果に複数の原文範囲が含まれる場合もあります。 |
| `CoveringTokens` | すべての部分に原文があり、少なくとも一部が `Atomic` のトークン対応を経由します。 |
| `Partial` | 原文が分かる部分と、対応が不明または曖昧な部分があります。 |
| `Unknown` | 問い合わせた範囲に対して、原文範囲を返せません。 |

`CanEdit` が true になるのは、`Exact` で原文範囲が一つだけあり、範囲の両端が UTF-16 のサロゲートペアを分断しない場合だけです。このため、二つの入力にまたがる `Exact` の結果を一度に置換することはできません。異なる原文の接続点にある長さ 0 の範囲は曖昧なため、編集できません。問い合わせに生成文字や隙間が含まれる場合も、厳密な対応にはなりません。

スナップショットの識別情報をたどって循環した場合、その経路の対応は不明になります。循環だけの問い合わせは `Unknown` を返し、別の経路から得られる原文があれば `Partial` になる場合があります。同じスナップショットに異なるマップがある場合は、一つを選ばずエラーにします。上流の出力の重複は `Compose` が拒否し、さらに上流をたどって見つかった競合は `Map` が拒否します。

### マップの合成と編集前の検査

各 C# サンプルは選択した `Syntamark` 実行時パッケージを参照するコンソールプロジェクトで実行できます。次の例は `guide.md` から `Hello` を取り込み、生成した接頭辞を加えた後、表示中の単語から二段階の対応をたどって原文を求めます。更新後の文字列をメモリ上で作成し、ファイルには書き込みません。

```csharp
using System;
using Syntamark;

var original = new SourceSnapshot("guide.md", "1", "Hello");
var included = new SourceSnapshot("included.md", "1", "Hello");
var output = new SourceSnapshot("view.txt", "1", "> Hello");

var includeMap = new SourceMap(included, new[]
{
    new SourceMapSegment(new RawSpan(0, 5),
        new SourceRange(original, new RawSpan(0, 5)), MarkdownSegmentKind.Linear),
});
var composed = new SourceMap(output, new[]
{
    new SourceMapSegment(new RawSpan(0, 2), null, MarkdownSegmentKind.Unknown),
    new SourceMapSegment(new RawSpan(2, 5),
        new SourceRange(included, new RawSpan(0, 5)), MarkdownSegmentKind.Linear),
}).Compose(new[] { includeMap });

var mapped = composed.Map(new RawSpan(2, 5));
if (!mapped.CanEdit)
    throw new InvalidOperationException("The mapped range cannot be edited directly.");

var edit = new SourceEdit(mapped.Origins[0], "Hello", "Hi");
var current = new SourceSnapshot("guide.md", "1", "Hello");
if (!edit.IsApplicable(current))
    throw new InvalidOperationException("The source changed; analyze it again.");

var span = edit.Target.Span;
var revised = current.Text.Remove(span.Start, span.Length).Insert(span.Start, edit.Replacement);
Console.WriteLine(revised); // Hi
Console.WriteLine(composed.Map(new RawSpan(0, 7)).Precision); // Partial
Console.WriteLine(edit.IsApplicable(new SourceSnapshot("guide.md", "2", "Hello"))); // False
```

`SourceEdit(target, expectedText, replacement)` は、`expectedText` が対象スナップショットの指定範囲にあるテキストと完全に一致するかを検査します。サロゲートペアを分断する範囲と、不正な Unicode を含む置換文字列も拒否します。編集案の作成時にはマップの精度を検査しないため、対応元の範囲を一度に置換する前に `mapped.CanEdit` を確認してください。

編集案を適用する前に、現在のソーススナップショットを渡して `IsApplicable(currentSnapshot)` を呼び出してください。ソース ID、バージョン、テキストハッシュ、範囲、期待する元のテキストを検査します。テキストが同じでもバージョンが変われば false になり、バージョンを再使用してもテキストが変われば false になります。Syntamark は現在のファイルの読み取り、ロック、書き込みを行いません。現在のスナップショットの取得と、検査から適用までの変更を防ぐトランザクションは利用側で扱います。

比較として、エンティティの復号では原文トークン全体の範囲が返り、直接編集はできません。

```csharp
using System;
using Syntamark;

var encoded = new SourceSnapshot("entity.md", "1", "&amp;");
var decoded = new SourceSnapshot("decoded.txt", "1", "&");
var map = new SourceMap(decoded, new[]
{
    new SourceMapSegment(new RawSpan(0, 1),
        new SourceRange(encoded, new RawSpan(0, 5)), MarkdownSegmentKind.Atomic),
});
var mapped = map.Map(new RawSpan(0, 1));
Console.WriteLine(mapped.Precision); // CoveringTokens
Console.WriteLine(mapped.CanEdit); // False
```

## 編集後のスナップショットと文章校正

段落の再利用を明示的に有効にする場合は、[`CreateSession` と `ApplyEdits`](selective-analysis.md#analyze-edited-snapshots) を使用してください。既存の解析メソッドの動作は変わりません。セッションは新しいスナップショットの位置を返し、全文解析に切り替えた理由を示します。永続的な構文ノード ID は保証しません。論理的なリソースカウンターの値だけでは、処理時間が改善したとは判断できません。

必要な解析情報と未対応構文の候補を選択して取得するエディターでは、`CreateAnalysisSession` を使用してください。専用の `MarkdownAnalysisSession` は文書全体を表す API の代わりに、各項目の取得状態を確認できる `Analysis` を公開します。Portable API の `MdParseSession.CreateForAnalysis` が返す結果には、同じ解析で得られた従来の描画用 `Blocks` も保持されます。要求した解析項目は差分解析でも全文解析でも保持されます。`UnsupportedSyntax` は明示的に要求し、文書全体のスナップショットと変換後の本文のスナップショットは区別してください。先行する `2.0.0-preview.20261113` ペアは元のセッション API を保持し、この実装で置き換えません。

[文章校正のサンプル](prose-consumer.md) では、呼び出し側が指定した Yomiyasu lint CLI を明示的に起動できます。選択したテキスト区間を送信し、行単位の診断を元のソース範囲に対応付けます。自動編集は行いません。Python と文章校正は利用側の任意の依存関係です。部分的な解析情報、行だけを示す lint の位置、原文との対応が不明な範囲を、厳密な編集範囲として扱わないでください。
