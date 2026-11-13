# Command-line control for Focus.
#   focus.ps1 75       block for 75 minutes (5-240)
#   focus.ps1 status   show the current session
#   focus.ps1 stop     end early (asks you to type a sentence)
param([string]$cmd = 'status')
$dir = Join-Path $env:ProgramData 'FocusBlock'
$req = Join-Path $dir 'request.txt'
$phrase = 'I am choosing to stop this session early'

function Kick { try { Start-ScheduledTask -TaskName FocusBlock -ErrorAction Stop; Start-Sleep 3 } catch { Write-Host '(applies within a minute)' } }
function EndTime {
    if (-not (Test-Path $req)) { return $null }
    $first = Get-Content $req | Where-Object { $_.Trim() } | Select-Object -First 1
    $dt = [datetime]::MinValue
    if ($first -and [datetime]::TryParse($first.Trim(), [ref]$dt) -and $dt -gt (Get-Date)) { return $dt }
    return $null
}

switch -regex ($cmd) {
    '^\d+$' {
        $m = [int]$cmd
        if ($m -lt 5 -or $m -gt 240) { Write-Host 'Minutes must be 5-240'; exit 1 }
        $now = Get-Date; $end = $now.AddMinutes($m)
        Set-Content $req @($end.ToString('s'), $now.ToString('s'))
        Kick
        Write-Host "Focusing until $($end.ToString('HH:mm'))."
    }
    '^status$' {
        $end = EndTime
        if ($end) { Write-Host "Focusing until $($end.ToString('HH:mm')) ($([int][math]::Ceiling(($end - (Get-Date)).TotalMinutes)) min left)." }
        else { Write-Host 'Not focusing.' }
    }
    '^stop$' {
        if (-not (EndTime)) { Write-Host 'Not focusing.'; exit 0 }
        Write-Host "Type exactly: $phrase"
        if ((Read-Host) -ne $phrase) { Write-Host 'Not matched. Still focusing.'; exit 1 }
        Set-Content $req ''
        Kick
        Write-Host 'Stopped.'
    }
    default { Write-Host 'Usage: focus.ps1 <minutes> | status | stop' }
}
