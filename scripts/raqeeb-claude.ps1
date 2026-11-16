# Starts `claude remote-control` (claude / claude2 account) in its own PowerShell window. No keystroke simulation.
# Requires the folder to be trusted once per account (run `claude` / `claude2` once in $Dir and accept the trust prompt).
param([ValidateSet('claude','claude2')][string]$Which = 'claude', [string]$Dir = $env:USERPROFILE)
$ErrorActionPreference = 'Stop'
$title = "RQ-$Which"
$name = "$env:COMPUTERNAME-$Which"
$cmd = "`$Host.UI.RawUI.WindowTitle='$title'; Set-Location -LiteralPath '$Dir'; "
if ($Which -eq 'claude2') { $cmd += "`$env:CLAUDE_CONFIG_DIR=(Join-Path `$HOME '.claude2'); " }
$cmd += "claude remote-control --name '$name'"
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
$p = Start-Process powershell.exe -ArgumentList '-NoLogo','-NoExit','-EncodedCommand',$enc -WindowStyle Minimized -PassThru
function ClaudeAlive { [bool](Get-CimInstance Win32_Process -Filter "Name='claude.exe' AND ParentProcessId=$($p.Id)" -EA 0) }
$t0 = Get-Date
while (-not (ClaudeAlive) -and ((Get-Date) - $t0).TotalSeconds -lt 60) { Start-Sleep -Seconds 1 }
if (-not (ClaudeAlive)) { Write-Output "claude remote-control did not start ($Which)"; exit 3 }
Start-Sleep -Seconds 10
if (-not (ClaudeAlive)) { Write-Output "claude remote-control exited early ($Which) - folder probably not trusted yet"; exit 4 }
Write-Output "$Which remote-control running (window $title, name $name)"
