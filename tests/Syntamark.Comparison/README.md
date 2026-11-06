# Markdig との共通 facts 比較

Markdig 1.5.1 をこのテスト project だけで使う。製品の依存や backend は変えない。

```powershell
dotnet restore tests/Syntamark.Comparison --locked-mode
dotnet run --project tests/Syntamark.Comparison -c Release --no-restore -- --output .local/comparison/current.json
```

`headings-and-resolved-links/1` は、見出しの level/decoded text/raw span と、解決済みリンク・画像の label/target/title/raw span/target span を文書順に比べる。独立に選んだ原文 token と decoded 値を期待値にする。参照定義は link use と分け、target span は定義側を指す。slug、section、code block、prose、診断、任意 substring の mapping は共通出力に含めない。

Markdig は `UsePreciseSourceLocation()` のみを有効にする。Syntamark は既存の `lithosharp-markdown/1`。共有する入力は両 profile の共通部分であり、この比較から純粋 CommonMark 全体の準拠や処理量の同等性は判断できない。

Markdig の end は inclusive なので `length = end - start + 1` に変換する。`SourceSpan.Empty` `(0,-1)` は位置不明として null にし、それ以外の `(start,start-1)` は既知の 0 幅として保持する。`UrlSpan` の lexical token が `<>` を含む例は、内容だけの Syntamark `TargetRawSpan` と意味が異なるため、別の empty destination probe で実値を出す。比較 adapter は marker を推測で除去しない。

共通5入力は不一致なら exit 1。empty destination probe は値と両実装の実位置を報告し、共通 subset の成功数には加えない。entity probe は Syntamark の `Map` と Markdig の `Original`/`Transcoded` を別の観測として確認する。後者を任意範囲 mapping と同一視しない。

出力には入力 checksum、profile、options hash、parser stamp、Markdig の実ロード identity と commit、runtime/OS を残す。`--output` を省略すると JSON を標準出力へ出す。性能は測定しない。
