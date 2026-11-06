# Markdown profile の分離

`lithosharp-markdown/1` を既定の互換プロファイルとして保持し、`commonmark/0.31.2` を明示選択できるようにした。既定値の変更や、既存 consumer の自動移行は行わない。

```csharp
var options = new MarkdownParseOptions(profileId: "commonmark/0.31.2");
var document = MarkdownParser.Parse(raw, "scope", "document.md", options: options);
Console.WriteLine(document.ProfileId);
```

facts と facade は解析時に選んだ `ProfileId` を保持する。`ContractVersion` はいずれも `1.0`。facts の型・UTF-16 range・mapping の基本契約を維持しているため、profile の追加だけで contract/schema の版を上げない。options schema は `1` だけを受理し、未知の profile/schema と不正な予算値は明示的に拒否する。CommonMark の仕様版、package version、parser source stamp は別の識別子である。

options hash の canonical JSON は実際の profile と展開済みの六つの値を含む。旧既定値の JSON bytes と SHA-256 `970d603ab09c239dde63511f42543a76b80b8897a323e335a4cf57685d1c8591` は変えていない。同じ raw 入力でも選択した profile が異なれば options hash が異なる。

CommonMark profile では front matter を暗黙に取り除かず、入力全体を body とする。冒頭の `---` も Markdown の文法で解釈する。BOM や改行を削除・正規化して raw offset を変えない。table、task marker、strike、裸 URL、数式、directive、MDX の特別扱いは無効にする。Markdown として成立する部分の通常解釈は残る。heading slug は既存の派生 policy であり、CommonMark が規定する識別子ではない。

CommonMark の HTML は native な block と inline token として認識する。block の内部から Markdown heading/link を取り出さず、inline のタグ間にある通常の Markdown は解析する。HTML 属性を link facts に混ぜない。raw HTML は本文へ展開せず、`rawHtmlBlock` / `rawHtmlInline` の unprojected region に元 range を示す。MDX の ESM や JSX expression をこの profile で不透明領域として特別扱いしない。HTML renderer、DOM、実行機能は追加しない。

新しい profile の `Status` は解析の終了状態を表す。認識済みの HTML を自然文へ投影しないことや、decoded mapping が不明であることだけでは `Partial` にしない。これらは `Coverage` の `DecodedMapping: Partial`、unprojected regions、理由、および情報診断で表す。scan/output/depth の予算停止は `Partial`、入力拒否は `Failed`、キャンセルは例外である。既存 profile の status と coverage の意味は維持する。

`markdownSyntax` capability は新しい profile では `Partial` としている。[版固定した公式例の対応表](commonmark-support.md)と残差を別途確認する。終了状態の `Complete` と、全 CommonMark 構文への準拠を同一視しない。profile を選択した事実も完全準拠の証拠にはしない。

public の `MarkdownParseOptions` constructor と `MarkdownParser.Parse`、`MarkdownDocument` の既存 getter の signature/default は保持した。portable の既存 `MdDocumentFacts` constructor も残し、新しい metadata を受け取る required-argument overload へ委譲する。既存 friend assembly の caller が要求する constructor を削除すると、再コンパイルしない caller に `MissingMethodException` が発生し得るためである。旧経路は従来の profile/contract を補い、parser は新 overload に実際の値を渡す。

runtime と source は同じ options/profile を扱う。artifact の `profileId` は互換性のため既定 profile の metadata として保持し、利用可能な profile の一覧を別 field に持つ。個々の document の profile は解析時に決まる。YamlDotNet を使わない profile でも source payload に互換実装が含まれるため、現行の固定依存配布契約は残る。

公式仕様例の構文比較と、raw slice/mapping・位置の回帰検査は分ける。`--profile` を指定して別の report directory へ出力し、legacy と CommonMark の成功件数を混ぜない。
