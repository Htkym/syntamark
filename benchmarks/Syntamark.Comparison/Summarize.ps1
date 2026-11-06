param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$reports = @(1..4 | ForEach-Object {
    $order = @('A', 'B', 'B', 'A')[$_ - 1]
    Get-Content -LiteralPath (Join-Path $ResultsDirectory "run-$_-$order.json") -Raw | ConvertFrom-Json
})
function Median($values) {
    $sorted = @($values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
if (@($reports.syntamark.assembly.sha256 | Sort-Object -Unique).Count -ne 1) { throw 'Syntamark assemblies differ.' }
if (@($reports.markdig.assembly.sha256 | Sort-Object -Unique).Count -ne 1) { throw 'Markdig assemblies differ.' }
if (@($reports.roslyn.assembly.sha256 | Sort-Object -Unique).Count -ne 1) { throw 'Roslyn assemblies differ.' }
$rows = foreach ($workload in $reports[0].results) {
    $perProcess = foreach ($report in $reports) {
        $observed = @($report.results | Where-Object { $_.Fixture -eq $workload.Fixture -and $_.Engine -eq $workload.Engine -and $_.Stage -eq $workload.Stage })
        if ($observed.Count -ne 1 -or $observed[0].Samples.Count -ne 5) { throw 'Missing workload or samples.' }
        [pscustomobject]@{ label = $report.label; medianNanoseconds = (Median $observed[0].Samples.NanosecondsPerOperation)
            medianAllocatedBytes = (Median $observed[0].Samples.AllocatedBytesPerOperation) }
    }
    [pscustomobject]@{ fixture = $workload.Fixture; engine = $workload.Engine; stage = $workload.Stage
        independentProcesses = 4; samplesPerProcess = 5; operationsPerSample = $workload.OperationsPerSample
        medianOfProcessMedianNanoseconds = (Median $perProcess.medianNanoseconds)
        minProcessMedianNanoseconds = ($perProcess.medianNanoseconds | Measure-Object -Minimum).Minimum
        maxProcessMedianNanoseconds = ($perProcess.medianNanoseconds | Measure-Object -Maximum).Maximum
        medianOfProcessMedianAllocatedBytes = (Median $perProcess.medianAllocatedBytes)
        perProcess = @($perProcess) }
}
$retained = foreach ($workload in $reports[0].retained) {
    $observed = @($reports.retained | Where-Object { $_.fixture -eq $workload.fixture -and $_.engine -eq $workload.engine -and $_.stage -eq $workload.stage })
    [pscustomobject]@{ fixture = $workload.fixture; engine = $workload.engine; stage = $workload.stage; cohortOutputs = 8
        medianBytesPerOutput = (Median $observed.bytesPerOutput)
        perProcessBytesPerOutput = @($observed.bytesPerOutput) }
}
$summary = [pscustomobject]@{ contract = $reports[0].contract; order = @($reports.processOrder)
    aggregation = 'Median of four independent process medians; all five raw samples per process remain in the source reports.'
    runtime = $reports[0].framework; os = $reports[0].os; cpu = $reports[0].cpu
    syntamark = $reports[0].syntamark; markdig = $reports[0].markdig; roslyn = $reports[0].roslyn
    validation = $reports[0].validation; rows = @($rows); retained = @($retained) }
$outputPath = Join-Path $ResultsDirectory 'summary.json'
if (Test-Path -LiteralPath $outputPath) { throw 'Refusing to overwrite summary.json.' }
$summary | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $outputPath -Encoding utf8NoBOM
$rows | Select-Object fixture,engine,stage,medianOfProcessMedianNanoseconds,medianOfProcessMedianAllocatedBytes | Format-Table -AutoSize
