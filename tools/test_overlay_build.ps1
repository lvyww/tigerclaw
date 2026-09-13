param([switch]$Build)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($relative in @('publish.bat', 'publish_arm64.bat', 'pack_release.bat', 'next\build_next.bat', 'next\build_overlay.bat')) {
    $text = [IO.File]::ReadAllText((Join-Path $root $relative))
    if ($text -match '(?<!\r)\n') { throw "Non-CRLF batch file: $relative" }
    if ($relative -notin @('next\build_overlay.bat', 'pack_release.bat') -and $text -notmatch 'build_overlay.bat') {
        throw "Missing shared Overlay build entry: $relative"
    }
}
$pack = [IO.File]::ReadAllText((Join-Path $root 'pack_release.bat'))
if ($pack -notmatch 'Overlay-THIRD-PARTY-NOTICES.txt') { throw 'Overlay license missing from release archive.' }
if ($Build) {
    $savedBackend = $env:TIGERCLAW_OVERLAY_BACKEND
    $scratch = Join-Path $root 'next\_run\OverlayMainlineCheck'
    $entry = Join-Path $root 'next\build_overlay.bat'
    try {
        foreach ($arch in @('ARM64', 'x64')) {
            $env:TIGERCLAW_OVERLAY_BACKEND = if ($arch -eq 'ARM64') { $null } else { 'native' }
            $out = Join-Path $scratch $arch
            & $entry $arch $out Release 4
            if ($LASTEXITCODE -ne 0) { throw "Native $arch build failed." }
            $exe = [IO.File]::ReadAllBytes((Join-Path $out 'TigerClaw.Overlay.exe'))
            $machine = [BitConverter]::ToUInt16($exe, [BitConverter]::ToInt32($exe, 0x3c) + 4)
            $expected = if ($arch -eq 'ARM64') { 0xaa64 } else { 0x8664 }
            if ($machine -ne $expected) { throw "Wrong PE architecture: $arch" }
            foreach ($file in @('Overlay-THIRD-PARTY-NOTICES.txt', 'sounds\KeyNormal.wav', 'sounds\KeySpace.wav', 'sounds\KeyFunc.wav')) {
                if (-not (Test-Path (Join-Path $out $file))) { throw "Missing $file" }
            }
        }
        $env:TIGERCLAW_OVERLAY_BACKEND = 'wpf'
        & $entry ARM64 (Join-Path $scratch 'Wpf Fallback') Release 2
        if ($LASTEXITCODE -ne 0) { throw 'WPF fallback build failed (path with spaces).' }
        $config = Join-Path $scratch 'Wpf Fallback\TigerClaw.Overlay.exe.config'
        if (-not (Test-Path $config)) { throw 'WPF config missing.' }
        $env:TIGERCLAW_OVERLAY_BACKEND = 'native'
        & $entry ARM64 (Join-Path $scratch 'Wpf Fallback') Release 2
        if ($LASTEXITCODE -ne 0 -or (Test-Path $config)) { throw 'Native switch retained old WPF config.' }
        $env:TIGERCLAW_OVERLAY_BACKEND = 'invalid'
        & $entry ARM64 (Join-Path $scratch 'Invalid') Release 2
        if ($LASTEXITCODE -eq 0) { throw 'Invalid backend accepted.' }
    } finally { $env:TIGERCLAW_OVERLAY_BACKEND = $savedBackend }
}
Write-Host 'Overlay mainline build checks passed; no runtime deployed.'
