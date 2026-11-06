param(
    [string]$Runner = (Join-Path $PSScriptRoot 'bin/Release/net10.0/Syntamark.Comparison.dll'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'results/current')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$orders = @('A', 'B', 'B', 'A')
for ($index = 0; $index -lt $orders.Count; $index++) {
    $label = 'run-' + ($index + 1) + '-' + $orders[$index]
    $result = Join-Path $OutputDirectory ($label + '.json')
    $log = Join-Path $OutputDirectory ($label + '.log')
    if ((Test-Path -LiteralPath $result) -or (Test-Path -LiteralPath $log)) {
        throw "Refusing to overwrite existing evidence: $label"
    }
    & dotnet $Runner $orders[$index] $label $result *> $log
    if ($LASTEXITCODE -ne 0) { throw "Comparison process failed; inspect $log" }
    Get-Content -LiteralPath $log
}
