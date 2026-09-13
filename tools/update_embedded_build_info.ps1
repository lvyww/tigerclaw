param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][ValidatePattern('^[01]$')][string]$TextLogEnabled
)

$ErrorActionPreference = 'Stop'

$raw = Get-Content -LiteralPath $Path -Raw
$original = $raw
foreach ($field in @('BIME_EMBED_TEXT_LOG_ENABLED', 'BIME_EMBED_VERBOSE_LOG_ENABLED')) {
    if ($raw -notmatch ('\b' + $field + '\b')) { throw "Missing field: $field" }
}

$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_TEXT_LOG_ENABLED\s+\d+\s*$', '#define BIME_EMBED_TEXT_LOG_ENABLED ' + $TextLogEnabled)
$raw = [regex]::Replace($raw, '(?m)^#define\s+BIME_EMBED_VERBOSE_LOG_ENABLED\s+\d+\s*$', '#define BIME_EMBED_VERBOSE_LOG_ENABLED ' + $TextLogEnabled)

if ($raw -cne $original) {
    Set-Content -LiteralPath $Path -Value $raw -Encoding Ascii -NoNewline
}
