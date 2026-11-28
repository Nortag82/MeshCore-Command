#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Upstream,
    [Parameter(Mandatory)][string]$StagingDirectory,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'runtime-sources.json') | ConvertFrom-Json
$source = New-Item -ItemType Directory -Path $StagingDirectory -Force
$msys = New-Item -ItemType Directory -Path (Join-Path $source.FullName 'msys2') -Force
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$commit = '4c592bd9a034f18178a3e7179db92acb14939668'
& git -C $Upstream archive --format zip "--output=$(Join-Path $source.FullName 'js8call-improved-v3.0.3.zip')" $commit
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the pinned JS8Call checkout.' }
& git -C $repo archive --format zip "--output=$(Join-Path $source.FullName 'meshcore-command-source.zip')" HEAD 'MeshCore Windows/Js8Native' docs/js8-r
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the JS8-R wrapper and build instructions.' }
$manifest.packages | ForEach-Object -Parallel {
    $root = $using:msys
    $configuration = $using:manifest
    $url = $configuration.msys2SourceBase + $_.source
    $target = Join-Path $root.FullName $_.source
    $partial = "$target.part"
    if (!(Test-Path -LiteralPath $target -PathType Leaf)) {
        Invoke-WebRequest -Uri $url -OutFile $partial -TimeoutSec 180 -ErrorAction Stop
        Move-Item -LiteralPath $partial -Destination $target -Force
    }
} -ThrottleLimit 5
foreach ($package in $manifest.packages) {
    $path = Join-Path $msys.FullName $package.source
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing corresponding source $($package.source)." }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY.md'), (Join-Path $PSScriptRoot 'runtime-sources.json') -Destination $source.FullName
[IO.Compression.ZipFile]::CreateFromDirectory($source.FullName, [IO.Path]::GetFullPath($Output), [IO.Compression.CompressionLevel]::NoCompression, $false)
