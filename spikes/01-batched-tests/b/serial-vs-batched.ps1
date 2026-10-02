# THROWAWAY SPIKE 1B: measured serial baseline vs batched, failing 0, 6 projects x 10 tests x 100 ms, 12 tasks.
# Usage: pwsh -File serial-vs-batched.ps1 [-Root C:\Development\agent-swarm-wt\s1c]
# Records Win32_Processor LoadPercentage before each phase (the host is shared; wall time is noisy: compare COUNTS first).
param([string]$Root = 'C:\Development\agent-swarm-wt\s1c')
$here = $PSScriptRoot
$gen = Join-Path $here '..\..\shared\sandbox-gen.cs'
$ev = Join-Path $here 'evidence'; New-Item -ItemType Directory -Force $ev | Out-Null
$sb = Join-Path $Root 'f0'
function Load { (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average }
if (-not (Test-Path (Join-Path $sb 'tasks.json'))) {
    dotnet run $gen -- $sb --projects 6 --tests-per 10 --delay-ms 100 --seed 1 --failing 0 --tasks 12 | Out-Host
}
$res = [ordered]@{}
function Phase($name, $mode, $extra) {
    $load = Load
    $state = Join-Path $Root "st-$name"
    if (Test-Path $state) { Remove-Item -LiteralPath $state -Recurse -Force }
    Push-Location $here
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = dotnet run batch.cs -- $sb (Join-Path $sb 'tasks.json') --state $state --mode $mode --slots 2 @extra 2>$null
    $sw.Stop(); Pop-Location
    $json = ($out | Where-Object { $_ -like '{*' } | Select-Object -Last 1)
    $o = $json | ConvertFrom-Json
    $o | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ev "$name.json")
    $res[$name] = [ordered]@{ loadPercentBefore = $load; wallSecondsOuter = [math]::Round($sw.Elapsed.TotalSeconds, 1); summary = $o }
    Write-Host ("{0}: load before {1}%, outer wall {2}s, suite runs {3}, landed {4}" -f $name, $load, $res[$name].wallSecondsOuter, $o.fullSuiteRuns, $o.tasksLanded)
}
# Pre-compile the file-based apps so the first timed phase does not pay compile time.
Push-Location $here; dotnet run batch.cs 2>$null | Out-Null; dotnet run testgate.cs 2>$null | Out-Null; Pop-Location
Phase 'warm' 'measure' @()      # warms restore/build in the worktree and times one full suite
Phase 'serial-f0' 'serial' @()
Phase 'batched-f0' 'batched' @()
$res | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ev 'serial-vs-batched.json')
