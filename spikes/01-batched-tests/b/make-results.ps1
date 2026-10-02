# THROWAWAY SPIKE 1B: compose results.json (plan results contract) from evidence/*.json. Run: pwsh -File make-results.ps1
$ev = Join-Path $PSScriptRoot 'evidence'
function J($n) { Get-Content (Join-Path $ev $n) -Raw | ConvertFrom-Json }
$sv = J 'serial-vs-batched.json'; $conf = J 'conf-prebatch.json'; $ovp = J 'ov-prebatch.json'; $ovn = J 'ov-naive4.json'
$ca = J 'concurrent-A.json'; $cb = J 'concurrent-B.json'; $edge = J 'edge-cases.json'
$f1 = J 'regress-f1.json'; $f2 = J 'regress-f2.json'
$m = [ordered]@{}; $b = [ordered]@{}
# 3. serial baseline measured (f0)
$m['f0.batched.fullSuiteRuns'] = $sv.'batched-f0'.summary.fullSuiteRuns
$m['f0.batched.wallSeconds'] = $sv.'batched-f0'.wallSecondsOuter
$m['f0.batched.loadPercentBefore'] = $sv.'batched-f0'.loadPercentBefore
$m['f0.singleWarmSuiteSec'] = [math]::Round($sv.warm.summary.singleSuiteMs / 1000, 1)
$b['f0.serial.fullSuiteRuns'] = $sv.'serial-f0'.summary.fullSuiteRuns
$b['f0.serial.wallSeconds'] = $sv.'serial-f0'.wallSecondsOuter
$b['f0.serial.loadPercentBefore'] = $sv.'serial-f0'.loadPercentBefore
$m['f0.wallSpeedupMeasured'] = [math]::Round($sv.'serial-f0'.wallSecondsOuter / $sv.'batched-f0'.wallSecondsOuter, 2)
# bisect regression with the changed batch.cs
foreach ($p in @(@('f1', $f1), @('f2', $f2))) {
    $m["$($p[0]).batched.fullSuiteRuns"] = $p[1].fullSuiteRuns; $m["$($p[0]).batched.bisectRuns"] = $p[1].bisectRuns
    $m["$($p[0]).batched.inferredRedSkipped"] = $p[1].inferredRedSkipped; $m["$($p[0]).batched.wallSeconds"] = $p[1].wallSeconds
    $m["$($p[0]).rejected"] = (($p[1].rejected | ForEach-Object id) -join ','); $b["$($p[0]).serial.fullSuiteRuns"] = 12
}
# 1. conflict path
foreach ($p in @(@('conf.prebatch', $conf), @('ov.prebatch', $ovp), @('ov.naiveFixed4', $ovn))) {
    $k = $p[0]; $s = $p[1]
    $m["$k.fullSuiteRuns"] = $s.fullSuiteRuns; $m["$k.batches"] = $s.batches; $m["$k.tasksLanded"] = $s.tasksLanded; $m["$k.returned"] = $s.returned
    $m["$k.rebasedAndLanded"] = $s.rebasedAndLanded; $m["$k.needsWorker"] = $s.needsWorker; $m["$k.overlapPairs"] = $s.overlapPairs
    $m["$k.overlapPairsSameBatchFirstPlacement"] = $s.overlapPairsSameBatchFirstPlacement; $m["$k.wallSeconds"] = $s.wallSeconds
}
$m['conf.touchesDerivedFromGitWithBogusTasksJson'] = $true
# 4. concurrency
$m['concurrent.suiteRuns'] = $ca.suiteRuns; $m['concurrent.overlappingHolds'] = $ca.overlappingHolds; $m['concurrent.maxWaitMs'] = $ca.maxWaitMs
$m['concurrent.slotBusyFraction'] = $ca.slotBusyFractionFromFirstAcquire; $m['concurrent.deadlock'] = (-not $ca.completed)
$m['killedHolder.reclaimed'] = [bool]($cb.survivorSuites | Where-Object reclaimed)
$m['killedHolder.secondsFromKillToReclaim'] = $cb.secondsFromKillToReclaim; $m['killedHolder.expirySec'] = $cb.expirySec
# 5. edge cases
$m['edge.casesPassed'] = @($edge | Where-Object result -eq 'PASS').Count; $m['edge.casesTotal'] = @($edge).Count
$verdict = if ($m['edge.casesPassed'] -eq $m['edge.casesTotal'] -and $m['killedHolder.reclaimed'] -and $m['concurrent.overlappingHolds'] -eq 0) { 'works' } else { 'partial' }
$notes = @(
    'Wall times are from a shared ~85-100% CPU host: compare COUNTS first. LoadPercentage was 100 before serial and 84 before batched, so batched was slightly favoured.',
    'Pre-batching separates same-file tasks (4 of 4 pairs) but does NOT prevent textual conflicts and did not reduce suite runs on the overlap sandbox (4 vs 4); it moves a stacked task''s conflict from land time to merge time.',
    'Rebase is clean only for a stacked task (parent already squash-landed); same-line conflicts conflict again and go to needs-worker. Rebases run on a copy ref rebased/<epic>/<task>.',
    'Not verified: real repo, real overlapping edits from LLM workers, >2 concurrent gates, two simultaneous reclaimers, rebase of multi-commit tasks with real semantic conflicts.'
)
$r = [ordered]@{
    spike = '01'; variant = 'b'
    question = 'Does adaptive batch size + derived-touches pre-batching + halving bisect isolate culprits with fewer full-suite runs than serial, and do conflicts, concurrency and failure edges behave?'
    metrics = $m; baseline = $b; verdict = $verdict; notes = $notes
    howToRun = 'pwsh -File serial-vs-batched.ps1 | conflicts-overlap.ps1 -Which conf|ov | concurrent.ps1 | regress.ps1 | edge-cases.ps1 ; then make-results.ps1 (sandboxes: sandbox-gen.cs --projects 6 --tests-per 10 --delay-ms 100 --seed 1 --tasks 12 [--failing N | --conflicts K --overlap K --stacked K])'
}
$r | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $PSScriptRoot 'results.json')
"results.json written ($($m.Count) metrics, verdict $verdict)"
