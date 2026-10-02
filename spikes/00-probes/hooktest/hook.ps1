$in = [Console]::In.ReadToEnd()
Add-Content -Path "$PSScriptRoot\hook-fired.log" -Value ("{0} {1}" -f (Get-Date -Format o), $in)
'{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}'
