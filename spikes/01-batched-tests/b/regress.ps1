# THROWAWAY SPIKE 1B: bisect regression on the changed batch.cs: seeded failing 1 and 2 (6 proj x 10 tests x 100 ms, 12 tasks).
# Usage: pwsh -File regress.ps1 [-Root C:\Development\agent-swarm-wt\s1c] [-Failing 1,2]
param([string]$Root = 'C:\Development\agent-swarm-wt\s1c', [int[]]$Failing = @(1, 2))
$here = $PSScriptRoot
$gen = Join-Path $here '..\..\shared\sandbox-gen.cs'
$ev = Join-Path $here 'evidence'; New-Item -ItemType Directory -Force $ev | Out-Null
function Load { (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average }
Push-Location $here; dotnet run batch.cs 2>$null | Out-Null; Pop-Location
foreach ($f in $Failing) {
    $sb = Join-Path $Root "f$f"
    if (-not (Test-Path (Join-Path $sb 'tasks.json'))) { dotnet run $gen -- $sb --projects 6 --tests-per 10 --delay-ms 100 --seed 1 --failing $f --tasks 12 | Out-Host }
    $load = Load
    $state = Join-Path $Root "st-regress-f$f"; if (Test-Path $state) { Remove-Item -LiteralPath $state -Recurse -Force }
    Push-Location $here
    $out = dotnet run batch.cs -- $sb (Join-Path $sb 'tasks.json') --state $state --slots 2 2>&1
    Pop-Location
    $out | Where-Object { $_ -notlike '{*' } | Set-Content (Join-Path $ev "regress-f$f.log")
    $o = ($out | Where-Object { $_ -like '{*' } | Select-Object -Last 1) | ConvertFrom-Json
    $o | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ev "regress-f$f.json")
    Write-Host ("f{0}: load {1}%, suite runs {2} (bisect {3}, inferred {4}), landed {5}, rejected {6}, wall {7}s" -f $f, $load, $o.fullSuiteRuns, $o.bisectRuns, $o.inferredRedSkipped, $o.tasksLanded, (($o.rejected | % id) -join ','), $o.wallSeconds)
}
