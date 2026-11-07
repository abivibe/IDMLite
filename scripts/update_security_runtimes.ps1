$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $root 'installer/toolchain/downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$nodeZip = Join-Path $downloads 'node-v24.22.1-win-x64.zip'
Invoke-WebRequest -Uri 'https://nodejs.org/dist/v24.22.1/node-v24.22.1-win-x64.zip' -OutFile $nodeZip
if ((Get-FileHash -LiteralPath $nodeZip -Algorithm SHA256).Hash -ne 'D2046D28C2C3DCD211AC5145C0BF30E2047EF9DFB306693C8C6E2ECCD8B95FA5') { throw 'Node archive integrity mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($nodeZip)
try {
    foreach ($spec in @(@('node-v24.22.1-win-x64/node.exe','ui/node.exe'), @('node-v24.22.1-win-x64/LICENSE','ui/NODE-LICENSE.txt'))) {
        $entry = $archive.GetEntry($spec[0])
        if (!$entry) { throw 'Expected Node archive entry is missing.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $root $spec[1]), $true)
    }
} finally { $archive.Dispose() }
$nodeHash = (Get-FileHash -LiteralPath (Join-Path $root 'ui/node.exe') -Algorithm SHA256).Hash
if ($nodeHash -ne 'D702DAA815EABBFF763C7D3808E235CF031CF2CA70211B56847BC72F51E240E5') { throw 'Node executable integrity mismatch.' }
$metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json'
$release = $metadata.releases | Where-Object { $_.'release-version' -eq '9.0.22' } | Select-Object -First 1
if ($release.sdk.version -ne '9.0.320') { throw 'Unexpected Microsoft SDK metadata.' }
$sdkFile = $release.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (!$sdkFile -or ([Uri]$sdkFile.url).Host -ne 'builds.dotnet.microsoft.com' -or ([Uri]$sdkFile.url).Scheme -ne 'https') { throw 'Unexpected Microsoft download URL.' }
$sdkZip = Join-Path $downloads 'dotnet-sdk-9.0.320-win-x64.zip'
Invoke-WebRequest -Uri $sdkFile.url -OutFile $sdkZip
if ((Get-FileHash -LiteralPath $sdkZip -Algorithm SHA512).Hash -ne $sdkFile.hash) { throw 'Microsoft SDK integrity mismatch.' }
Expand-Archive -LiteralPath $sdkZip -DestinationPath (Join-Path $root 'installer/toolchain/dotnet') -Force
[pscustomobject]@{
    NodeVersion='24.22.1'; NodeExecutableSha256=$nodeHash; Sdk='9.0.320'; Runtime='9.0.22'; SdkSha512=$sdkFile.hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'installer/toolchain/security-runtimes.json')
Write-Host 'Official security runtimes downloaded and verified.'
