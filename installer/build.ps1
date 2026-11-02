# Builds installer\KaraokeSetup.exe: self-contained publish (x64 only) + bundled ffmpeg + Inno Setup.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& "C:\Program Files\dotnet\dotnet.exe" publish (Join-Path $root 'KaraokePlayer') -c Release -r win-x64 --self-contained true -o $dist
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
# only the 64-bit VLC is needed
Remove-Item (Join-Path $dist 'libvlc\win-x86'), (Join-Path $dist 'libvlc\win-arm64') -Recurse -Force -ErrorAction SilentlyContinue
$ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ffmpeg) { throw 'ffmpeg.exe not found (winget install Gyan.FFmpeg)' }
Copy-Item $ffmpeg.FullName $dist
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Q (Join-Path $PSScriptRoot 'Karaoke.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Get-Item (Join-Path $PSScriptRoot 'KaraokeSetup.exe') | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB) } }
