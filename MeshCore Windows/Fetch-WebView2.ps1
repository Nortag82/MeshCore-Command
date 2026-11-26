#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$bootstrapper = Join-Path $PSScriptRoot 'artifacts/MicrosoftEdgeWebview2Setup.exe'
$expected = 'FC2972DA71D43E0C57B8D85A5CB9B200ED221F723CF43052D08CA722EB0BB22E'
$source = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/cc1101bc-3d8a-4c67-8678-b753ccdc08ad/MicrosoftEdgeWebview2Setup.exe'

if (-not (Test-Path -LiteralPath $bootstrapper) -or (Get-FileHash -LiteralPath $bootstrapper).Hash -ne $expected) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $bootstrapper) -Force | Out-Null
    $download = "$bootstrapper.$([guid]::NewGuid().ToString('N')).tmp.exe"
    try {
        Invoke-WebRequest -Uri $source -OutFile $download
        if ((Get-FileHash -LiteralPath $download).Hash -ne $expected) { throw 'Microsoft WebView2 bootstrapper checksum mismatch.' }
        Move-Item -LiteralPath $download -Destination $bootstrapper -Force
    } finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
    }
}
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )CN=Microsoft Corporation(,|$)') {
    throw 'The WebView2 bootstrapper is missing a valid Microsoft Authenticode signature.'
}
