param(
    [Parameter(Mandatory=$true)][ValidateSet('x64','ARM64')][string]$Architecture,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$ModelPath = $env:TIGERCLAW_SHAPE_FIVEGRAM_MODEL
)
$ErrorActionPreference = 'Stop'
if (!$ModelPath) {
    $ModelPath = 'C:\Archive\tigerclaw_sentence_ml\experiments\brightmart-char5-500mb-20260922\char5-context128-q8.klm'
}
$expected = '580ed90ced0ac72e453e0d647635cec2d3879e2e47ae1a231b96f49b2d34eafa'
if (!(Test-Path -LiteralPath $ModelPath -PathType Leaf)) { throw "Missing shape fivegram: $ModelPath. Set TIGERCLAW_SHAPE_FIVEGRAM_MODEL to the verified model." }
if ((Get-Item -LiteralPath $ModelPath).Length -ne 419929926 -or (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash -ne $expected) {
    throw 'Shape fivegram size/hash does not match the validated 419.93 MB model.'
}
& "$PSScriptRoot\build_pinyin_native.bat" $Architecture $OutputDirectory $Configuration
if ($LASTEXITCODE -ne 0) { throw 'KenLM native build failed' }
$models = Join-Path $OutputDirectory 'Models'
$licenses = Join-Path $OutputDirectory 'licenses\kenlm'
New-Item -ItemType Directory -Force $models,$licenses | Out-Null
$destination = Join-Path $models 'sentence-fivegram.klm'
Copy-Item -LiteralPath $ModelPath -Destination $destination -Force
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expected) { throw 'Copied fivegram hash mismatch' }
foreach ($name in @('LICENSE','COPYING','COPYING.3','COPYING.LESSER.3')) {
    Copy-Item -LiteralPath "$PSScriptRoot\..\third_party\kenlm\$name" -Destination $licenses -Force
}
Write-Host "Staged shape fivegram + $Architecture KenLM into $OutputDirectory"
