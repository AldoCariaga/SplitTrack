# Downloads the FFmpeg shared build that FFmpeg.AutoGen is bound against into third_party\ffmpeg.
# The binding's major.minor (Directory.Packages.props) must equal this build's, so change both together.
[CmdletBinding()]
param(
    [string]$Branch = '9.0',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'third_party\ffmpeg'
$stampFile = Join-Path $target 'VERSION.txt'
$name = "ffmpeg-n$Branch-latest-win64-gpl-shared-$Branch"
$baseUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest'

if ((Test-Path $stampFile) -and -not $Force) {
    Write-Host "FFmpeg already present: $(Get-Content $stampFile -TotalCount 1). Use -Force to replace it."
    return
}

$temp = Join-Path ([IO.Path]::GetTempPath()) "splittrack-ffmpeg-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $temp | Out-Null
try {
    $zip = Join-Path $temp "$name.zip"
    Write-Host "Downloading $name.zip ..."
    Invoke-WebRequest "$baseUrl/$name.zip" -OutFile $zip
    $sums = (Invoke-WebRequest "$baseUrl/checksums.sha256").Content
    if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }

    $expected = ($sums -split "`n" | Where-Object { $_ -match "\s\*?$([regex]::Escape("$name.zip"))\s*$" } | Select-Object -First 1) -replace '\s.*$', ''
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected) { throw "checksums.sha256 has no entry for $name.zip" }
    if ($actual -ne $expected.ToLowerInvariant()) { throw "SHA-256 mismatch for $name.zip: expected $expected, got $actual" }

    Expand-Archive $zip -DestinationPath $temp
    $extracted = Join-Path $temp $name
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $extracted 'bin\*') $target
    Copy-Item (Join-Path $extracted 'LICENSE.txt') $target

    $version = (& (Join-Path $target 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1)
    Set-Content $stampFile @($version, "sha256 $actual", "downloaded $(Get-Date -Format s)")
    Write-Host "Installed to $target"
    Write-Host $version
}
finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
