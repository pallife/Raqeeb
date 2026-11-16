# One-time installer (self-elevating): copies scripts, registers the elevated on-demand "Raqeeb-Resume" task,
# enables wake timers, and adds the `rq` shortcut to the PowerShell profile.
param([string]$User = $env:USERNAME, [string]$Home2 = $env:USERPROFILE)
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Output 'Not running as Administrator. Open PowerShell with "Run as administrator" and run this script again.'
    return
}
$base = Join-Path $Home2 'AppData\Local\Raqeeb'; $dst = Join-Path $base 'scripts'
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'raqeeb-*.ps1') $dst -Force
$act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dst\raqeeb-resume.ps1`""
$pri = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 8)
Register-ScheduledTask -TaskName 'Raqeeb-Resume' -Action $act -Principal $pri -Settings $set -Force | Out-Null
powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d 1 | Out-Null
powercfg /setdcvalueindex SCHEME_CURRENT SUB_SLEEP bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d 1 | Out-Null
powercfg /setactive SCHEME_CURRENT | Out-Null
$prof = Join-Path $Home2 'Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1'
if (-not (Select-String -Path $prof -Pattern 'function rq ' -Quiet -EA 0)) {
    Add-Content $prof "`nfunction rq { & `"`$env:LOCALAPPDATA\Raqeeb\scripts\raqeeb-power.ps1`" @args }"
}
'installed' | Set-Content (Join-Path $base 'scheduler.installed')
