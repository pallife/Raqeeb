# Runs elevated after wake/boot: Wi-Fi -> internet -> Raqeeb agent -> wait for unlocked desktop -> claude wizard(s).
param([string]$Wifi = '!AHMED.ALI-5G')
$base = Join-Path $env:LOCALAPPDATA 'Raqeeb'; $log = Join-Path $base 'logs\resume.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
function Log($m) { "$(Get-Date -Format 's') $m" | Add-Content -Path $log -Encoding UTF8 }
$pf = Join-Path $base 'pending.json'
$pend = if (Test-Path $pf) { Get-Content $pf -Raw | ConvertFrom-Json } else { $null }
Log "resume start pending=$([bool]$pend)"
function Online { try { (Test-Connection 1.1.1.1 -Count 1 -Quiet) } catch { $false } }
$t0 = Get-Date
while (-not (Online) -and ((Get-Date) - $t0).TotalSeconds -lt 150) {
    $cur = (netsh wlan show interfaces | Select-String '^\s*SSID\s*:\s*(.+)$' | Select-Object -First 1)
    if (-not $cur -or $cur.Matches[0].Groups[1].Value.Trim() -ne $Wifi) { netsh wlan connect name="$Wifi" | Out-Null; Log "wifi connect $Wifi" }
    Start-Sleep -Seconds 6
}
Log "online=$(Online)"
if (-not (Get-Process Raqeeb -EA 0)) { Start-Process explorer.exe (Join-Path $base 'Raqeeb.exe'); Log 'started Raqeeb.exe' }
Get-ScheduledTask -TaskName 'Raqeeb-Wake-*' -EA 0 | Unregister-ScheduledTask -Confirm:$false -EA 0
if ($pend -and $pend.claude -and $pend.claude -ne 'none') {
    $t0 = Get-Date   # wait up to 6h for the user to unlock (from the phone via Raqeeb helper, or by typing the password)
    while (((Get-Date) - $t0).TotalHours -lt 6 -and ((Get-Process LogonUI -EA 0) -or -not (Get-Process explorer -EA 0))) { Start-Sleep -Seconds 10 }
    Start-Sleep -Seconds 8
    $list = if ($pend.claude -eq 'both') { 'claude','claude2' } else { ,$pend.claude }
    foreach ($w in $list) { Log "wizard $w"; try { Log (& (Join-Path $PSScriptRoot 'raqeeb-claude.ps1') -Which $w | Out-String) } catch { Log "wizard failed: $_" }; Start-Sleep 5 }
}
Remove-Item $pf -EA 0
Log 'resume done'
