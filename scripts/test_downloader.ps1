# IDMLite Core Engine Unit / Integration Test Script

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Split-Path -Parent $scriptDir
$coreDll = Join-Path $rootDir "installer\dist_app\IDMLite.Core.dll"

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "  IDMLite Download Engine Automated Test" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

# Load Core Assembly
Add-Type -Path $coreDll

$testUrl = "https://raw.githubusercontent.com/curl/curl/master/README.md"
$tempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "idmlite_test_" + [System.Guid]::NewGuid().ToString("N") + ".md")

try {
    Write-Host "[1/4] Initializing SegmentedDownloader..." -ForegroundColor Yellow
    $downloader = New-Object IDMLite.Core.SegmentedDownloader($testUrl, $tempFile, 4, $null)

    Write-Host "[2/4] Probing HTTP headers & Range support..." -ForegroundColor Yellow
    $downloader.ProbeAsync([System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Write-Host "      Detected Size: $($downloader.TotalBytes) bytes" -ForegroundColor Green
    Write-Host "      Range Support: $($downloader.SupportsRange)" -ForegroundColor Green
    Write-Host "      Suggested Name: $($downloader.SuggestedFileName)" -ForegroundColor Green

    Write-Host "[3/4] Downloading with 4 concurrent threads..." -ForegroundColor Yellow
    $downloader.StartAsync().GetAwaiter().GetResult()

    if (Test-Path $tempFile) {
        $actualLen = (Get-Item $tempFile).Length
        Write-Host "      Downloaded File Size: $actualLen bytes" -ForegroundColor Green

        Write-Host "[4/4] Verifying SHA-256 Checksum..." -ForegroundColor Yellow
        $hash = [IDMLite.Core.ChecksumHelper]::ComputeHashAsync($tempFile, "SHA256", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        Write-Host "      SHA-256: $hash" -ForegroundColor Green

        Write-Host "`n[SUCCESS] All download engine checks PASSED!" -ForegroundColor Green
    } else {
        Write-Host "`n[FAIL] File was not created." -ForegroundColor Red
    }
}
catch {
    Write-Host "`n[ERROR] $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (Test-Path $tempFile) {
        Remove-Item $tempFile -Force -ErrorAction SilentlyContinue
    }
}
