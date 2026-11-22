$ErrorActionPreference = 'Stop'
$nativeDir = $PSScriptRoot
$repoDir = Split-Path -Parent $nativeDir
$versionXml = [xml](Get-Content -LiteralPath (Join-Path $nativeDir 'NativeVersion.props') -Raw)
$nativeVersion = [string]$versionXml.Project.PropertyGroup.MccVersion
if ($nativeVersion -eq '0.0.0' -or $nativeVersion -notmatch '^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$' -or ($nativeVersion.Split('.') | Where-Object { [int]$_ -gt 65535 })) { throw 'Invalid native version.' }
$dotnet = if ($env:MCC_DOTNET_EXE) { $env:MCC_DOTNET_EXE } else { 'dotnet' }
& (Join-Path $nativeDir 'Build-Preview.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
$artifacts = Join-Path $nativeDir 'artifacts'
$launchPublish = Join-Path $artifacts 'launcher-publish'
& $dotnet publish (Join-Path $nativeDir 'MeshCore.Windows.Launcher') -c Release -r win-x64 --self-contained true -o $launchPublish -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
Copy-Item -LiteralPath (Join-Path $launchPublish 'MeshCore Command.exe') -Destination (Join-Path $artifacts 'installer-launcher.exe') -Force
Add-Type -AssemblyName System.IO.Compression
$source = Join-Path $artifacts 'preview-publish'
$files = Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName
$hashes = [ordered]@{}
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\','/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$package = Join-Path $artifacts 'installer-payload.zip'
$stream = [IO.File]::Open($package, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $manifest = $zip.CreateEntry('manifest.json')
        $writer = [IO.StreamWriter]::new($manifest.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write(([ordered]@{version=1;files=$hashes} | ConvertTo-Json -Depth 8 -Compress)) } finally { $writer.Dispose() }
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
$output = Join-Path $artifacts 'installer-publish'
& $dotnet publish (Join-Path $nativeDir 'MeshCore.Windows.Installer') -c Release -r win-x64 --self-contained true -o $output -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$installer = Join-Path $output 'MeshCore.Command.Native.Preview.Setup.exe'
if ((Get-Item -LiteralPath $installer).VersionInfo.FileVersion -ne "$nativeVersion.0") { throw 'Installer version does not match the native version.' }
if ((Get-Item -LiteralPath $installer).Length -gt 200MB) { throw 'Installer exceeds the native updater download limit.' }
$test = Start-Process -FilePath $installer -ArgumentList '--test-install' -WindowStyle Hidden -Wait -PassThru
if ($test.ExitCode -ne 0) { throw 'Isolated installer test failed.' }
$updateTest = Start-Process -FilePath $installer -ArgumentList '--test-update' -WindowStyle Hidden -Wait -PassThru
if ($updateTest.ExitCode -ne 0) { throw 'Isolated updater handoff test failed.' }
Write-Output "Native preview installer built and tested: $installer"
Write-Output 'This is an isolated development preview, not a production release.'
