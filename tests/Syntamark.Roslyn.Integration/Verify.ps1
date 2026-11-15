[CmdletBinding()]
param(
    [string] $CandidateVersion,
    [string] $CandidateFeed,
    [string] $ExpectedParserVersion
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$integration = Join-Path $PSScriptRoot 'Syntamark.Roslyn.Integration.csproj'
$compiler = Join-Path $PSScriptRoot 'CompilerHost/CompilerHost.csproj'
$output = Join-Path $root '.local/roslyn-canary'
$null = New-Item -ItemType Directory -Path $output -Force
if ([bool]$CandidateVersion -ne [bool]$CandidateFeed) {
    throw 'CandidateVersion and CandidateFeed must be specified together.'
}
if ($CandidateVersion -and !$ExpectedParserVersion) {
    throw 'A candidate must specify ExpectedParserVersion from its prepared manifest.'
}
$properties = @()
if ($CandidateVersion) {
    $properties = @("-p:SyntamarkSourceVersion=$CandidateVersion", '-p:SyntamarkCandidateMode=true',
        "-p:SyntamarkExpectedParserVersion=$ExpectedParserVersion")
    $feedPath = (Resolve-Path -LiteralPath $CandidateFeed).Path
    $escapedFeed = [System.Security.SecurityElement]::Escape($feedPath)
    $candidateConfig = Join-Path $output 'NuGet.Config'
    $configText = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="Syntamark.Source" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
    [System.IO.File]::WriteAllText($candidateConfig, $configText, [System.Text.UTF8Encoding]::new($false))
    & dotnet restore $integration @properties --configfile $candidateConfig
}
else {
    & dotnet restore $integration --locked-mode
}
if ($LASTEXITCODE -ne 0) { throw 'Source-package integration restore failed.' }
& dotnet restore $compiler --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Compiler-host restore failed.' }

# Use the optional local verification wrapper without making it a CI prerequisite.
function Build([string[]] $DotnetArguments) {
    $profileDirectory = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)
    $wrapper = Join-Path $profileDirectory '.agents/skills/rtk-dotnet-verify/scripts/Invoke-RtkDotNet.ps1'
    if (Test-Path -LiteralPath $wrapper) {
        & $wrapper -Action build -Arguments $DotnetArguments
    }
    else {
        & dotnet build @DotnetArguments
    }
    if ($LASTEXITCODE -ne 0) { throw 'Canary build failed.' }
}
Build (@($integration, '-c', 'Release', '--no-restore') + $properties)
$runArguments = @('--project', $integration, '-c', 'Release', '--no-restore', '--no-build') + $properties
$runArguments += @('--', '--candidate')
$mode = if ($CandidateVersion) { 'candidate' } else { 'local-source' }
& dotnet run @runArguments | Tee-Object -FilePath (Join-Path $output "driver-$mode.jsonl")
if ($LASTEXITCODE -ne 0) { throw 'Analyzer driver integration failed.' }

$markdown = Join-Path $output 'snapshot.md'
$sarif = Join-Path $output 'compiler.sarif'
$utf8 = [System.Text.UTF8Encoding]::new($false)
$snapshot = "`u{FEFF}---`r`ntitle: 😀`r`n---`r`n# TODO &amp;😀`r`n[x]()`r`n"
$snapshot += "`r`n<div>`r`n# TODO Hidden`r`n[bad](/wrong)`r`n</div>`r`n`r`ntext <span>**bold** [x](/u)</span>`r`n"
[System.IO.File]::WriteAllText($markdown, $snapshot, $utf8)
Build @($compiler, '-c', 'Release', '--no-restore', '-t:Rebuild', "-p:CanaryMarkdown=$markdown", "-p:CanaryErrorLog=$sarif")
$results = (Get-Content -LiteralPath $sarif -Raw | ConvertFrom-Json).runs[0].results
$todo = @($results | Where-Object ruleId -eq 'SMC001')
$identity = @($results | Where-Object ruleId -eq 'SMC000')
if ($todo.Count -ne 1 -or $identity.Count -ne 1 -or $identity[0].message.text -notmatch 'status=Complete; YAML=native-yaml/1;') {
    throw 'Real compiler host did not load the source parser and native YAML or emit the expected diagnostics.'
}
if ($ExpectedParserVersion -and !$identity[0].message.text.EndsWith("parser=$ExpectedParserVersion", [StringComparison]::Ordinal)) {
    throw 'Actual compiler loaded parser stamp differs from the prepared candidate.'
}
if ($identity[0].message.text -notmatch 'profile=commonmark/0\.31\.2; syntax=Document; nodes=[1-9][0-9]*; HTML=3; YAMLstate=Parsed;') {
    throw 'Actual compiler did not execute the candidate standard syntax path and separate native YAML probe.'
}
$region = $todo[0].locations[0].physicalLocation.region
if ($region.startLine -ne 4 -or $region.startColumn -ne 3 -or $region.endColumn -ne 15) {
    throw "Compiler diagnostic UTF-16 region differs: $($region | ConvertTo-Json -Compress)"
}
$initial = @{ startLine = $region.startLine; startColumn = $region.startColumn; endColumn = $region.endColumn }
Copy-Item -LiteralPath $sarif -Destination (Join-Path $output 'compiler-original.sarif') -Force
Copy-Item -LiteralPath $sarif -Destination (Join-Path $output "compiler-$mode-original.sarif") -Force
[System.IO.File]::WriteAllText($markdown, "# Done`r`n", $utf8)
Build @($compiler, '-c', 'Release', '--no-restore', '-t:Rebuild', "-p:CanaryMarkdown=$markdown", "-p:CanaryErrorLog=$sarif")
$edited = (Get-Content -LiteralPath $sarif -Raw | ConvertFrom-Json).runs[0].results
if (@($edited | Where-Object ruleId -eq 'SMC001').Count -ne 0) {
    throw 'Real compiler retained the edited snapshot diagnostic.'
}
$sdk = & dotnet --version
@{
    status = 'PASS'; sdk = $sdk; sourcePackageVersion = $(if ($CandidateVersion) { $CandidateVersion } else { 'local Portable source' })
    compilerIdentityDiagnostic = $identity[0].message.text; originalRegion = $initial
    profileId = 'commonmark/0.31.2'
    syntaxRequested = $true; yamlProbeProfileId = 'lithosharp-markdown/1'; yamlProfileId = 'native-yaml/1'
    driverRoslynVersion = '4.14.0'; host = 'dotnet build (UseSharedCompilation=false)'; ide = 'unverified'
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $output 'result.json')
Copy-Item -LiteralPath (Join-Path $output 'result.json') -Destination (Join-Path $output "$mode-result.json") -Force
