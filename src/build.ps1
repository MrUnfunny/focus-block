# Builds dist\FocusSetup.exe: the app with its scripts and default block list
# embedded. Uses the C# compiler that ships with Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$fw = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319' }

New-Item -ItemType Directory -Force "$repo\dist" | Out-Null
$out = "$repo\dist\FocusSetup.exe"
$refs = 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'System.Xaml.dll', 'System.Core.dll' |
    ForEach-Object { "/r:$fw\$_" }
$res = @{
    'engine.ps1' = 'src\engine.ps1'; 'focus.ps1' = 'src\focus.ps1'; 'register.ps1' = 'src\register.ps1'
    'uninstall.ps1' = 'src\uninstall.ps1'; 'sites.txt' = 'sites.txt'; 'icon.png' = 'assets\icon.png'
}
$resArgs = foreach ($k in $res.Keys) { "/resource:$repo\$($res[$k]),$k" }

& "$fw\csc.exe" /nologo /target:winexe /optimize+ "/out:$out" "/win32icon:$repo\assets\focus.ico" @refs @resArgs "$repo\src\Focus.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $out"
