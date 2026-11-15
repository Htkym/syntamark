[CmdletBinding()]
param([Parameter(Mandatory)][string] $Stage)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$stagePath = (Resolve-Path -LiteralPath $Stage).Path
$manifestPath = Join-Path $stagePath 'pair-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$producer = Join-Path $PSScriptRoot 'New-ArtifactPair.mjs'
& node $producer check-pair $stagePath $manifestHash
if ($LASTEXITCODE -ne 0) { throw 'The sealed pair did not pass byte verification.' }
$consumers = Join-Path $stagePath 'consumers'
if (Test-Path -LiteralPath $consumers) { throw 'Use a fresh consumer directory; preserve prior verification evidence.' }
$null = New-Item -ItemType Directory -Path $consumers
$utf8 = [Text.UTF8Encoding]::new($false)
$version = $manifest.componentVersion
$config = Join-Path $stagePath 'NuGet.Config'
$packages = Join-Path $stagePath 'packages'
$sourceProject = $null
$savedPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $packages

function Build([string] $Project) {
    $wrapper = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.agents/skills/rtk-dotnet-verify/scripts/Invoke-RtkDotNet.ps1'
    $buildArguments = @($Project, '-c', 'Release', '--no-restore')
    if (Test-Path -LiteralPath $wrapper) { & $wrapper -Action build -Arguments $buildArguments }
    else { & dotnet build @buildArguments }
    if ($LASTEXITCODE -ne 0) { throw "Consumer build failed: $Project" }
}

$program = @'
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
#if SOURCE_HOST
using Parser = Syntamark.Compilation.MdParser;
using Parts = Syntamark.Compilation.MdAnalysisParts;
using Options = Syntamark.Compilation.MdOptions;
using Yaml = Syntamark.Compilation.MdYamlParser;
using AnalysisResult = Syntamark.Compilation.MdAnalysisResult;
using TextEdit = Syntamark.Compilation.MdTextEdit;
using Span = Syntamark.Compilation.MdRawRange;
#else
using Parser = Syntamark.MarkdownParser;
using Parts = Syntamark.MarkdownAnalysisParts;
using Options = Syntamark.MarkdownParseOptions;
using Yaml = Syntamark.YamlParser;
using AnalysisResult = Syntamark.MarkdownAnalysisResult;
using TextEdit = Syntamark.MarkdownTextEdit;
using Span = Syntamark.RawSpan;
#endif

const string markdown = "---\r\ntitle: Pair\r\n---\r\n# Pair 😀\r\n\r\n[guide](guide.md)\r\n\r\nText[^note].\r\n\r\n[^note]: Footnote.\r\n\r\nAlpha\r\n\r\nBeta\r\n\r\nGamma\r\n\r\nTerm {#old}\r\n";
var analysis = Parser.Analyze(markdown, Parts.All, "package-pair", "pair.md", "1",
    new Options(profileId: "syntamark-docs/2"), CancellationToken.None);
if (analysis.Status.ToString() != "Complete" || analysis.Root is null || analysis.ProfileId != "syntamark-docs/2")
    throw new InvalidOperationException("The packaged Markdown documentation profile did not complete.");
const Parts selection = Parts.UnsupportedSyntax;
#if SOURCE_HOST
var session = Syntamark.Compilation.MdParseSession.CreateForAnalysis(markdown, selection,
    "package-pair", "pair.md", "1", new Options(profileId: "syntamark-docs/2"), CancellationToken.None);
#else
var session = Parser.CreateAnalysisSession(markdown, selection, "package-pair", "pair.md", "1",
    new Options(profileId: "syntamark-docs/2"), CancellationToken.None);
#endif
var savedSelection = JsonSerializer.Serialize(SelectedSnapshot(session.Analysis));
var betaStart = markdown.IndexOf("Beta", StringComparison.Ordinal);
const string replacement = "Beta {#edit}";
var changedText = markdown.Remove(betaStart, 4).Insert(betaStart, replacement);
var updated = session.ApplyEdits(session.Analysis.TextHash,
    new[] { new TextEdit(new Span(betaStart, 4), replacement) }, "2", null, CancellationToken.None);
foreach (var selected in new[] { session.Analysis, updated.Analysis })
    if ((int)selected.RequestedParts != 128 || selected.Root is not null
        || selected.GetAvailability(Parts.Syntax).ToString() != "NotRequested"
        || selected.GetAvailability(Parts.UnsupportedSyntax).ToString() != "Complete")
        throw new InvalidOperationException("The analysis session lost its selected output mask or exposed an unrequested root.");
if (updated.Mode.ToString() != "Delta" || updated.ReusedBlocks != 2 || updated.RebasedBlocks != 1)
    throw new InvalidOperationException("The packaged analysis session did not reuse the isolated prefix and rebase the suffix.");
var candidates = updated.Analysis.UnsupportedSyntax!;
var expectedTokens = new[] { "{#edit}", "{#old}" };
if (candidates.Count != expectedTokens.Length)
    throw new InvalidOperationException("The delta did not retain both introduced and shifted unsupported candidates.");
for (var i = 0; i < candidates.Count; i++)
    if (candidates[i].RawSpan.Start != changedText.IndexOf(expectedTokens[i], StringComparison.Ordinal)
        || candidates[i].RawSpan.Length != expectedTokens[i].Length
        || changedText.Substring(candidates[i].RawSpan.Start, candidates[i].RawSpan.Length) != expectedTokens[i])
        throw new InvalidOperationException("An unsupported candidate retained a stale source position.");
if (savedSelection != JsonSerializer.Serialize(SelectedSnapshot(session.Analysis)))
    throw new InvalidOperationException("Applying an edit mutated the previous selected snapshot.");
#if SOURCE_HOST
var bodyStart = updated.Analysis.BodySpan?.Start ?? throw new InvalidOperationException("The selected analysis lost its body envelope.");
if (session.Analysis.Blocks is null || updated.Analysis.Blocks is null || bodyStart <= 0
    || updated.Analysis.Blocks[0].Span.Start != 0
    || !updated.Analysis.Blocks.OfType<Syntamark.Compilation.LithoParagraph>().Any(block =>
        block.Span.Start == changedText.IndexOf("Gamma", StringComparison.Ordinal) - bodyStart))
    throw new InvalidOperationException("The Source analysis session lost body-relative rendering blocks.");
#endif
var analysisSessionSnapshot = new
{
    mask = (int)selection,
    initial = new { mode = session.Mode.ToString(), fallback = session.Fallback.ToString(), session.ReusedBlocks,
        session.RebasedBlocks, session.ParsedBlocks, session.ScanUnits, session.OutputItems, analysis = SelectedSnapshot(session.Analysis) },
    updated = new { mode = updated.Mode.ToString(), fallback = updated.Fallback.ToString(), updated.ReusedBlocks,
        updated.RebasedBlocks, updated.ParsedBlocks, updated.ScanUnits, updated.OutputItems, analysis = SelectedSnapshot(updated.Analysis) },
};
var yamlSnapshots = new[] { "type: concept\nvalues: [\"false\", true, 0x2A]\n", "a: 1\na: 2\n", "a: *alias\n" }
    .Select(raw =>
    {
        var parsed = Yaml.Parse(raw);
        return new
        {
            status = parsed.Status.ToString(), parsed.ProfileId,
            entries = parsed.Root?.Entries.Select(entry => new
            {
                key = entry.Key.Value, value = entry.Value.Value,
                kind = entry.Value.Kind.ToString(), scalarKind = entry.Value.ScalarKind.ToString(),
                entry.Key.RawSpan.Start, entry.Key.RawSpan.Length,
                children = entry.Value.Children.Select(child => new { child.Value, kind = child.ScalarKind.ToString() }),
            }),
            diagnostics = parsed.Diagnostics.Select(diagnostic => new
            {
                kind = diagnostic.Kind.ToString(), diagnostic.Reason, diagnostic.RawSpan.Start, diagnostic.RawSpan.Length,
            }),
        };
    }).ToArray();
if (yamlSnapshots[0].status != "Complete" || yamlSnapshots[1].status != "Partial" || yamlSnapshots[2].status == "Complete")
    throw new InvalidOperationException("The packaged YAML parser lost complete, duplicate or unsupported states.");
var parserAssembly = typeof(Parser).Assembly;
var noRetiredDependencies = !parserAssembly.GetReferencedAssemblies().Any(reference =>
    reference.Name is "YamlDotNet" or "SkiaSharp" || reference.Name?.StartsWith("SkiaSharp.NativeAssets", StringComparison.Ordinal) == true);
if (!noRetiredDependencies)
    throw new InvalidOperationException("The consumer loaded a retired dependency.");
var snapshot = new
{
    analysis.ParserVersion, analysis.ProfileId, analysis.YamlProfileId, analysis.ContractVersion,
    status = analysis.Status.ToString(), syntax = analysis.Root.DescendantsAndSelf()
        .Select(node => new { kind = node.Kind.ToString(), start = node.RawSpan?.Start, length = node.RawSpan?.Length }),
    yaml = yamlSnapshots,
    analysisSession = analysisSessionSnapshot,
};
#if SOURCE_HOST
var sourceParseCompiled = parserAssembly.GetType("Syntamark.Compilation.MdParser") is not null;
var noSourceFacade = parserAssembly.GetType("Syntamark.MarkdownParser") is null;
var noSourceFriendMetadata = !parserAssembly.GetCustomAttributes<InternalsVisibleToAttribute>().Any();
if (!sourceParseCompiled || !noSourceFacade || !noSourceFriendMetadata)
    throw new InvalidOperationException("The Source package payload escaped its compiler-host boundary.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS", parserVersion = analysis.ParserVersion, yamlProfileId = analysis.YamlProfileId,
    sourceParseCompiled, noSourceFacade, noSourceFriendMetadata,
    sourceBodyRelativeBlocks = true,
    noThirdPartyParserDependencies = noRetiredDependencies, snapshot,
}));
#else
var loadedVersion = parserAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
if (args.Length != 1 || loadedVersion != args[0])
    throw new InvalidOperationException("The runtime consumer loaded a different package version.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS", parserVersion = analysis.ParserVersion, yamlProfileId = analysis.YamlProfileId,
    runtimeAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(parserAssembly.Location))).ToLowerInvariant(),
    noThirdPartyParserDependencies = noRetiredDependencies, snapshot,
}));
#endif

static object SelectedSnapshot(AnalysisResult result) => new
{
    result.ScopeId, result.SourceId, result.SourceVersion, result.ParserVersion, result.ProfileId, result.YamlProfileId,
    result.ContractVersion, result.OptionsHash, result.TextHash, result.AnalysisHash, result.AnalysisSchemaVersion,
    mask = (int)result.RequestedParts, status = result.Status.ToString(), rootAbsent = result.Root is null,
    bodySpan = result.BodySpan is { } body ? new { body.Start, body.Length } : null,
    availability = Enumerable.Range(0, 8).Select(bit => result.GetAvailability((Parts)(1 << bit)).ToString()).ToArray(),
    candidates = result.UnsupportedSyntax?.Select(candidate => new
    {
        kind = candidate.Kind.ToString(), candidate.Reason, candidate.RawSpan.Start, candidate.RawSpan.Length,
    }).ToArray(),
    diagnostics = result.Diagnostics.Select(diagnostic => new
    {
        diagnostic.Id, severity = diagnostic.Severity.ToString(), diagnostic.Origin, diagnostic.Reason,
        diagnostic.Message, start = diagnostic.RawSpan?.Start, length = diagnostic.RawSpan?.Length,
    }).ToArray(),
    coverage = new { raw = result.Coverage.RawPositions.ToString(), decoded = result.Coverage.DecodedMapping.ToString(), result.Coverage.Reasons },
};
'@

try {
    $results = @{}
    foreach ($kind in @('runtime', 'source')) {
        $directory = Join-Path $consumers $kind
        $null = New-Item -ItemType Directory -Path $directory
        $project = Join-Path $directory 'Consumer.csproj'
        $reference = if ($kind -eq 'runtime') {
            "<PackageReference Include=`"Syntamark`" Version=`"[$version]`" />"
        }
        else {
            "<PackageReference Include=`"Syntamark.Source`" Version=`"[$version]`" PrivateAssets=`"all`" IncludeAssets=`"build`" />"
        }
        $sourceProperties = if ($kind -eq 'source') {
            "<DefineConstants>SOURCE_HOST</DefineConstants><SyntamarkIncludeSource>true</SyntamarkIncludeSource><SyntamarkSourceVersion>$version</SyntamarkSourceVersion><SyntamarkExpectedParserVersion>$($manifest.parserVersion)</SyntamarkExpectedParserVersion>"
        } else { '' }
        $projectText = "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><LangVersion>13</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>$sourceProperties</PropertyGroup><ItemGroup>$reference</ItemGroup></Project>"
        [IO.File]::WriteAllText($project, $projectText, $utf8)
        [IO.File]::WriteAllText((Join-Path $directory 'Program.cs'), $program, $utf8)
        & dotnet restore $project --configfile $config --packages $packages
        if ($LASTEXITCODE -ne 0) { throw "Consumer restore failed: $kind" }
        Build $project
        $output = & dotnet run --project $project -c Release --no-restore --no-build -- $version
        if ($LASTEXITCODE -ne 0) { throw "Consumer execution failed: $kind" }
        [IO.File]::WriteAllText((Join-Path $directory 'result.json'), ($output -join "`n"), $utf8)
        $results[$kind] = ($output -join "`n") | ConvertFrom-Json
        if ($kind -eq 'source') { $sourceProject = $project }
    }
    if (($results.runtime.snapshot | ConvertTo-Json -Depth 32 -Compress) -cne ($results.source.snapshot | ConvertTo-Json -Depth 32 -Compress)) {
        throw 'Restored runtime and Source produced different Markdown/YAML facts, diagnostics or positions.'
    }
    $guardOutput = & dotnet msbuild $sourceProject -t:CheckSyntamarkSourceArtifact -p:SyntamarkExpectedParserVersion=wrong-parser-stamp -p:Configuration=Release 2>&1
    $guardExit = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $consumers 'wrong-stamp.log'), ($guardOutput -join "`n"), $utf8)
    if ($guardExit -eq 0 -or ($guardOutput -join "`n") -notmatch 'Selected Markdown parser stamp differs from the expected pair manifest') {
        throw 'The actual Source package target did not reject a mismatched parser stamp.'
    }
    & (Join-Path $root 'tests/Syntamark.Roslyn.Integration/Verify.ps1') -CandidateVersion $version -CandidateFeed (Join-Path $stagePath 'feed') -ExpectedParserVersion $manifest.parserVersion
    if ($LASTEXITCODE -ne 0) { throw 'The candidate failed the actual Roslyn compiler-host path.' }
    $canary = @{
        status = 'PASS'; componentVersion = $version; parserVersion = $results.runtime.parserVersion
        runtimeAssemblySha256 = $results.runtime.runtimeAssemblySha256
        sourceParseCompiled = $results.source.sourceParseCompiled; noSourceFacade = $results.source.noSourceFacade
        noSourceFriendMetadata = $results.source.noSourceFriendMetadata; expectedStampRejected = $true
        noThirdPartyParserDependencies = $results.runtime.noThirdPartyParserDependencies -and $results.source.noThirdPartyParserDependencies
        yamlProfileId = $results.runtime.yamlProfileId
        sourceBodyRelativeBlocks = $results.source.sourceBodyRelativeBlocks
        analysisSession = $results.runtime.snapshot.analysisSession
    }
    $canaryPath = Join-Path $consumers 'canary.json'
    [IO.File]::WriteAllText($canaryPath, ($canary | ConvertTo-Json -Depth 8), $utf8)
    & node $producer verify-restored $stagePath $canaryPath
    if ($LASTEXITCODE -ne 0) { throw 'Restored package verification failed.' }
}
finally {
    $env:NUGET_PACKAGES = $savedPackages
}
