param(
    [Parameter(Mandatory=$true)][ValidateSet('x64','ARM64')][string]$Architecture,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$ModelPath = $env:TIGERCLAW_SHAPE_FIVEGRAM_MODEL
)
$ErrorActionPreference = 'Stop'
if (!$ModelPath) {
    $ModelPath = 'C:\Archive\tigerclaw_sentence_ml\runtime\sentence-fivegram-mobile.bin'
}
$expected = '5c46b7c2734886e868c6207a724f4dff2d9c64cb3eba193e7dd44ea9df244361'
if (!(Test-Path -LiteralPath $ModelPath -PathType Leaf)) { throw "Missing shape fivegram: $ModelPath. Set TIGERCLAW_SHAPE_FIVEGRAM_MODEL to the verified model." }
if ((Get-Item -LiteralPath $ModelPath).Length -ne 356492204 -or (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash -ne $expected) {
    throw 'Shape fivegram size/hash does not match the validated 356.49 MB TCS Q8 model.'
}
$models = Join-Path $OutputDirectory 'Models'
New-Item -ItemType Directory -Force $models | Out-Null
$destination = Join-Path $models 'sentence-fivegram-mobile.bin'
Copy-Item -LiteralPath $ModelPath -Destination $destination -Force
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expected) { throw 'Copied fivegram hash mismatch' }
Write-Host "Staged standalone TCS Q8 fivegram into $OutputDirectory ($Architecture)"
