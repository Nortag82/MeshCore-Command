#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'artifacts/mercury'
$archiveHash = '7cf95ef0dd5f589c6d9224e481452029a7435d820b80ecc6d1258ed1f532df6e'
$asset = 'https://github.com/Rhizomatica/mercury/releases/download/v1.9.25/mercury-1.9.25-w64-33efac4e.zip'
$source = 'https://raw.githubusercontent.com/Rhizomatica/mercury/33efac4e3089f64f493fde1232b8e96305d15185'
$files = @('mercury.exe', 'libgcc_s_seh-1.dll', 'libhamlib-4.dll', 'libusb-1.0.dll', 'libwinpthread-1.dll', 'mercury.ini.example')
$temporary = [IO.Directory]::CreateTempSubdirectory('mcc-mercury-')
try {
    $archive = Join-Path $temporary.FullName 'mercury.zip'
    Invoke-WebRequest -Uri $asset -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $archiveHash) { throw 'Mercury archive checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $temporary.FullName
    $payload = Join-Path $temporary.FullName 'mercury-1.9.25'
    foreach ($license in 'LICENSE', 'LICENSE-freedv') {
        Invoke-WebRequest -Uri "$source/$license" -OutFile (Join-Path $payload $license)
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    foreach ($file in $files + @('LICENSE', 'LICENSE-freedv')) {
        Copy-Item -LiteralPath (Join-Path $payload $file) -Destination (Join-Path $output $file) -Force
    }
} finally {
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    $resolved = [IO.Path]::GetFullPath($temporary.FullName)
    if ([IO.Path]::GetDirectoryName($resolved) -ieq $tempRoot -and [IO.Path]::GetFileName($resolved).StartsWith('mcc-mercury-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
