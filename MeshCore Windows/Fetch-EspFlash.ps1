#Requires -Version 7.0
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$archiveHash = '2b10333e4f4f1a26024259b0cc1942ba17f27b3c530621c6111f4c10ac70213e'
$binaryHash = '9fc8f3ace81e3180d2ba0b72c31a81e3f47ac580e3c9667954b58016d7a3fd87'
$url = 'https://github.com/esp-rs/espflash/releases/download/v4.7.0/espflash-x86_64-pc-windows-msvc.zip'
$temporaryArchive = Join-Path ([System.IO.Path]::GetTempPath()) ('mcc-espflash-' + [guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri $url -OutFile $temporaryArchive
    if ((Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) { throw 'espflash archive digest mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -eq 'espflash.exe' })
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 100000000) { throw 'Invalid espflash archive.' }
        [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
        $output = Join-Path $OutputDirectory 'espflash.exe'
        $tempOutput = Join-Path $OutputDirectory ('.espflash-' + [guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $input = $entries[0].Open()
            try {
                $file = [System.IO.File]::Open($tempOutput, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
                try { $input.CopyTo($file) } finally { $file.Dispose() }
            } finally { $input.Dispose() }
            if ((Get-FileHash -LiteralPath $tempOutput -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binaryHash) { throw 'espflash executable digest mismatch.' }
            [System.IO.File]::Move($tempOutput, $output, $true)
        } finally { if (Test-Path -LiteralPath $tempOutput) { Remove-Item -LiteralPath $tempOutput } }
    } finally { $zip.Dispose() }
    Write-Output 'Verified espflash executable prepared.'
} finally { if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive } }
