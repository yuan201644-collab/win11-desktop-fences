# Installs the desktop media controller into a stable per-user directory.
#
#   .\deploy-controller.ps1              # publish, install, start
#   .\deploy-controller.ps1 -NoRestart   # publish and install only
#
# Why an install directory instead of pointing the shortcut at bin\Debug: this widget is meant to
# start at sign-in and normally lives in the tray, and its desktop shortcut is the only way back to a
# hidden card. Both have to keep working while the code is being edited, and a Debug build cannot: it
# is file-locked while the app runs (so `dotnet build` fails) and it vanishes on `dotnet clean` (so
# the shortcut breaks). The install directory lives outside the repository, where neither git nor
# MSBuild looks. Because the path never changes, a later redeploy cannot invalidate the shortcut or
# the sign-in entry - both keep pointing at the right executable.
#
# Framework-dependent on purpose. Self-contained single-file was measured at 187 MB and unpacks its
# native libraries into %TEMP%\.net on every version change, for a folder we copy anyway; the
# framework-dependent build is 25 MB (24.9 MB of it Microsoft.Windows.SDK.NET.dll, which is what the
# WinRT/SMTC projection needs) and starts without unpacking. The cost is a dependency on the .NET 9
# Desktop Runtime, which this machine has via its SDK.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads .ps1 files as ANSI unless they carry a BOM, so
# any non-ASCII character in this file would be at the mercy of the file's encoding.
param([switch]$NoRestart)

$ErrorActionPreference = "Stop"

# Environment variables can be absent in a stripped-down shell; the folder API cannot.
$root       = $PSScriptRoot
$project    = Join-Path $root "src\DesktopMediaController"
$localApp   = [Environment]::GetFolderPath('LocalApplicationData')
$installDir = Join-Path $localApp "DesktopMediaController\app"
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) ("dmc-publish-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$exeName    = "DesktopMediaController.exe"

Write-Host ">>> Stopping the running widget ..." -ForegroundColor Cyan
Get-Process -Name "DesktopMediaController" -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Milliseconds 900

Write-Host ">>> Publishing (Release, framework-dependent) ..." -ForegroundColor Cyan
# Resolved before use so a failure says what is wrong. Without this a missing dotnet raises a raw
# "term not recognized" that reads like a script bug. PATH is tried first; the SDK's default install
# location covers shells that were spawned with a bare environment.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) "dotnet\dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    Write-Host "!!! dotnet not found on PATH and not at the default install location." -ForegroundColor Red
    Write-Host "    Install the .NET SDK, or run this from a Developer PowerShell." -ForegroundColor Red
    exit 1
}
& $dotnet publish $project -c Release --self-contained false -o $stagingDir
if ($LASTEXITCODE -ne 0) {
    # exit 1 rather than exit $LASTEXITCODE: an unset code would become 0, i.e. a failed publish
    # reporting success.
    Write-Host "!!! dotnet publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

$stagedExe = Join-Path $stagingDir $exeName
if (-not (Test-Path $stagedExe)) {
    Write-Host "!!! publish produced no exe: $stagedExe" -ForegroundColor Red
    exit 1
}

Write-Host ">>> Installing into $installDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
# Overwrite in place instead of wiping the directory first: no recursive delete is needed here, and
# there is no reason to run one on the user's machine.
Copy-Item (Join-Path $stagingDir "*") $installDir -Force

# Same version stamp the organizer's publish.ps1 writes, so the deployed build can be identified.
$version = "unknown"
try {
    $hash = (git -C $root rev-parse --short HEAD 2>$null).Trim()
    if ($hash) { $version = "$hash @ $(Get-Date -Format 'yyyy-MM-dd HH:mm')" }
} catch { }
$version | Out-File -FilePath (Join-Path $installDir "VERSION.txt") -Encoding utf8 -NoNewline

$installedExe = Join-Path $installDir $exeName
$sizeMb = [math]::Round(((Get-ChildItem $installDir -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
Write-Host ">>> Installed: $installedExe ($sizeMb MB, $version)" -ForegroundColor Green

Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

if ($NoRestart) {
    Write-Host ">>> -NoRestart given: not starting the widget." -ForegroundColor DarkGray
    exit 0
}

Write-Host ">>> Starting the widget ..." -ForegroundColor Cyan
Start-Process -FilePath $installedExe
