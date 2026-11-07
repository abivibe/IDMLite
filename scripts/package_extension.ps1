# IDMLite Browser Extension Packaging Script

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$extensionDir = Join-Path $rootDir "extension"
$distDir = Join-Path $rootDir "installer\dist_extension"

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
}

$zipPath = Join-Path $distDir "IDMLite_Extension_v1.0.1.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Write-Host "Creating extension zip package..." -ForegroundColor Cyan

# Compress files excluding native_host scripts
$files = Get-ChildItem -Path $extensionDir -Recurse | Where-Object {
    $_.FullName -notmatch "native_host"
}

Compress-Archive -Path "$extensionDir\manifest.json", "$extensionDir\background.js", "$extensionDir\content.js", "$extensionDir\content.css", "$extensionDir\popup.html", "$extensionDir\popup.js", "$extensionDir\popup.css", "$extensionDir\icons" -DestinationPath $zipPath -Force

Write-Host "Extension successfully packaged to: $zipPath" -ForegroundColor Green

