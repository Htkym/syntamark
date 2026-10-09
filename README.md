# LithoSharp.Markdown

Parse-only Markdown component for LithoSharp and SharpDeps. The runtime package is `LithoSharp.Markdown`; compiler hosts use `LithoSharp.Markdown.Source` with an exact matching version. Package IDs, contract 1.0 and profile `lithosharp-markdown/1` are unchanged by the repository move.

Canonical parser, DTOs and contracts remain under `src/LithoSharp.Markdown` so the parser hash and logical source paths are preserved. Runtime friend metadata is included only in the runtime assembly. Source payloads contain portable sources and explicit build targets; they exclude the public facade and friend metadata. HTML rendering, workspace policy and graph projection belong to the consumers.

`eng/markdown/source-history.json` maps original commits to the scoped imported history. `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/YamlDotNet.LICENSE.txt` retain author and dependency notices.

Run the existing parser checks with `dotnet run --project tests/LithoSharp.Markdown.Portability -c Release`. To create a fresh local immutable candidate, run `node eng/markdown/New-ArtifactPair.mjs prepare <full source commit> <2.0.0-preview.N> <new stage>`, restore and pack both prepared projects, then run `check-stage` and `seal`. Never replace an existing version or stage. Registered versions in `eng/markdown/immutable-versions.json` are rejected before stage creation. Record each sealed pair there before selecting it as a fixed consumer candidate. The CI workflow has read-only repository permission, runs the parser checks, and optionally packs a candidate without publishing it.

This MD-06 checkout has no remote yet. The final owner and publication destination must be approved before repository creation or source upload. Local candidate metadata deliberately omits a GitHub RepositoryUrl until that destination exists.
