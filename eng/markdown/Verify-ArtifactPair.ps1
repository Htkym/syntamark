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
#else
using Parser = Syntamark.MarkdownParser;
using Parts = Syntamark.MarkdownAnalysisParts;
using Options = Syntamark.MarkdownParseOptions;
using Yaml = Syntamark.YamlParser;
#endif

const string markdown = "---\r\ntitle: Pair\r\n---\r\n# Pair 😀\r\n\r\n[guide](guide.md)\r\n\r\nText[^note].\r\n\r\n[^note]: Footnote.\r\n";
var analysis = Parser.Analyze(markdown, Parts.All, "package-pair", "pair.md", "1",
    new Options(profileId: "syntamark-docs/2"), CancellationToken.None);
if (analysis.Status.ToString() != "Complete" || analysis.Root is null || analysis.ProfileId != "syntamark-docs/2")
    throw new InvalidOperationException("The packaged Markdown documentation profile did not complete.");
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
    }
    $canaryPath = Join-Path $consumers 'canary.json'
    [IO.File]::WriteAllText($canaryPath, ($canary | ConvertTo-Json -Depth 8), $utf8)
    & node $producer verify-restored $stagePath $canaryPath
    if ($LASTEXITCODE -ne 0) { throw 'Restored package verification failed.' }
}
finally {
    $env:NUGET_PACKAGES = $savedPackages
}
