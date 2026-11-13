# Removes Focus: unblocks everything, deletes the task, files, shortcuts and the
# Settings > Apps entry. Needs admin. Normally started by "Focus.exe --uninstall".
param([switch]$Quiet, [switch]$Notify)

$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Quiet) { $a += '-Quiet' }; if ($Notify) { $a += '-Notify' }
    try { Start-Process powershell -Verb RunAs -Wait -ArgumentList $a } catch { Write-Host 'Uninstall cancelled.' }
    exit
}

$dir = Join-Path $env:ProgramData 'FocusBlock'
Get-Process Focus -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$dir\Focus.exe" } | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path "$dir\engine.ps1") { & "$dir\engine.ps1" -Off }   # removes the hosts section
Unregister-ScheduledTask -TaskName FocusBlock -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Focus.lnk", "$env:PUBLIC\Desktop\Focus.lnk" -ErrorAction SilentlyContinue
Remove-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FocusBlock' -Recurse -ErrorAction SilentlyContinue
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue

if ($Notify) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('Focus has been removed and all sites are unblocked.', 'Focus') | Out-Null
}
if (-not $Quiet) { Write-Host 'Focus has been removed.'; Read-Host 'Press Enter to close' | Out-Null }
