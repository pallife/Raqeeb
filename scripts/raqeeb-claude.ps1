# Opens PowerShell running claude / claude2, accepts the trust prompt and types /rc (remote control).
param([ValidateSet('claude','claude2')][string]$Which = 'claude', [string]$Dir = $env:USERPROFILE, [int]$StartupSec = 14)
$ErrorActionPreference = 'Stop'
$title = "RQ-$Which-" + (Get-Date -Format 'HHmmss')
$cmd = "`$Host.UI.RawUI.WindowTitle='$title'; Set-Location -LiteralPath '$Dir'; "
if ($Which -eq 'claude2') { $cmd += "`$env:CLAUDE_CONFIG_DIR=(Join-Path `$HOME '.claude2'); " }
$cmd += "claude"
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
$p = Start-Process powershell.exe -ArgumentList '-NoLogo','-NoExit','-EncodedCommand',$enc -PassThru
Start-Sleep -Seconds $StartupSec
Add-Type -AssemblyName Microsoft.VisualBasic, System.Windows.Forms
function Focus { for ($i=0; $i -lt 6; $i++) { try { [Microsoft.VisualBasic.Interaction]::AppActivate($p.Id); return $true } catch { try { [Microsoft.VisualBasic.Interaction]::AppActivate($title); return $true } catch { Start-Sleep 1 } } }; $false }
if (-not (Focus)) { Write-Output "could not focus window $title"; exit 2 }
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')   # accepts "trust this folder" if shown; harmless on an empty prompt
Start-Sleep -Seconds 5
[void](Focus)
foreach ($ch in '/rc'.ToCharArray()) { [System.Windows.Forms.SendKeys]::SendWait([string]$ch); Start-Sleep -Milliseconds 150 }
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Write-Output "$Which started with /rc (window $title)"
