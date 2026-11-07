param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'ui\yt-dlp.exe'
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest' -TimeoutSec 20
$version = [string]$release.tag_name
if ($version -notmatch '^\d{4}\.\d{2}\.\d{2}$') { throw 'Invalid official release version.' }
$current = if (Test-Path -LiteralPath $target) { (Get-Item -LiteralPath $target).VersionInfo.FileVersion }
Write-Host "Packaged yt-dlp: $current; official stable: $version"
if ($CheckOnly -or ($current -and [version]$current -ge [version]$version)) { return }

$base = "https://github.com/yt-dlp/yt-dlp/releases/download/$version"
$checksums = (Invoke-WebRequest -Uri "$base/SHA2-256SUMS" -UseBasicParsing -TimeoutSec 30).Content
$expected = [regex]::Match([string]$checksums, '(?im)^([a-f0-9]{64})[ \t]+\*?yt-dlp\.exe\r?$').Groups[1].Value
if (!$expected) { throw 'The official checksum list is missing yt-dlp.exe.' }
$stage = Join-Path (Split-Path -Parent $target) ("yt-dlp-" + [guid]::NewGuid().ToString('N') + '.exe')
try {
    Invoke-WebRequest -Uri "$base/yt-dlp.exe" -UseBasicParsing -TimeoutSec 90 -OutFile $stage
    if ((Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash -ne $expected) { throw 'yt-dlp SHA-256 mismatch.' }
    if ((Get-Item -LiteralPath $stage).VersionInfo.FileVersion -ne $version) { throw 'yt-dlp version mismatch.' }
    if (Test-Path -LiteralPath $target) { [IO.File]::Replace($stage, $target, "$target.previous") }
    else { [IO.File]::Move($stage, $target) }
    Write-Host 'Verified yt-dlp is ready for the next distribution build.'
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage }
}
