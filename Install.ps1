<#
    ThrottleView installer (no administrator rights required).

    Copies ThrottleView.exe into %LOCALAPPDATA%\ThrottleView, adds a per-user startup entry that
    launches into the tray, and starts the app. Run it by right-clicking and choosing "Run with
    PowerShell", or from a console:

        powershell -ExecutionPolicy Bypass -File .\Install.ps1
#>

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:LOCALAPPDATA 'ThrottleView'
$ExeName    = 'ThrottleView.exe'
$LibName    = 'LibreHardwareMonitorLib.dll'

function Find-Source($name) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $candidates = @(
        (Join-Path $here $name),
        (Join-Path $here "ThrottleView\bin\x64\Release\$name"),
        (Join-Path $here "ThrottleView\bin\x64\Debug\$name")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

Write-Host 'Installing ThrottleView...' -ForegroundColor Cyan

# 1. Stop any running instance so the file is not locked.
Get-Process -Name 'ThrottleView' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# 2. Copy the executable into the per-user install folder.
$src = Find-Source $ExeName
if (-not $src) { throw "Could not find $ExeName. Build Release|x64 in Visual Studio first (Ctrl+Shift+B), or place $ExeName next to this script." }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force -Path $src -Destination (Join-Path $InstallDir $ExeName)
$exe = Join-Path $InstallDir $ExeName

# 3. Copy the optional sensor library if it is present, for accurate temperatures.
$lib = Find-Source $LibName
if ($lib) {
    Copy-Item -Force -Path $lib -Destination (Join-Path $InstallDir $LibName)
    Write-Host "Bundled $LibName for hardware temperatures." -ForegroundColor DarkGray
}

# 4. Start with Windows, launching into the tray.
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ThrottleView' -Value "`"$exe`" --tray"

# 5. Launch now.
Start-Process -FilePath $exe
Write-Host "Installed to $InstallDir and started. Find it in your system tray." -ForegroundColor Green
