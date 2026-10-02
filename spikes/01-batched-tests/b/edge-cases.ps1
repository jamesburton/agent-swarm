# THROWAWAY SPIKE 1B: failure edges. Each case states its EXPECTED exit code and message; the script checks both and
# fails the case if the process hangs (60 s limit: none of these may run a suite). Run: pwsh -File edge-cases.ps1
#
# Case                         Expected exit  Expected one-line stderr (regex)                      Other expected effect
# a all merges conflict        1              CONFLICT T002 .*returned to worker / CONFLICT T004    0 suite runs, 0 landed, needsWorker 2, terminates
# b empty batch                0              nothing to do: tasks.json is empty                    0 suite runs, no worktree created
# c task branch missing        3              error: task branch not found: T999                    nothing merged (preflight)
# d state path > 200 chars     2              error: state dir path is \d+ chars \(limit 200\)      (batch.cs AND testgate.cs)
# e malformed tasks.json       3              error: tasks file is not valid JSON
param([string]$Root = 'C:\Development\agent-swarm-wt\s1c')
$here = $PSScriptRoot
$gen = Join-Path $here '..\..\shared\sandbox-gen.cs'
$ev = Join-Path $here 'evidence'; New-Item -ItemType Directory -Force $ev | Out-Null
$sb = Join-Path $Root 'edge'

# Tiny sandbox: 4 tasks = 2 conflicting pairs (T001/T002 on one file, T003/T004 on another). Land T001 and T003 on main,
# so T002 and T004 each conflict with main (every merge in the batch fails).
if (-not (Test-Path (Join-Path $sb 'tasks.json'))) {
    dotnet run $gen -- $sb --projects 2 --tests-per 2 --delay-ms 10 --seed 1 --tasks 4 --conflicts 2 | Out-Host
    git -C $sb merge -q --no-edit task/T001 2>&1 | Out-Host
    git -C $sb merge -q --no-edit task/T003 2>&1 | Out-Host
}
function Task($id) { "{ `"id`": `"$id`", `"touches`": [], `"branch`": `"task/$id`" }" }
Set-Content (Join-Path $sb 'edge-allconflict.json') ("[" + ((Task 'T002'), (Task 'T004') -join ',') + "]")
Set-Content (Join-Path $sb 'edge-empty.json') '[]'
Set-Content (Join-Path $sb 'edge-missing.json') ("[" + ((Task 'T002'), (Task 'T999') -join ',') + "]")
Set-Content (Join-Path $sb 'edge-bad.json') '[ { "id": '
Push-Location $here; dotnet run batch.cs 2>$null | Out-Null; dotnet run testgate.cs 2>$null | Out-Null; Pop-Location   # pre-compile

function Invoke-Limited([string[]]$argv, [int]$limitMs = 60000) {
    $o = [IO.Path]::GetTempFileName(); $e = [IO.Path]::GetTempFileName()
    $p = Start-Process dotnet -ArgumentList $argv -WorkingDirectory $here -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e
    $done = $p.WaitForExit($limitMs)
    if (-not $done) { taskkill /F /T /PID $p.Id | Out-Null }
    $r = [pscustomobject]@{ exit = $(if ($done) { $p.ExitCode } else { 'HUNG' }); out = (Get-Content $o -Raw); err = (Get-Content $e -Raw) }
    Remove-Item $o, $e -Force; $r
}
$fail = 0; $report = @()
function Case($name, $r, $expectExit, $expectErr, $extra = { $true }) {
    $errLines = ($r.err -split "`r?`n" | Where-Object { $_ -match 'error:|nothing to do|CONFLICT|REBASE' })
    $ok = ("$($r.exit)" -eq "$expectExit") -and ($r.err -match $expectErr) -and (& $extra $r)
    if (-not $ok) { $script:fail++ }
    $script:report += [pscustomobject]@{ case = $name; expectedExit = $expectExit; actualExit = $r.exit; result = $(if ($ok) { 'PASS' } else { 'FAIL' }); stderrKeyLines = ($errLines -join ' | ') }
}
function Batch($tasksFile, $state, $extra = @()) { Invoke-Limited (@('run', 'batch.cs', '--', $sb, (Join-Path $sb $tasksFile), '--state', $state, '--slots', '1') + $extra) }
function Json($r) { ($r.out -split "`r?`n" | Where-Object { $_ -like '{*' } | Select-Object -Last 1) | ConvertFrom-Json }

# (a) everything conflicts: must terminate, run no suite, report needs-worker for both
$r = Batch 'edge-allconflict.json' (Join-Path $Root 'st-edge-a')
$s = Json $r
Case 'a all merges conflict' $r 1 'CONFLICT T002[\s\S]*CONFLICT T004' { param($x) $s.fullSuiteRuns -eq 0 -and $s.tasksLanded -eq 0 -and $s.needsWorker -eq 2 }
# (b) empty batch
$r = Batch 'edge-empty.json' (Join-Path $Root 'st-edge-b')
Case 'b empty batch' $r 0 'nothing to do: tasks.json is empty'
# (c) task branch does not exist
$r = Batch 'edge-missing.json' (Join-Path $Root 'st-edge-c')
Case 'c missing task branch' $r 3 'error: task branch not found: T999'
# (d) state path > 200 chars (batch.cs and testgate.cs)
$long = Join-Path $Root ('st-edge-d\' + ((1..8 | ForEach-Object { 'segment-' + ('x' * 20) }) -join '\'))
$r = Batch 'edge-allconflict.json' $long
Case "d long state path ($($long.Length) chars) batch.cs" $r 2 'error: state dir path is \d+ chars \(limit 200\)'
$r = Invoke-Limited @('run', 'testgate.cs', '--', 'run', '--slots', '1', '--state', $long, '--', 'cmd', '/c', 'exit', '0')
Case "d long state path testgate.cs" $r 2 'error: state dir path is \d+ chars \(limit 200\)'
# (e) malformed tasks.json
$r = Batch 'edge-bad.json' (Join-Path $Root 'st-edge-e')
Case 'e malformed tasks.json' $r 3 'error: tasks file is not valid JSON'

$report | Format-Table -AutoSize -Wrap | Out-String -Width 250 | Tee-Object -FilePath (Join-Path $ev 'edge-cases.out')
$report | ConvertTo-Json | Set-Content (Join-Path $ev 'edge-cases.json')
Write-Host "edge cases failed: $fail"
exit $fail
