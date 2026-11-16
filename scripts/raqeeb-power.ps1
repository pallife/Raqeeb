# Scheduled power actions + automatic resume.
#  rq -Do hibernate -In 2h -WakeIn 5h -Claude both     off in 2h, back 5h later, then Wi-Fi + claude/claude2 + /rc
#  rq -Do restart  -In 30m -Claude claude2             restart in 30 min, then start claude2 after sign-in
#  rq -Do sleep -In 23:30 -WakeIn 7h                   -In accepts 90s / 30m / 2h / 1d / HH:mm
#  rq -Do list | rq -Do cancel
param([ValidateSet('shutdown','restart','sleep','hibernate','list','cancel')][string]$Do = 'list',
      [string]$In = '0m', [string]$WakeIn = '', [ValidateSet('none','claude','claude2','both')][string]$Claude = 'none')
$base = Join-Path $env:LOCALAPPDATA 'Raqeeb'; $pf = Join-Path $base 'pending.json'
function ToSpan([string]$s) {
    if ($s -match '^(\d+)\s*([smhd]?)$') { $n=[double]$Matches[1]; switch ($Matches[2]) { 's' {return [TimeSpan]::FromSeconds($n)} 'h' {return [TimeSpan]::FromHours($n)} 'd' {return [TimeSpan]::FromDays($n)} default {return [TimeSpan]::FromMinutes($n)} } }
    if ($s -match '^(\d{1,2}):(\d{2})$') { $t=(Get-Date).Date.AddHours([int]$Matches[1]).AddMinutes([int]$Matches[2]); if ($t -le (Get-Date)) { $t=$t.AddDays(1) }; return $t - (Get-Date) }
    throw "bad time: $s"
}
if ($Do -eq 'list') {
    Get-ScheduledTask -TaskName 'Raqeeb-*' -EA 0 | ForEach-Object { $i = $_ | Get-ScheduledTaskInfo; '{0,-34} {1,-9} next={2}' -f $_.TaskName,$_.State,$i.NextRunTime }
    if (Test-Path $pf) { 'pending: ' + (Get-Content $pf -Raw) }
    return
}
if ($Do -eq 'cancel') { Get-ScheduledTask -TaskName 'Raqeeb-Power-*','Raqeeb-Wake-*' -EA 0 | Unregister-ScheduledTask -Confirm:$false; Remove-Item $pf -EA 0; shutdown /a 2>$null | Out-Null; 'cancelled'; return }

$when = (Get-Date) + (ToSpan $In); $act = $Do
$wake = $null
if ($WakeIn) {
    $wake = $when + (ToSpan $WakeIn)
    if ($Do -eq 'shutdown') { $act = 'hibernate'; Write-Output 'note: a full shutdown cannot be woken by software; using hibernate (state saved, same effect).' }
}
$id = Get-Date -Format 'yyyyMMddHHmmss'
New-Item -ItemType Directory -Force $base | Out-Null
@{ claude = $Claude; id = $id; wakeAt = "$wake" } | ConvertTo-Json | Set-Content $pf -Encoding UTF8
$exe = switch ($act) { 'sleep' {'rundll32.exe'} default {'shutdown.exe'} }
$arg = switch ($act) { 'hibernate' {'/h'} 'restart' {'/r /t 20 /c "Raqeeb scheduled restart"'} 'shutdown' {'/s /t 20 /c "Raqeeb scheduled shutdown"'} 'sleep' {'powrprof.dll,SetSuspendState 0,1,0'} }
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -WakeToRun
$me = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
Get-ScheduledTask -TaskName 'Raqeeb-Power-*','Raqeeb-Wake-*' -EA 0 | Unregister-ScheduledTask -Confirm:$false
Register-ScheduledTask -TaskName "Raqeeb-Power-$id" -Principal $me -Settings $set -Trigger (New-ScheduledTaskTrigger -Once -At $when) -Action (New-ScheduledTaskAction -Execute $exe -Argument $arg) | Out-Null
$resume = New-ScheduledTaskAction -Execute 'schtasks.exe' -Argument '/run /tn Raqeeb-Resume'
if ($wake) {
    Register-ScheduledTask -TaskName "Raqeeb-Wake-$id" -Principal $me -Settings $set -Trigger (New-ScheduledTaskTrigger -Once -At $wake) -Action $resume | Out-Null
} elseif ($Do -eq 'restart' -or $Claude -ne 'none') {
    Register-ScheduledTask -TaskName "Raqeeb-Wake-$id" -Principal $me -Settings $set -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME) -Action $resume | Out-Null
}
$msg = "$act at $($when.ToString('yyyy-MM-dd HH:mm'))"
if ($wake) { $msg += "; wake + resume at $($wake.ToString('yyyy-MM-dd HH:mm'))" }
if ($Claude -ne 'none') { $msg += "; then $Claude + /rc" }
$msg
