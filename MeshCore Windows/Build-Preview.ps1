#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$dotnet = if ($env:MCC_DOTNET_EXE) { $env:MCC_DOTNET_EXE } else { (Get-Command dotnet -ErrorAction Stop).Source }
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $PSScriptRoot 'artifacts\preview-publish'
Push-Location $root
try {
    node (Join-Path $PSScriptRoot 'Build-WebUI.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Web interface build failed.' }
    node --test 'NeonPocket/examples/companion_radio/webui/*.test.mjs' 'NeonPocket/examples/companion_radio/webui/src/*.test.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Web interface compatibility tests failed.' }
    $flasher = Join-Path $PSScriptRoot 'artifacts\espflash.exe'
    if (-not (Test-Path -LiteralPath $flasher) -or (Get-FileHash -LiteralPath $flasher -Algorithm SHA256).Hash.ToLowerInvariant() -ne '9fc8f3ace81e3180d2ba0b72c31a81e3f47ac580e3c9667954b58016d7a3fd87') {
        & (Join-Path $PSScriptRoot 'Fetch-EspFlash.ps1')
    }
    & $dotnet run --project (Join-Path $PSScriptRoot 'MeshCore.Windows.Core.Tests') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Native core tests failed.' }
    & $dotnet publish (Join-Path $PSScriptRoot 'MeshCore.Windows.Preview') --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $output
    if ($LASTEXITCODE -ne 0) { throw 'Preview publish failed.' }
    $listenerRetry = Start-Process -FilePath (Join-Path $output 'MeshCore.Windows.Preview.exe') -ArgumentList '--listener-retry-test' -WindowStyle Hidden -Wait -PassThru
    if ($listenerRetry.ExitCode -ne 0) { throw 'Listener retry test failed.' }
    $firmware = Start-Process -FilePath (Join-Path $output 'MeshCore.Windows.Preview.exe') -ArgumentList '--verify-firmware' -WindowStyle Hidden -Wait -PassThru
    if ($firmware.ExitCode -ne 0) { throw 'Packaged firmware verification failed.' }
    $test = Start-Process -FilePath (Join-Path $output 'MeshCore.Windows.Preview.exe') -ArgumentList '--integration-test' -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw 'Preview integration test failed.' }
    $migration = Start-Process -FilePath (Join-Path $output 'MeshCore.Windows.Preview.exe') -ArgumentList '--profile-integration-test' -WindowStyle Hidden -Wait -PassThru
    if ($migration.ExitCode -ne 0) { throw 'Synthetic profile migration integration test failed.' }
    Write-Output 'Development preview built and tested. It is not an installer or production release.'
} finally { Pop-Location }
