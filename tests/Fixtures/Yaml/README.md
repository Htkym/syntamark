# Native YAML acceptance fixtures

The input bytes and expected JSON are unchanged excerpts from the upstream
[YAML Test Suite](https://github.com/yaml/yaml-test-suite/tree/6e6c296ae9c9d2d5c4134b4b64d01b29ac19ff6f),
release tag `data-2022-01-17`. `manifest.json` records the commit, input hashes,
titles, and expected outcome for the `native-yaml/1` profile. The accompanying
MIT license applies to these fixtures.

These cases cover the supported block/flow collection and scalar subset. They
do not claim conformance to the complete YAML Test Suite or all YAML 1.2.2
grammar. `55WF` is invalid syntax; `3R3P` is valid YAML using an anchor, which
this profile preserves as unsupported input instead of expanding it.
