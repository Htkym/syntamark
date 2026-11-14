# 公開機能

[English](public-features.md) | 日本語

Syntamark の公開機能と、対応する英語・日本語の説明を示します。リンク先のガイドには API の詳細と例があり、この文書では共通の設定、診断、制約、移行方法を説明します。Syntamark は渡されたテキストを解析します。HTML の描画、ファイルの読み取り、MDX の実行、C# のコンテンツスキーマへのバインド、サイトのルーティングは利用側で行います。

## 機能と英日説明の対応

| 機能 | 公開 API | English | 日本語 |
|---|---|---|---|
| runtime と compiler host 向け Source | `MarkdownParser`、portable `MdParser`、Source build properties | [Packages](public-features.md#packages) | [配布](#packages) |
| Markdown の文法と profile | `MarkdownParseOptions.ProfileId`、`Capabilities` | [Profiles](public-features.md#profiles) | [profile](#profiles) |
| 全 facts、section、link、text | `Parse`、`MarkdownDocument` | [Facts and positions](public-features.md#facts) | [facts と位置](#facts) |
| デコード済みの投影と行位置 | `MarkdownTextProjection.Map`、`GetLinePosition` | [Facts and positions](public-features.md#facts) | [facts と位置](#facts) |
| 選択 facts と未対応構文の候補 | `Analyze`、`MarkdownAnalysisParts`、`GetAvailability` | [Analysis](public-features.md#analysis) | [選択解析](#analysis) |
| header scan、front matter、Body 入力 | `ScanFrontMatter`、`MarkdownInputKind`、recovery options | [Document inputs](public-features.md#inputs) | [文書入力](#inputs) |
| 構文、内容・ラベルの位置、table cell | `ParseWithSyntax`、`MarkdownSyntaxNode` | [Syntax](syntax-design.en.md) | [構文](syntax-design.md) |
| ディレクティブの属性と診断 | `MarkdownDirectiveSyntax`、`LMD020`〜`LMD024` | [Directives](syntax-design.en.md) | [ディレクティブ](syntax-design.md) |
| 脚注の定義、参照、番号 | `AnalyzeFootnotes`、`MarkdownFootnoteDocument` | [Footnotes](migration-v2.md#markdown-profiles-and-footnotes) | [脚注](migration-v2.ja.md) |
| 複数段階の位置対応と修正の保護 | `SourceSnapshot`、`SourceMap`、`SourceEdit` | [Source maps](migration-v2.md#editing-positions) | [位置対応](migration-v2.ja.md) |
| 全情報・選択情報を保持する不変の編集セッション | `CreateSession`、`CreateAnalysisSession`、`ApplyEdits` | [Editing](public-features.md#editing) | [編集](#editing) |
| YAML の構文、scalar の意味、bytes、front matter | `YamlParser`、`YamlParseOptions`、`GetYamlSyntax` | [YAML](native-yaml.md) | [YAML](native-yaml.ja.md) |
| 本文の選択と任意の Yomiyasu 連携 | `samples/Syntamark.Prose` | [Prose](public-features.md#prose) | [校正連携](#prose) |
| 処理予算、キャンセル、結果の識別 | parse options、`Status`、`Coverage`、hash | [Resources](public-features.md#resources) | [資源制限](#resources) |
| 再現可能な性能測定 | benchmark executable、session counters | [Measurement](public-features.md#measurement) | [測定](#measurement) |

<a id="packages"></a>
## runtime と compiler host 向け Source

導入例は [README](../README.md) を参照してください。両 package は同じ Markdown・YAML 実装を使い、内容を固定した同じバージョンの組として提供します。runtime package は .NET 10 で `Syntamark` を公開します。Source は `SyntamarkIncludeSource=true` の netstandard2.0/C# 13 の利用側へ internal な portable `Syntamark.Compilation` 型を追加します。`SyntamarkSourceVersion` は package reference と同じ正確なバージョンへ固定してください。Source には runtime の公開 facade と friend assembly 宣言は含みません。adapter と host への接続は利用側で行い、runtime の公開型名を Source の entry point として使うことはできません。

[Roslyn サンプル](../samples/Syntamark.Roslyn/) は cancellation token を使って一つの `AdditionalText` snapshot を読み、その同じ snapshot で診断位置を求めます。対象の Roslyn API は 4.14.0 で、API の参照バージョン、source をビルドする compiler、analyzer を実行する host は別の条件です。読み取り不能、編集、削除、rename の例を含みます。サンプルの `SMC000` は parser の状態、`SMC001` は TODO を含む見出し、`SMC002` は読み取り不能を示します。IDE、Visual Basic、.NET Framework の compiler host、最低 SDK の互換性を保証するものではありません。Syntamark 本体は Roslyn に依存しません。組や expected parser stamp を更新する前に[移行ガイド](migration-v2.ja.md)を参照してください。

<a id="profiles"></a>
## Markdown の文法と profile

既定値は互換 profile の `lithosharp-markdown/1` です。既存の front matter、table、task list、strikethrough、裸 URL、数式、directive/admonition、HTML・MDX の不透明領域の扱いを含みます。`commonmark/0.31.2` は明示的に選択し、YAML らしい入力も Markdown として扱い、これらの拡張を有効にしません。HTML は block/token として認識しますが、描画や DOM の構築は行いません。見出しの anchor は Syntamark の slug policy に従い、CommonMark の規定ではありません。`syntamark-docs/2` は互換 profile に脚注とディレクティブ属性を追加します。定義リスト、汎用属性、grid table は未対応です。

```csharp
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var document = MarkdownParser.Parse("# Guide\n", "docs", "guide.md", options: options);
Console.WriteLine(document.ProfileId);
```

`AnalyzeFootnotes` だけは `syntamark-docs/2` を既定値とし、通常の parse・analysis・session は互換 profile を既定値とします。profile の名前から完全対応を推測せず、選んだ profile の `Capabilities`、診断、coverage を確認してください。[Conformance runner](../tests/Syntamark.Conformance/README.md) はバージョンを固定した CommonMark の公式例から対応表を作成します。既知の例に通ることはすべての入力への保証にはなりません。未対応の構文が通常のテキストとして残る場合があります。

<a id="facts"></a>
## facts、デコード済みの投影、位置

`Parse` は front matter、heading、section、link、fence、text region を持つ `MarkdownDocument` を返します。heading は `RawLevel`、`LocalKey`、`Anchor`、text の投影を返します。section は `ParentLocalKey`、`HeadingLocalKey`、`DirectBodySpan`、`SubtreeSpan` を返します。link は inline・autolink・reference use・reference definition と `Resolution` を区別します。`Image` は画像の構文を表し、`TargetRawSpan` が use 自体の外にある定義のテキストを指す場合があります。fence は marker・info・content・closing の位置と `Closed` を返し、text region は投影の種類を保持します。これらは構文の情報で、route や外部参照の検証結果ではありません。

local key は結果内の facts を識別し、編集をまたぐ永続的な ID ではありません。section の直接の本文はその見出しと子 section を除き、subtree はそれらを含みます。未解決の参照には解決済みの target がありません。null を空の URL とせず、`Resolution` と nullable な `Target` を確認してください。

`RawSpan` は BOM と元の改行を含む入力文字列の、0始まりの UTF-16 半開区間です。CRLF と補助平面の Unicode 文字は、それぞれ2単位を使います。`GetLinePosition` は1始まりの行・列を返し、CRLF 内の LF の offset は次の行の1列目へ対応します。位置が null であることは不明または非適用を表し、位置が既知の0幅の範囲とは異なります。元 snapshot に位置を適用する前に header を除いたり、改行を正規化したりしないでください。

```csharp
var document = MarkdownParser.Parse("# A &amp; B\n", "docs", "guide.md");
var heading = document.Headings[0];
var mapping = heading.Text.Map(new RawSpan(2, 1));
Console.WriteLine(mapping.Precision);
foreach (var fragment in mapping.RawFragments)
    Console.WriteLine(fragment);
```

`MarkdownTextProjection.Text` はデコード済みのテキストで、`SourceSegments` は `Linear`、`Atomic`、`Unknown` の対応を保持します。`Map` はデコード後の範囲を受け取り、元範囲の順序付き一覧と、`Exact`、`CoveringTokens`、`Partial`、`Unknown` の精度を返します。entity や escape は token 全体へ対応し、除外された記号によって元範囲が分かれる場合があります。複数の範囲を保ち、それらを囲む一つの範囲を置換しないでください。解析の完了、投影の網羅範囲、安全に修正できる精度は別の性質です。複数 snapshot をまたぐ変換と古い修正の拒否は、[位置対応と修正](migration-v2.ja.md)を参照してください。

<a id="analysis"></a>
## 選択 facts と未対応構文の候補

`Analyze` は要求した情報だけを公開します。基本の part は `FrontMatter`、`Headings`、`Sections`、`Links`、`Fences`、`TextRegions`、`Syntax` です。`AllFacts=63` と `All=127` の意味は維持し、どちらも `UnsupportedSyntax=128` を含みません。`None` でも必須の解析と検証を行います。出力を減らしても、入力検査、front matter の検証、参照の解決、キャンセル、処理予算は無効になりません。

```csharp
var analysis = MarkdownParser.Analyze("# Guide\n\n[API](api.md)\n",
    MarkdownAnalysisParts.Headings | MarkdownAnalysisParts.Links,
    "docs", "guide.md");
if (analysis.GetAvailability(MarkdownAnalysisParts.Links) == MarkdownAnalysisAvailability.Complete)
    Console.WriteLine(analysis.Links!.Count);
```

`GetAvailability` には基本の part を一つ指定します。`NotRequested`、`Complete`、`Partial`、`Unavailable` を区別し、未要求・取得不能の property は null です。profile の対応範囲内で不在を確認できるのは、完全な出力が空の場合です。資源制限によって構文が取得不能でも、ほかの選択 facts が部分的に残る場合があります。`UnsupportedSyntax` は通常のテキストに残った脚注、定義リスト、汎用属性、grid table らしい候補を明示的に要求します。描画の拡張や製品固有の診断ではありません。docs profile で有効な脚注は未対応候補にせず、code・math・HTML token・link destination は既存の解析を使って除外します。

portable adapter の `MdParser.AnalyzeWithBlocks` は `Syntax` を要求せず、同じ解析から従来の描画用 block を保持します。internal な block の位置は body 相対で、公開 facts・候補は入力全体の相対位置です。block は後段の投影制限で残る場合がありますが、block 解析が完了しなければ null です。第二の公開 AST ではありません。[選択解析](selective-analysis.md)には追加の英語の例があります。

<a id="inputs"></a>
## front matter、header scan、Body 入力

Document 入力は選んだ profile に従って front matter を認識します。`FrontMatter.State` の `Absent`、`Parsed`、`Empty`、`Invalid`、`Unterminated`、`Unknown` を区別してください。不正または部分的な YAML は、有効な型付きコンテンツではありません。native syntax と互換 node の座標の違いは、[YAML の front matter](native-yaml.ja.md)を参照してください。

```csharp
var header = MarkdownParser.ScanFrontMatter("---\ntitle: Guide\n---\n# Content\n");
Console.WriteLine(header.State);
```

`ScanFrontMatter` は `frontmatter-scan/1` の契約で区切りと header の UTF-16 だけを検査し、nullable な marker・YAML・body・failure の範囲と診断を返します。`Closed`・`Complete` は YAML や本文の妥当性を示しません。入力と scan の予算は header の接頭部分へ適用するため、閉じた header の結果は後続本文の長さや構文に依存しません。予算停止を未終端の確定として扱わず、未終端が確定した場合は `LMD004` と EOF の failure span を返します。scanner が予期する次の `FailureLine` は物理的な EOF の行を超える場合があります。この entry は Body mode の options を拒否します。

`new MarkdownParseOptions(MarkdownInputKind.Body)` は変換済み本文の envelope 認識を無効にし、BOM も含めて `BodySpan=(0, raw.Length)` とします。一方、`new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody)` は未終端の文書 envelope を本文として回復し、`Unterminated`、`Partial`、`LMD004` を残します。既定は厳格な処理です。CommonMark と Body 入力には recovery を適用しません。元の文書と変換済み本文の識別は分けてください。

<a id="editing"></a>
## 不変の編集セッション

`CreateSession` は全 facts と構文のセッションを返します。`CreateAnalysisSession` は `Analysis` を持ち、全情報の `Document` facade を持たない別の選択解析セッションを返します。最初の part の選択は再利用と full fallback のどちらでも維持します。`ApplyEdits` は、前の入力全体の同じ snapshot に対する text hash と、開始順・重複なし・開始位置が異なる UTF-16 編集を受け取ります。範囲外、null の置換文字列、hash の不一致は拒否します。options が null なら前の options を保持し、キャンセルや失敗でも前のセッションは変わりません。LSP の連続した buffer 変更は一つの snapshot の位置へ正規化するか、順に適用してください。

```csharp
var previous = MarkdownParser.CreateAnalysisSession("Alpha\n\nBeta\n\nGamma\n",
    MarkdownAnalysisParts.UnsupportedSyntax, "docs", "guide.md", sourceVersion: "1");
var next = previous.ApplyEdits(previous.Analysis.TextHash,
    new[] { new MarkdownTextEdit(new RawSpan(7, 4), "Beta {#section}") }, sourceVersion: "2");
Console.WriteLine(next.Analysis.GetAvailability(MarkdownAnalysisParts.UnsupportedSyntax));
```

`Mode` と `Fallback` は `Delta` または正規の `Full` 解析になった理由を示します。再利用の対象は境界が確定した単一行の通常 paragraph で、参照の解決、ほかの block、結果の公開、hash 計算は実行します。options の変更、不完全な前の結果、未対応の編集、資源制限の境界では full 解析へ戻る場合があります。結果の位置は新 snapshot に対応し、編集をまたぐ node の同一性は保証しません。`ReusedBlocks`、`RebasedBlocks`、`ParsedBlocks`、`ScanUnits`、`OutputItems` は再利用や論理予算の counter で、時間や割り当て量ではありません。portable の `MdParseSession.CreateForAnalysis` は前述の internal な body 相対 block も保持します。[編集例と fallback の理由](selective-analysis.md#analyze-edited-snapshots)には追加の英語説明があります。

<a id="prose"></a>
## 本文の選択と任意の校正連携

[本文サンプル](../samples/Syntamark.Prose/) は一つの構文・facts snapshot に対する利用側の方針を示します。heading・paragraph のテキスト、互換 profile の table cell、link label を選びます。code、image/alt、autolink、HTML token/block、math、認識済み front matter、reference definition は理由付きで除外し、inline HTML tag 間の本文は残します。CommonMark の裸 URL はテキストとして残るため、利用側で保護してください。0幅の除外も含め、除外部分をまたいで segment を結合しません。対応不明の位置ではテキストを推測せず、選択を止めます。

```powershell
dotnet run --project samples/Syntamark.Prose/Syntamark.Prose.csproj -c Release -- --yomiyasu python /path/to/yomiyasu_lint.py guide.md
```

Python と lint script は利用側で用意します。通常の解析やサンプル実行では校正プロセスを起動しません。明示的に実行すると、選択したデコード済み segment を `--json --no-words -` で個別に送ります。文書全体の規則やプロジェクトの用語一覧は自動で適用しません。この bridge はサンプルで、自然言語の本文を一般的に定義する API や自動修正 API ではありません。

Yomiyasu は先頭 BOM を1個除去し、CRLF/CR を LF へ正規化し、Python code point の位置を使います。Syntamark は元の UTF-16 を使います。サンプルの `YomiCoordinates` は境界を記録し、surrogate pair・CRLF の内部や除いた BOM への逆変換を拒否します。行だけが分かる lint JSON の診断は、最大でも `CoveringTokens` の精度で元範囲の順序付き一覧へ戻し、正確な自動修正にはしません。呼び出しは30秒で中止し、非0終了、未対応の JSON、不正な行番号は失敗とします。文章の意味の確認、古いバージョンの拒否、修正の適用は利用側で行います。[選択方針と座標の例](prose-consumer.md)、[修正の保護](migration-v2.ja.md)も参照してください。

<a id="resources"></a>
## 処理予算、キャンセル、結果の識別

`MarkdownParseOptions` の既定値は `MaxInputUtf16=1048576`、`MaxOutputItems=131072`、`MaxNestingDepth=200`、`MaxScanUnits=16777216` で、すべて正の値です。入力拒否は `Failed`、開始後の処理予算停止は資源制限診断と不完全な coverage を伴う `Partial` です。公開済みの facts は残る場合があります。`LMD002` は入力拒否、`LMD003` は資源制限による停止を示します。各出力の availability と nullable な位置を確認してください。`Coverage` はすべての構文位置を保証しません。Markdown のキャンセルは `OperationCanceledException`、不正な options や範囲は引数例外です。汎用 YAML は段階によってキャンセルの契約が異なり、[Native YAML](native-yaml.ja.md)に6個の予算を説明しています。

| 診断 | 意味 |
|---|---|
| `LMD001` | 検査した入力内に対応しない UTF-16 surrogate がある |
| `LMD002` | 入力制限による拒否 |
| `LMD003` | work・output・depth の資源制限による停止 |
| `LMD004` | 未終端が確定した front matter |
| `LMD005` | mapping 以外の root を含む、不正または未対応の front matter YAML |
| `LMD006` | decoded から raw への対応が不完全であることを示す情報診断 |

ディレクティブの `LMD020`〜`LMD024` は[構文ガイド](syntax-design.md)、脚注の `LMD030`・`LMD031` は[移行ガイド](migration-v2.ja.md)で説明しています。汎用 YAML の診断はこれらの Markdown の ID ではなく `Kind` と `Reason` を使います。

結果は scope・source・version、text hash、parser stamp、Markdown/YAML profile、実効 options を識別します。全 facts の契約は `2.0`、選択解析は `analysis/2`、analysis schema は1です。header-only scan は別の契約です。cache は入力 snapshot、parser・契約・profile・options、要求した選択情報の `AnalysisHash` で識別し、`OptionsHash` だけで共用しないでください。Body mode と recovery は実効的な識別を変える場合があります。native YAML 契約への移行時は1.x の結果を無効にしてください。部分的、不明、未要求、取得不能は、対象がないという成功結果ではありません。[portable の安全性検査](../tests/Syntamark.Safety/)は製品への依存を増やさず、制限とキャンセルを検査します。

<a id="measurement"></a>
## 測定と移行

[benchmark project](../benchmarks/)は条件を明示した入力で解析、構文、投影の位置対応、行位置を測定します。profile、出力、package、process 設定、baseline を揃えて比較し、経過時間、割り当て量、保持メモリを別々に測定してください。session の論理 counter は高速化の証拠にはならず、新しい YAML・構文の処理と既存処理のリグレッションは区別します。[benchmark の実行方法](../benchmarks/Syntamark.Benchmarks/README.md)と[測定条件](performance.md)を参照してください。

major 更新の依存、profile、脚注、位置対応、編集の変更は[英日対応の移行ガイド](migration-v2.ja.md)を参照してください。package version、parser stamp、profile identifier、contract version、options schema は別の識別子です。private な候補や stable の検証は正式公開を意味しません。正式に発行された組へ切り替えるときは runtime/Source の参照と expected stamp を同時に更新してください。
