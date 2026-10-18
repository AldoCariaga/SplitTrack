# Publishes the app and builds its installer, out\installer\SplitTrack-Setup-<version>.exe, with Inno Setup 6.
# The version comes from <Version> in src\SplitTrack.App\SplitTrack.App.csproj.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'out\installer\publish'

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is missing. Install it with: winget install JRSoftware.InnoSetup --scope user' }

& (Join-Path $PSScriptRoot 'get-ffmpeg.ps1')

# A clean folder, so nothing stale from an earlier publish ends up in the installer.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root 'src\SplitTrack.App') -c Release -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

& $iscc /Q "/DAppDir=$publish" (Join-Path $root 'installer\SplitTrack.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)" }

Get-ChildItem (Join-Path $root 'out\installer') -Filter 'SplitTrack-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length / 1MB)) MB)" }
