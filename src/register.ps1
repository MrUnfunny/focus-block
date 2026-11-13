# Called (elevated) by "FocusSetup.exe --install" after it has copied the app and
# scripts into $Dir. Sets permissions, the scheduled task, shortcuts and the
# Settings > Apps entry.
param([Parameter(Mandatory)][string]$Dir, [string]$Version = '1.0.0', [switch]$NoDesktop)

$ErrorActionPreference = 'Stop'
try {
    # Scripts and the app are admin-only. The session file and the block list are
    # data files normal users may edit (engine.ps1 validates them).
    icacls $Dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    icacls "$Dir\request.txt" /grant '*S-1-5-32-545:M' | Out-Null
    icacls "$Dir\sites.txt" /grant '*S-1-5-32-545:M' | Out-Null
    if (-not (Test-Path "$Dir\history.csv")) { Set-Content "$Dir\history.csv" 'start,end,planned_min,final_min,actual_min,outcome' }
    icacls "$Dir\history.csv" /grant '*S-1-5-32-545:M' | Out-Null

    # Task that applies / removes the block every minute and at startup
    $act  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$Dir\engine.ps1`""
    $t1   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $t2   = New-ScheduledTaskTrigger -AtStartup
    $prin = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $set  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName FocusBlock -Description 'Focus: blocks distracting sites during a focus session' -Action $act -Trigger $t1, $t2 -Principal $prin -Settings $set -Force | Out-Null

    # Let normal users start the task so a session applies instantly
    $svc = New-Object -ComObject Schedule.Service; $svc.Connect()
    $task = $svc.GetFolder('\').GetTask('FocusBlock')
    $sd = $task.GetSecurityDescriptor(4)
    if ($sd -notmatch '\(A;;GRGX;;;AU\)') { $task.SetSecurityDescriptor($sd + '(A;;GRGX;;;AU)', 0) }

    # Shortcuts for all users
    $ws = New-Object -ComObject WScript.Shell
    $links = @("$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Focus.lnk")
    if (-not $NoDesktop) { $links += "$env:PUBLIC\Desktop\Focus.lnk" }
    foreach ($p in $links) {
        $l = $ws.CreateShortcut($p); $l.TargetPath = "$Dir\Focus.exe"; $l.IconLocation = "$Dir\Focus.exe,0"
        $l.Description = 'Block distracting sites for a while'; $l.Save()
    }

    # Settings > Apps entry
    $key = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FocusBlock'
    New-Item $key -Force | Out-Null
    $vals = @{
        DisplayName = 'Focus'; DisplayVersion = $Version; Publisher = 'Focus'
        DisplayIcon = "$Dir\Focus.exe,0"; InstallLocation = $Dir
        UninstallString = "`"$Dir\Focus.exe`" --uninstall"
    }
    foreach ($k in $vals.Keys) { Set-ItemProperty $key $k $vals[$k] }
    Set-ItemProperty $key NoModify 1 -Type DWord; Set-ItemProperty $key NoRepair 1 -Type DWord
    Set-ItemProperty $key EstimatedSize 200 -Type DWord

    Start-ScheduledTask -TaskName FocusBlock
    exit 0
} catch {
    Add-Content "$Dir\install.log" "$(Get-Date -Format s) register failed: $_"
    exit 1
}
