# Focus engine. Runs every minute as SYSTEM via the "FocusBlock" scheduled task.
# While request.txt holds a future end time, the domains in sites.txt are pointed
# at 0.0.0.0 in the hosts file, inside a marked section. Everything else in the
# hosts file is left untouched.
param([switch]$Off)

$ErrorActionPreference = 'Stop'
$dir   = Join-Path $env:ProgramData 'FocusBlock'
$hosts = Join-Path $env:windir 'System32\drivers\etc\hosts'
$req   = Join-Path $dir 'request.txt'
$sites = Join-Path $dir 'sites.txt'
$S = '# FocusBlockStart'
$E = '# FocusBlockEnd'
$MaxMinutes = 245

function Log($m) { try { Add-Content (Join-Path $dir 'log.txt') "$(Get-Date -Format s) $m" } catch {} }

# 1. Is a session active? request.txt: line 1 = end time, line 2 = start time
$want = $false
$now = Get-Date
$end = [datetime]::MinValue
$start = [datetime]::MinValue
$reqLines = @()
if (Test-Path $req) { $reqLines = @(Get-Content $req | Where-Object { $_.Trim() }) }
if ($reqLines.Count -ge 1 -and [datetime]::TryParse($reqLines[0].Trim(), [ref]$end)) {
    if ($reqLines.Count -lt 2 -or -not [datetime]::TryParse($reqLines[1].Trim(), [ref]$start)) { $start = (Get-Item $req).LastWriteTime }
    $want = (-not $Off) -and ($end -gt $now) -and ($end -lt $now.AddMinutes($MaxMinutes))
}

# 1b. Session history. current.txt tracks the running session:
#     start | first planned end | latest end. When it ends, one row goes to history.csv.
$cur  = Join-Path $dir 'current.txt'
$hist = Join-Path $dir 'history.csv'
function Finish($c, $outcome, $actualEnd) {
    $plannedMin = [int][math]::Round(($c.Planned - $c.Start).TotalMinutes)
    $finalMin   = [int][math]::Round(($c.End - $c.Start).TotalMinutes)
    $actualMin  = [int][math]::Round(($actualEnd - $c.Start).TotalMinutes)
    if ($actualMin -lt 0) { $actualMin = 0 }
    if (-not (Test-Path $hist) -or -not (Get-Content $hist -TotalCount 1)) { Set-Content $hist 'start,end,planned_min,final_min,actual_min,outcome' }
    Add-Content $hist ('{0},{1},{2},{3},{4},{5}' -f $c.Start.ToString('s'), $actualEnd.ToString('s'), $plannedMin, $finalMin, $actualMin, $outcome)
    Remove-Item $cur -ErrorAction SilentlyContinue
    Log "session $outcome ($actualMin of $finalMin min)"
}
try {
    $c = $null
    if (Test-Path $cur) {
        $p = (Get-Content $cur -Raw).Trim().Split('|')
        if ($p.Count -eq 3) { $c = @{ Start = [datetime]$p[0]; Planned = [datetime]$p[1]; End = [datetime]$p[2] } }
    }
    if ($want) {
        if ($c -and $c.Start.ToString('s') -ne $start.ToString('s')) { Finish $c 'completed' $(if ($c.End -lt $now) { $c.End } else { $now }); $c = $null }
        if (-not $c) {
            Set-Content $cur ('{0}|{1}|{1}' -f $start.ToString('s'), $end.ToString('s'))
        } elseif ($c.End.ToString('s') -ne $end.ToString('s')) {
            Set-Content $cur ('{0}|{1}|{2}' -f $c.Start.ToString('s'), $c.Planned.ToString('s'), $end.ToString('s'))
        }
    } elseif ($c) {
        if ($reqLines.Count -eq 0 -or $Off) { Finish $c 'stopped' $(if ($Off) { $now } else { (Get-Item $req).LastWriteTime }) }
        else { Finish $c 'completed' $(if ($end -gt $c.Start) { $end } else { $c.End }) }
    }
} catch { Log "history error: $_" }

# 2. What the block section should contain
$desired = @()
if ($want -and (Test-Path $sites)) {
    $domains = Get-Content $sites |
        ForEach-Object { ($_ -replace '#.*$', '').Trim().ToLower() } |
        Where-Object { $_ -match '^[a-z0-9.-]+\.[a-z]{2,}$' }
    $all = @(foreach ($d in $domains) {
        $d
        if (-not $d.StartsWith('www.') -and $d.Split('.').Count -eq 2) { "www.$d" }
    })
}

# 3. What it contains now
$lines = [IO.File]::ReadAllLines($hosts)
if ($want) {
    # Sites removed from the list mid-session stay blocked until the session ends.
    $inSection = $false
    $prev = foreach ($l in $lines) {
        if ($l -eq $S) { $inSection = $true; continue }
        if ($l -eq $E) { $inSection = $false; continue }
        if ($inSection) { ($l -split '\s+')[1] }
    }
    foreach ($d in (@($all) + @($prev) | Where-Object { $_ } | Select-Object -Unique)) { $desired += "0.0.0.0 $d"; $desired += ":: $d" }
}
$keep = New-Object System.Collections.Generic.List[string]
$current = @()
$inside = $false
foreach ($l in $lines) {
    if ($l -eq $S) { $inside = $true; continue }
    if ($l -eq $E) { $inside = $false; continue }
    if ($inside) { $current += $l } else { $keep.Add($l) }
}
$hasSection = $lines -contains $S

if ($want) {
    if ($hasSection -and (($current -join "`n") -eq ($desired -join "`n"))) { exit 0 }
} elseif (-not $hasSection) { exit 0 }

# 4. Rewrite
if ($want) { $keep.Add($S); foreach ($d in $desired) { $keep.Add($d) }; $keep.Add($E) }
for ($i = 0; $i -lt 10; $i++) {
    try { [IO.File]::WriteAllLines($hosts, $keep.ToArray(), (New-Object Text.ASCIIEncoding)); break }
    catch { Start-Sleep -Milliseconds 300; if ($i -eq 9) { Log "write failed: $_"; exit 1 } }
}
ipconfig /flushdns | Out-Null
Log $(if ($want) { "block on ($($desired.Count / 2) domains)" } else { 'block off' })
