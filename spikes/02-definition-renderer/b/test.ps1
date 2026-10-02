# Throwaway checks for spike 2B. Run from this folder: pwsh ./test.ps1
$here = $PSScriptRoot; $good = Join-Path $here 'epic-delivery.md'
$tmp = Join-Path ([IO.Path]::GetTempPath()) 'spike2b'; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; New-Item $tmp -ItemType Directory | Out-Null
$src = Get-Content $good -Raw
$cases = [ordered]@{
  'unknown-model'     = $src.Replace('## worker  (haiku)', '## worker  (gpt5)')
  'duplicate-role'    = $src + "`n## worker  (haiku)`n`ndescription: dup`n`nprompt`n"
  'missing-model'     = $src.Replace('## reviewer  (sonnet)', '## reviewer')
  'missing-section'   = $src.Replace('## orchestrator  (code)', '## gate extra-gate').Replace('stages:', 'x:')
  'extra-section'     = $src + "`n## Notes and ideas`n`nfree text`n"
}
$fail = 0
foreach ($c in $cases.Keys) {
  $in = Join-Path $tmp "$c.md"; $out = Join-Path $tmp "out-$c"; Set-Content $in $cases[$c]
  $msg = dotnet run (Join-Path $here 'render.cs') -- $in $out 2>&1 | Where-Object { $_ -notmatch 'Compiler server' }
  $ok = ($LASTEXITCODE -ne 0) -and (-not (Test-Path $out)) -and (@($msg).Count -eq 1)
  "{0,-16} {1}  exit={2}  files={3}  msg={4}" -f $c, ($ok ? 'PASS' : 'FAIL'), $LASTEXITCODE, (Test-Path $out), ($msg -join ' | ')
  if (-not $ok) { $fail++ }
}
"invalid cases failed: $fail"
