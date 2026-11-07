$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (!(Test-Path -LiteralPath $chrome)) { throw 'Chrome is required to sign the CRX3 package' }
$stage = Join-Path $root 'installer\pack_extension'
$expected = [IO.Path]::GetFullPath((Join-Path $root 'installer\pack_extension'))
if ([IO.Path]::GetFullPath($stage) -ne $expected) { throw 'Unsafe extension staging path' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($file in @('manifest.json','background.js','content.js','content.css','popup.html','popup.js','popup.css','icons')) {
    Copy-Item -LiteralPath (Join-Path $root "extension\$file") -Destination $stage -Recurse -Force
}
$generated = "$stage.crx"
if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated -Force }
$key = Join-Path $root 'extension.pem'
$profile = Join-Path $root 'installer\pack_profile'
$arguments = @("--user-data-dir=`"$profile`"", "--pack-extension=`"$stage`"", "--pack-extension-key=`"$key`"", '--no-message-box','--no-first-run')
$process = Start-Process -FilePath $chrome -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Chrome extension packing timed out' }
if (!(Test-Path -LiteralPath $generated)) { throw 'Chrome did not produce a fresh CRX' }
$output = Join-Path $root 'installer\dist_extension'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($destination in @((Join-Path $root 'IDMLite.crx'), (Join-Path $root 'extension.crx'), (Join-Path $root 'extension\IDMLite.crx'), (Join-Path $output 'IDMLite.crx'))) {
    Copy-Item -LiteralPath $generated -Destination $destination -Force
}

