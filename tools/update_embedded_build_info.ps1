param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$CoreExe,
    [Parameter(Mandatory = $true)][ValidatePattern('^[01]$')][string]$TextLogEnabled,
    [Parameter(Mandatory = $true)][ValidatePattern('^[01]$')][string]$CoreHashVerifyEnabled,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$')][string]$TrialExpireUtc
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $CoreExe)) {
    throw "Core exe not found: $CoreExe"
}

$raw = Get-Content -LiteralPath $Path -Raw
$coreHash = (Get-FileHash -LiteralPath $CoreExe -Algorithm SHA256).Hash.ToLowerInvariant()

$getKey = {
    param([string]$Name)
    $match = [regex]::Match($raw, '(?m)^#define\s+' + [regex]::Escape($Name) + '\s+0x([0-9A-Fa-f]{1,2})\s*$')
    if (-not $match.Success) {
        throw "Missing key: $Name"
    }

    [Convert]::ToInt32($match.Groups[1].Value, 16)
}

$keys = @(
    (& $getKey 'BIME_EMBED_XOR_KEY0'),
    (& $getKey 'BIME_EMBED_XOR_KEY1'),
    (& $getKey 'BIME_EMBED_XOR_KEY2'),
    (& $getKey 'BIME_EMBED_XOR_KEY3')
)

$encode = {
    param([string]$Text)
    $src = [System.Text.Encoding]::ASCII.GetBytes($Text)
    $dst = New-Object byte[] $src.Length
    for ($i = 0; $i -lt $src.Length; $i++) {
        $mask = ($keys[$i % $keys.Count] -bxor (($i * 13 + 0x5A) -band 0xFF))
        $dst[$i] = ($src[$i] -bxor $mask)
    }

    $dst
}

$formatBytes = {
    param([byte[]]$Bytes)
    (($Bytes | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', ')
}

$trialEncoded = & $encode $TrialExpireUtc
$coreHashEncoded = & $encode $coreHash

$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_TEXT_LOG_ENABLED\s+\d+\s*$', '#define BIME_EMBED_TEXT_LOG_ENABLED ' + $TextLogEnabled)
$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_VERBOSE_LOG_ENABLED\s+\d+\s*$', '#define BIME_EMBED_VERBOSE_LOG_ENABLED ' + $TextLogEnabled)
$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_CORE_HASH_VERIFY_ENABLED\s+\d+\s*$', '#define BIME_EMBED_CORE_HASH_VERIFY_ENABLED ' + $CoreHashVerifyEnabled)
$raw = [regex]::Replace($raw, '(?m)^static const unsigned char BIME_EMBED_TRIAL_EXPIRE_UTC_ENC\[\]\s*=\s*\{[^}]*\};\s*$', 'static const unsigned char BIME_EMBED_TRIAL_EXPIRE_UTC_ENC[] = { ' + (& $formatBytes $trialEncoded) + ' };')
$raw = [regex]::Replace($raw, '(?m)^static const unsigned int BIME_EMBED_TRIAL_EXPIRE_UTC_LEN\s*=\s*\d+u;\s*$', 'static const unsigned int BIME_EMBED_TRIAL_EXPIRE_UTC_LEN = ' + $trialEncoded.Length + 'u;')
$raw = [regex]::Replace($raw, '(?m)^static const unsigned char BIME_EMBED_CORE_SHA256_ENC\[\]\s*=\s*\{[^}]*\};\s*$', 'static const unsigned char BIME_EMBED_CORE_SHA256_ENC[] = { ' + (& $formatBytes $coreHashEncoded) + ' };')
$raw = [regex]::Replace($raw, '(?m)^static const unsigned int BIME_EMBED_CORE_SHA256_LEN\s*=\s*\d+u;\s*$', 'static const unsigned int BIME_EMBED_CORE_SHA256_LEN = ' + $coreHashEncoded.Length + 'u;')

Set-Content -LiteralPath $Path -Value $raw -Encoding Ascii -NoNewline
Write-Host ('  embedded core_sha256=' + $coreHash)
