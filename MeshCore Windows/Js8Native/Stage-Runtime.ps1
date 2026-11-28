#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Worker,
    [Parameter(Mandatory)][string]$Output,
    [string]$MsysRoot = 'C:\msys64'
)
$ErrorActionPreference = 'Stop'
$runtime = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'runtime-sources.json') | ConvertFrom-Json
$ucrt = Join-Path $MsysRoot 'ucrt64'
$pacman = Join-Path $MsysRoot 'usr/bin/pacman.exe'
$destination = New-Item -ItemType Directory -Path $Output
$workerPath = Join-Path $destination.FullName 'meshcore-js8-phy.exe'
Copy-Item -LiteralPath $Worker -Destination $workerPath
foreach ($package in $runtime.packages) {
    $name = "mingw-w64-ucrt-x86_64-$($package.package)"
    $installed = & $pacman -Q $name
    if ($LASTEXITCODE -ne 0 -or $installed -cne "$name $($package.version)") { throw "Expected $name $($package.version)." }
    foreach ($dll in $package.dlls) {
        Copy-Item -LiteralPath (Join-Path $ucrt "bin/$dll") -Destination $destination.FullName
    }
    if ($package.licenseDir) {
        $target = Join-Path $destination.FullName "licenses/$($package.licenseDir)"
        New-Item -ItemType Directory -Path $target | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $ucrt "share/licenses/$($package.licenseDir)") -File -Recurse |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target }
    }
    if ($package.licenseFile) {
        $target = Join-Path $destination.FullName "licenses/$($package.package)"
        New-Item -ItemType Directory -Path $target | Out-Null
        Copy-Item -LiteralPath (Join-Path $ucrt $package.licenseFile) -Destination $target
    }
}
foreach ($file in @('COPYING', 'THIRD-PARTY.md', 'runtime-sources.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $destination.FullName
}
# Test using the staged redistributables and the Windows system runtime.
$originalPath = $env:PATH
try {
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $reply = '{"protocol":1,"id":"package","operation":"status"}' | & $workerPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !$reply.result.fileOnly -or $reply.id -cne 'package') {
        throw 'The staged JS8 worker did not start with its packaged libraries.'
    }
}
finally { $env:PATH = $originalPath }
