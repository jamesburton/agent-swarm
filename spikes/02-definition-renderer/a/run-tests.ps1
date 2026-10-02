# THROWAWAY: renders the example (md + yaml), checks outputs, runs the four invalid-input cases.
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$ex = Get-Content ../epic-delivery.md -Raw
$out = Join-Path $env:TEMP 'spike2a'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet run render.cs -- ../epic-delivery.md "$out/md"; "md exit=$LASTEXITCODE"
dotnet run render.cs -- epic-delivery.yaml "$out/yaml"; "yaml exit=$LASTEXITCODE"
dotnet run render.cs -- --check "$out/md"; "check exit=$LASTEXITCODE"
"agents identical md vs yaml: " + (-not (Compare-Object (Get-ChildItem "$out/md/.claude" -Recurse -File | % { (Get-Content $_ -Raw) }) (Get-ChildItem "$out/yaml/.claude" -Recurse -File | % { (Get-Content $_ -Raw) })))
if (Get-Command node -ErrorAction SilentlyContinue) {
  foreach ($d in 'md', 'yaml') { node --check (Get-ChildItem "$out/$d/.claude/workflows/*.js").FullName; "node --check $d exit=$LASTEXITCODE" }
} else { 'node not available' }

$cases = [ordered]@{
  'bad-model'       = $ex -replace 'model: haiku', 'model: gpt5'
  'duplicate-role'  = $ex + "`n## worker  (llm)`nmodel: haiku`n"
  'missing-model'   = $ex -replace '(?m)^model: haiku\r?\n', ''
  'missing-section' = $ex -replace '(?m)^## orchestrator  \(code\)', '' -replace '(?m)^flow: .*\r?\n', ''
  'extra-section'   = $ex + "`n## Notes`nsome notes`n"
}
New-Item -ItemType Directory -Force tests | Out-Null
foreach ($k in $cases.Keys) {
  $f = "tests/$k.md"; Set-Content $f $cases[$k] -NoNewline
  $o = "$out/$k"
  $msg = dotnet run render.cs -- $f $o 2>&1
  $code = $LASTEXITCODE
  $n = if (Test-Path $o) { (Get-ChildItem $o -Recurse -File).Count } else { 0 }
  "$k : exit=$code lines=$(@($msg).Count) files=$n msg=$msg"
}
