# CommonMark 0.31.2 の対応範囲

`commonmark/0.31.2` は明示して選ぶ profile である。既定の `lithosharp-markdown/1` は拡張を含む互換 profile として利用できる。選択方法と HTML・front matter の違いは [profile の使い方](profile-design.md)を参照。

## 対応する構文

CommonMark profile は次の標準カテゴリを扱う。

| 分類 | 構文 |
|---|---|
| 基本規則 | tab、backslash escape、文字参照・数値文字参照、構文の優先順位 |
| 見出し・段落 | ATX 見出し、Setext 見出し、段落、空行、区切り線 |
| コンテナ | block quote、list item、ordered / unordered list |
| コード | indented code block、fenced code block、code span |
| インライン | inline の優先順位、強調、強い強調、link、参照定義、image、autolink |
| HTML | HTML block の7種、inline の raw HTML |
| テキスト | hard line break、soft line break、通常のテキスト |

table、task list、strikethrough、裸 URL などの拡張は、この profile では特別扱いしない。

## 公式例と利用上の限界

CommonMark 0.31.2 の公式652例に対する HTML 比較は次のとおり。

| Profile | 一致 | 差分 | 比較用 renderer で観測不可 |
|---|---:|---:|---:|
| `commonmark/0.31.2` | 652 | 0 | 0 |
| `lithosharp-markdown/1` | 521 | 129 | 2 |

この結果は公式例での対応範囲を示し、あらゆる入力への完全準拠、facts の完全性、位置や mapping の正しさを証明するものではない。互換 profile の差分には拡張や front matter の扱いの違いが含まれ、差分数を不具合数とはみなさない。観測不可の2例は、比較用 renderer が互換 profile の raw info string を解釈しないためである。

`ParseWithSyntax` の `RawSpan` は不明な場合に null となる。互換 profile では公式例中の25個の emphasis node に、親子の包含や兄弟の順序と矛盾する内部位置があり、構文 view はその位置を null として返す。位置を推測して補わない。

解析終了を表す `Status` の `Complete` と、対応範囲を表す `Coverage` は別の契約である。`markdownSyntax` capability は CommonMark profile でも `Partial` であり、HTML の自然文投影や一部の decoded mapping には制約がある。解析結果から「存在しない」と判断する前に、status・coverage・diagnostics を確認する。

HTML は構文として認識するが、HTML renderer、DOM、MDX の実行機能は提供しない。構文 view の位置と所有権については [構文 view の使い方](syntax-design.md)を参照。
