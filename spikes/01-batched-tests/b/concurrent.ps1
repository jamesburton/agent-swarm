# THROWAWAY SPIKE 1B: two batch.cs instances, separate sandboxes, ONE shared state dir, --slots 1.
# Part A: both run to completion; show full-suite runs serialise through the slot (waitMs > 0, no overlap, no deadlock).
# Part B: kill the slot holder (testgate process tree) mid-run with a short expiry; show the other instance reclaims.
# Usage: pwsh -File concurrent.ps1 [-Root C:\Development\agent-swarm-wt\s1c] [-ExpirySec 8]
param([string]$Root = 'C:\Development\agent-swarm-wt\s1c', [int]$ExpirySec = 8)
$here = $PSScriptRoot
$gen = Join-Path $here '..\..\shared\sandbox-gen.cs'
$ev = Join-Path $here 'evidence'; New-Item -ItemType Directory -Force $ev | Out-Null
function Load { (Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average }
foreach ($n in 'c2a', 'c2b') {
    if (-not (Test-Path (Join-Path $Root "$n\tasks.json"))) { dotnet run $gen -- (Join-Path $Root $n) --projects 4 --tests-per 5 --delay-ms 100 --seed 1 --failing 0 --tasks 6 | Out-Host }
}
Push-Location $here; dotnet run batch.cs 2>$null | Out-Null; dotnet run testgate.cs 2>$null | Out-Null; Pop-Location

function Start-Instance($n, $state, $extra) {
    $a = @('run', (Join-Path $here 'batch.cs'), '--', (Join-Path $Root $n), (Join-Path $Root "$n\tasks.json"), '--state', $state, '--slots', '1', '--testgate', (Join-Path $here 'testgate.cs')) + $extra
    Start-Process dotnet -ArgumentList $a -WorkingDirectory $here -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $Root "$n.out") -RedirectStandardError (Join-Path $Root "$n.err")
}
function Read-Summary($n) {
    $l = Get-Content (Join-Path $Root "$n.out") -ErrorAction SilentlyContinue | Where-Object { $_ -like '{*' } | Select-Object -Last 1
    if ($l) { $l | ConvertFrom-Json } else { $null }
}
function Last-Err($n) { (Get-Content (Join-Path $Root "$n.err") -ErrorAction SilentlyContinue | Where-Object { $_ -like 'error:*' } | Select-Object -Last 1) }

# ---------------- Part A: serialisation ----------------
$stateA = Join-Path $Root 'st-conc'
if (Test-Path $stateA) { Remove-Item -LiteralPath $stateA -Recurse -Force }
$loadA = Load
$t0 = [DateTime]::UtcNow
$pa = Start-Instance 'c2a' $stateA @(); $pb = Start-Instance 'c2b' $stateA @()
$ok = $pa.WaitForExit(1800000) -and $pb.WaitForExit(1800000)
$spanMs = ([DateTime]::UtcNow - $t0).TotalMilliseconds
if (-not $ok) { Write-Host 'DEADLOCK/TIMEOUT: killing'; $pa, $pb | ForEach-Object { if (-not $_.HasExited) { taskkill /F /T /PID $_.Id | Out-Null } } }
$sa = Read-Summary 'c2a'; $sb = Read-Summary 'c2b'
$all = @(); foreach ($p in @(@('c2a', $sa), @('c2b', $sb))) { foreach ($s in $p[1].suites) { $all += [pscustomobject]@{ inst = $p[0]; label = $s.label; waitMs = $s.waitMs; runMs = $s.runMs; acq = [DateTime]$s.acquiredUtc; rel = [DateTime]$s.releasedUtc } } }
$all = $all | Sort-Object acq
$overlaps = 0; for ($i = 1; $i -lt $all.Count; $i++) { if ($all[$i].acq -lt $all[$i - 1].rel) { $overlaps++ } }
$busyMs = ($all | Measure-Object runMs -Sum).Sum
$firstAcq = ($all | Select-Object -First 1).acq; $lastRel = ($all | Measure-Object rel -Maximum).Maximum
$utilFromFirst = $busyMs / ($lastRel - $firstAcq).TotalMilliseconds
$partA = [ordered]@{
    loadPercentBefore = $loadA; completed = $ok; exitCodes = @($pa.ExitCode, $pb.ExitCode); suiteRuns = $all.Count; overlappingHolds = $overlaps
    suites = $all | ForEach-Object { [ordered]@{ inst = $_.inst; label = $_.label; waitMs = $_.waitMs; runMs = $_.runMs; acquiredUtc = $_.acq.ToString('o'); releasedUtc = $_.rel.ToString('o') } }
    maxWaitMs = ($all | Measure-Object waitMs -Maximum).Maximum
    slotBusyFractionFromFirstAcquire = [math]::Round($utilFromFirst, 3); slotBusyFractionFromLaunch = [math]::Round($busyMs / $spanMs, 3); wallMs = [math]::Round($spanMs)
    landed = @($sa.tasksLanded, $sb.tasksLanded)
}
$partA | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ev 'concurrent-A.json')
Write-Host ("PART A: completed={0} suites={1} overlappingHolds={2} maxWaitMs={3} slotBusy(from first acquire)={4} landed={5}/{6}" -f $ok, $all.Count, $overlaps, $partA.maxWaitMs, $partA.slotBusyFractionFromFirstAcquire, $sa.tasksLanded, $sb.tasksLanded)

# ---------------- Part B: kill the holder, other reclaims ----------------
$stateB = Join-Path $Root 'st-kill'
if (Test-Path $stateB) { Remove-Item -LiteralPath $stateB -Recurse -Force }
$loadB = Load
$ex = @('--expiry-sec', $ExpirySec, '--heartbeat-sec', 1)
$pa = Start-Instance 'c2a' $stateB $ex; $pb = Start-Instance 'c2b' $stateB $ex
$lock = Join-Path $stateB 'slots\slot-0.lock'
$sw = [Diagnostics.Stopwatch]::StartNew(); $holderPid = $null
while (-not $holderPid -and $sw.Elapsed.TotalMinutes -lt 25) {
    Start-Sleep -Milliseconds 300
    if (Test-Path $lock) { try { $holderPid = (Get-Content $lock -Raw | ConvertFrom-Json).pid } catch { } }
}
Start-Sleep -Seconds 20   # holder is now inside `dotnet test`, heartbeating
$killAt = [DateTime]::UtcNow
taskkill /F /T /PID $holderPid | Out-Null
Write-Host "PART B: killed holder testgate pid $holderPid at $($killAt.ToString('o'))"
$ok = $pa.WaitForExit(1800000) -and $pb.WaitForExit(1800000)
if (-not $ok) { Write-Host 'DEADLOCK/TIMEOUT: killing'; $pa, $pb | ForEach-Object { if (-not $_.HasExited) { taskkill /F /T /PID $_.Id | Out-Null } } }
$ra = Read-Summary 'c2a'; $rb = Read-Summary 'c2b'
$rec = @(); foreach ($p in @(@('c2a', $ra), @('c2b', $rb))) { if ($p[1]) { foreach ($s in $p[1].suites) { $rec += [ordered]@{ inst = $p[0]; label = $s.label; waitMs = $s.waitMs; reclaimed = $s.reclaimed; acquiredUtc = ([DateTime]$s.acquiredUtc).ToString('o') } } } }
$partB = [ordered]@{
    loadPercentBefore = $loadB; expirySec = $ExpirySec; killedHolderPid = $holderPid; killedAtUtc = $killAt.ToString('o'); completed = $ok
    exitCodes = [ordered]@{ c2a = $pa.ExitCode; c2b = $pb.ExitCode }
    victimError = [ordered]@{ c2a = (Last-Err 'c2a'); c2b = (Last-Err 'c2b') }
    survivorSuites = $rec
    secondsFromKillToReclaim = ($rec | Where-Object { $_.reclaimed } | ForEach-Object { ([DateTime]$_.acquiredUtc).ToUniversalTime().Subtract($killAt).TotalSeconds } | Select-Object -First 1)
}
$partB | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ev 'concurrent-B.json')
Write-Host ("PART B: completed={0} exit c2a={1} c2b={2} reclaimed-after={3}s victim: {4} {5}" -f $ok, $pa.ExitCode, $pb.ExitCode, $partB.secondsFromKillToReclaim, $partB.victimError.c2a, $partB.victimError.c2b)
