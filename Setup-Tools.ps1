param([string]$AppDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$toolsDirectory = Join-Path $AppDirectory 'tools'
New-Item -ItemType Directory -Force $toolsDirectory | Out-Null
Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $toolsDirectory 'yt-dlp.exe')
$archive = Join-Path $toolsDirectory 'ffmpeg.zip'
Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolsDirectory 'ffmpeg-package') -Force
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $binary = Get-ChildItem (Join-Path $toolsDirectory 'ffmpeg-package') -Recurse -Filter $name | Select-Object -First 1
    if (!$binary) { throw "Missing $name in FFmpeg package" }
    Copy-Item -LiteralPath $binary.FullName -Destination (Join-Path $toolsDirectory $name) -Force
}
# Deno supplies the JavaScript runtime required by current YouTube extraction.
Invoke-WebRequest 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' -OutFile (Join-Path $toolsDirectory 'deno.zip')
Expand-Archive -LiteralPath (Join-Path $toolsDirectory 'deno.zip') -DestinationPath $toolsDirectory -Force
Write-Host 'Downloader ready. Install VB-CABLE separately from https://vb-audio.com/Cable/ and reboot before routing audio.'
