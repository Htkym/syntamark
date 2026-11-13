Earlier Markdown packages used YamlDotNet 18.1.0 (MIT). Its license remains in
licenses/YamlDotNet.LICENSE.txt as historical attribution. The native-yaml/1
implementation does not depend on or include that package. Original parser
authors and MIT terms are preserved in LICENSE.

The test fixture tests/Fixtures/CommonMark/0.31.2/spec.json is the unmodified CommonMark
Spec 0.31.2 example set by John MacFarlane, licensed under CC BY-SA 4.0.
See tests/Fixtures/CommonMark/0.31.2/README.md for its source, checksum, and attribution.
This test data is not included in the runtime or source NuGet packages.

The pinned YAML Test Suite subset in tests/Fixtures/Yaml uses its upstream MIT
license. See that directory's README, manifest and LICENSE.txt for provenance.
These fixtures are not included in the runtime or source NuGet packages.

CommonMark HTML named character references are generated from WHATWG HTML
entities.json (downloaded 2026-11-06, SHA-256
d741d877ac77c4194c4ad526b5b4a19aef8dfe411ab840a466891cdbb9f362e6).
The incorporated source portions use the BSD 3-Clause license; see
licenses/WHATWG.LICENSE.txt.

CommonMark reference-label case folding uses Unicode 15.1.0 CaseFolding.txt
(SHA-256 4e55acfdc32825a22e87670e9056a3bf94ad7c5400065778e9e10f8314372bcf),
retaining its common/full mappings without locale-specific mappings or normalization.
See licenses/Unicode.LICENSE.txt. These notices accompany both runtime and source packages.
