# 获取内置工具（yt-dlp / aria2c / ffmpeg）到 tools\
# 用法: powershell -ExecutionPolicy Bypass -File scripts\fetch-tools.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "tools"
New-Item -ItemType Directory -Force -Path $tools | Out-Null

function Get-File($url, $name) {
    $out = Join-Path $tools $name
    Write-Host "下载 $name ..."
    Invoke-WebRequest -Uri $url -OutFile $out
    Write-Host "  -> $out ($([math]::Round((Get-Item $out).Length/1MB,1)) MB)"
}

Get-File "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" "yt-dlp.exe"
Get-File "https://github.com/aria2/aria2/releases/latest/download/aria2-1.37.0-win-64bit-build1.zip" "_aria2.zip"
Get-File "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" "_ffmpeg.zip"

# 解压并取所需文件
Expand-Archive (Join-Path $tools "_aria2.zip") -DestinationPath (Join-Path $tools "_aria2") -Force
Get-ChildItem (Join-Path $tools "_aria2") -Recurse -Filter "aria2c.exe" | Select-Object -First 1 | Copy-Item -Destination (Join-Path $tools "aria2c.exe") -Force

Expand-Archive (Join-Path $tools "_ffmpeg.zip") -DestinationPath (Join-Path $tools "_ffmpeg") -Force
Get-ChildItem (Join-Path $tools "_ffmpeg") -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1 | Copy-Item -Destination (Join-Path $tools "ffmpeg.exe") -Force

Remove-Item (Join-Path $tools "_aria2.zip"), (Join-Path $tools "_ffmpeg.zip") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $tools "_aria2"), (Join-Path $tools "_ffmpeg") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "完成。tools\ 下应有 yt-dlp.exe / aria2c.exe / ffmpeg.exe"
