# Build Inno Setup Installer Script

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$issPath = Join-Path $rootDir "installer\setup.iss"

# Find Inno Setup compiler
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
)

$isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $isccPath) {
    Write-Host "[ERROR] Inno Setup Compiler (ISCC.exe) not found." -ForegroundColor Red
    Exit 1
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  Compiling Windows Installer with Inno Setup" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "Using compiler: $isccPath" -ForegroundColor Gray

# Ensure output directory exists
$outDir = Join-Path $rootDir "installer\output"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

# Run Inno Setup Compiler from installer directory
Push-Location "$rootDir\installer"
try {
    & $isccPath "$issPath"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed: $LASTEXITCODE" }
} finally { Pop-Location }

$setupExe = Join-Path $outDir "IDMLite_Setup_v1.0.1.exe"
if (Test-Path $setupExe) {
    $rootSetup = Join-Path $rootDir 'IDMLite_Setup_v1.0.1.exe'
    Copy-Item -LiteralPath $setupExe -Destination $rootSetup -Force
    $hash = (Get-FileHash -LiteralPath $rootSetup -Algorithm SHA256).Hash
    "$hash  IDMLite_Setup_v1.0.1.exe" | Set-Content -LiteralPath "$rootSetup.sha256" -Encoding ascii
    $size = (Get-Item $setupExe).Length
    $sizeMb = [Math]::Round($size / (1024 * 1024), 2)
    Write-Host "`n[SUCCESS] Installer created successfully!" -ForegroundColor Green
    Write-Host "Package: $setupExe ($sizeMb MB)" -ForegroundColor Green
} else {
    Write-Host "`n[ERROR] Setup executable was not generated." -ForegroundColor Red
    Exit 1
}
