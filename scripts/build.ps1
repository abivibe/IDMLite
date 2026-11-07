param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'installer\dist_app'
$dotnet = Join-Path $root 'installer\toolchain\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Verified .NET SDK 9.0.320 is required in installer/toolchain/dotnet.' }
$node = Join-Path $root 'ui\node.exe'
if ((& $dotnet --version).Trim() -ne '9.0.320') { throw 'Unexpected .NET SDK version.' }
if ((Get-Item -LiteralPath $node).VersionInfo.ProductVersion -ne '24.22.1') { throw 'Node.js security version 24.22.1 is required.' }
if ((Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash -ne 'D702DAA815EABBFF763C7D3808E235CF031CF2CA70211B56847BC72F51E240E5') { throw 'Node.js official binary hash mismatch.' }
Push-Location $root
try {
    if (!$SkipTests) {
        & $dotnet run --project tests/Regression/Regression.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Backend regression tests failed' }
        & $dotnet run --project tests/DialogPreview/DialogPreview.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Live download dialog preview tests failed' }
        & $dotnet run --project tests/TransferRecovery/TransferRecovery.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Transfer and recovery tests failed' }
        & $dotnet run --project tests/Security/Security.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Security regression tests failed' }
        & $dotnet run --project tests/MediaRecovery/MediaRecovery.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Media recovery tests failed' }
        & $node tests/extension-regression.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Extension regression tests failed' }
        & $node tests/security-extension.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Extension security tests failed' }
        & $node tests/extension-preview.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Extension metadata prefetch tests failed' }
        & $node scripts/test_instagram_button.cjs --capture
        if ($LASTEXITCODE -ne 0) { throw 'Instagram menu handoff test failed' }
        & $node scripts/test_tiktok_button.cjs
        if ($LASTEXITCODE -ne 0) { throw 'TikTok canvas and video identity tests failed' }
        & $node tests/tiktok-identity.cjs
        if ($LASTEXITCODE -ne 0) { throw 'TikTok React identity tests failed' }
    }
    foreach ($dependency in @('yt-dlp.exe','ffmpeg.exe','ffprobe.exe','node.exe','NODE-LICENSE.txt','FFMPEG-LICENSE.txt','YTDLP-LICENSE.txt','YTDLP-THIRD-PARTY-LICENSES.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $root "ui\$dependency"))) { throw "Missing dependency: ui\$dependency" }
    }
    Push-Location (Join-Path $root 'dart_engine')
    try {
        dart pub get --enforce-lockfile
        if ($LASTEXITCODE -ne 0) { throw 'Dart dependency restore failed' }
        dart compile exe bin/dart_engine.dart -o idm_social_engine.exe
        if ($LASTEXITCODE -ne 0) { throw 'Dart compilation failed' }
        Copy-Item -LiteralPath 'idm_social_engine.exe' -Destination (Join-Path $root 'ui\idm_social_engine.exe') -Force
    } finally { Pop-Location }
    $resolved = [IO.Path]::GetFullPath($dist)
    if ($resolved -ne [IO.Path]::GetFullPath((Join-Path $root 'installer\dist_app'))) { throw 'Unsafe staging directory' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    & $dotnet publish ui/IDMLite.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $dist
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    foreach ($file in @('yt-dlp.exe','ffmpeg.exe','ffprobe.exe','node.exe','idm_social_engine.exe','NODE-LICENSE.txt','FFMPEG-LICENSE.txt','YTDLP-LICENSE.txt','YTDLP-THIRD-PARTY-LICENSES.txt')) {
        Copy-Item -LiteralPath (Join-Path $root "ui\$file") -Destination $dist -Force
    }
    Copy-Item -LiteralPath (Join-Path $root 'installer/toolchain/dotnet/LICENSE.txt') -Destination (Join-Path $dist 'DOTNET-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $root 'installer/toolchain/dotnet/ThirdPartyNotices.txt') -Destination (Join-Path $dist 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
    & (Join-Path $PSScriptRoot 'pack_crx.ps1')
    & (Join-Path $PSScriptRoot 'package_extension.ps1')
    $extension = Join-Path $dist 'extension'
    New-Item -ItemType Directory -Path $extension -Force | Out-Null
    foreach ($file in @('manifest.json','background.js','content.js','content.css','popup.html','popup.js','popup.css','icons','native_host')) {
        Copy-Item -LiteralPath (Join-Path $root "extension\$file") -Destination $extension -Recurse -Force
    }
    $firefox = Join-Path $dist 'extension_firefox'
    New-Item -ItemType Directory -Path $firefox -Force | Out-Null
    foreach ($file in @('background.js','content.js','content.css','popup.html','popup.js','popup.css','icons')) {
        Copy-Item -LiteralPath (Join-Path $extension $file) -Destination $firefox -Recurse -Force
    }
    $gecko = Get-Content -Raw -LiteralPath (Join-Path $extension 'manifest.json') | ConvertFrom-Json
    $gecko.PSObject.Properties.Remove('key')
    $gecko.permissions = @($gecko.permissions | Where-Object { $_ -ne 'downloads.ui' })
    $gecko.background = [pscustomobject]@{ scripts = @('background.js'); persistent = $false }
    [IO.File]::WriteAllText((Join-Path $firefox 'manifest.json'), ($gecko | ConvertTo-Json -Depth 20))
    Compress-Archive -Path (Join-Path $firefox '*') -DestinationPath (Join-Path $root 'installer/dist_extension/IDMLite_Firefox_Unsigned_v1.0.1.zip') -Force
    Copy-Item -LiteralPath (Join-Path $root 'IDMLite.crx') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'DAGITIM.md') -Destination $dist -Force
    Copy-Item -LiteralPath (Join-Path $root 'DOGRULAMA_1.0.1.md') -Destination $dist -Force
    $manifest = Get-ChildItem -LiteralPath $dist -File -Recurse | ForEach-Object {
        [pscustomobject]@{ file = $_.FullName.Substring($dist.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.json') -Encoding utf8
    Write-Host "Release staged: $dist"
} finally { Pop-Location }

