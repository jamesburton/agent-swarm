# THROWAWAY SPIKE 1B: conflict path (-Which conf) and overlap / pre-batching comparison (-Which ov).
# Usage: pwsh -File conflicts-overlap.ps1 -Which conf|ov [-Root C:\Development\agent-swarm-wt\s1c]
# conf: 12 tasks, 2 conflict pairs + 1 stacked pair. tasks.json is REPLACED by a copy whose `touches` are all bogus,
#       to prove batch.cs derives touches from git instead of trusting the file.
# ov:   12 tasks, 1 conflict + 2 overlap + 1 stacked pair; runs pre-batched (default) and naive fixed-4 chunks (--no-prebatch --fixed 4).
param([Parameter(Mandatory)][ValidateSet('conf', 'ov')][string]$Which, [string]$Root = 'C:\Development\agent-swarm-wt\s1c')
$here = $PSScriptRoot
$gen = Join-Path $here '..\..\shared\sandbox-gen.cs'
$ev = Join-Path $here 'evidence'; New-Item -ItemType Directory -Force $ev | Out-Null
function Load { (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average }
$sb = Join-Path $Root $Which
if (-not (Test-Path (Join-Path $sb 'tasks.json'))) {
    $pairs = if ($Which -eq 'conf') { '--conflicts', 2, '--stacked', 1 } else { '--conflicts', 1, '--overlap', 2, '--stacked', 1 }
    dotnet run $gen -- $sb --projects 6 --tests-per 10 --delay-ms 100 --seed 1 --failing 0 --tasks 12 @pairs | Out-Host
}
function Phase($name, $tasksFile, $extra) {
    $load = Load
    $state = Join-Path $Root "st-$name"
    if (Test-Path $state) { Remove-Item -LiteralPath $state -Recurse -Force }
    Push-Location $here
    $out = dotnet run batch.cs -- $sb $tasksFile --state $state --slots 2 @extra 2>&1
    $code = $LASTEXITCODE
    Pop-Location
    $out | Where-Object { $_ -notlike '{*' } | Set-Content (Join-Path $ev "$name.log")
    $json = ($out | Where-Object { $_ -like '{*' } | Select-Object -Last 1)
    $o = $json | ConvertFrom-Json
    $o | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ev "$name.json")
    Copy-Item (Join-Path $state "runs\$Which\returned.json") (Join-Path $ev "$name-returned.json") -ErrorAction SilentlyContinue
    Write-Host ("{0}: exit {1}, load before {2}%, suite runs {3} (bisect {4}), batches {5}, landed {6}, returned {7}, rebased-and-landed {8}, needs-worker {9}, rejected-red {10}, wall {11}s" -f `
        $name, $code, $load, $o.fullSuiteRuns, $o.bisectRuns, $o.batches, $o.tasksLanded, $o.returned, $o.rebasedAndLanded, $o.needsWorker, $o.rejectedRed, $o.wallSeconds)
}
Push-Location $here; dotnet run batch.cs 2>$null | Out-Null; Pop-Location   # pre-compile
if ($Which -eq 'conf') {
    $lying = Join-Path $sb 'tasks-lying.json'
    (Get-Content (Join-Path $sb 'tasks.json') -Raw) -replace '"touches": \[[^\]]*\]', '"touches": ["bogus/Ignored.cs"]' | Set-Content $lying
    Phase 'conf-prebatch' $lying @()
}
else {
    $tj = Join-Path $sb 'tasks.json'
    Phase 'ov-prebatch' $tj @()
    Phase 'ov-naive4' $tj @('--no-prebatch', '--fixed', '4')
}
