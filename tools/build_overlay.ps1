param(
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'ARM64')][string]$Arch,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(1, 64)][int]$Jobs = 8
)
$ErrorActionPreference = 'Stop'
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed: $LASTEXITCODE" }
}
try {
    $root = Split-Path -Parent $PSScriptRoot
    $backend = $env:TIGERCLAW_OVERLAY_BACKEND
    if ([string]::IsNullOrWhiteSpace($backend)) { $backend = 'native' }
    if ($backend -notin @('native', 'wpf')) { throw 'TIGERCLAW_OVERLAY_BACKEND must be native or wpf.' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    [void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
    Write-Host "Overlay backend: $backend; architecture: $Arch"
    if ($backend -eq 'wpf') {
        $project = Join-Path $root 'next\TigerClaw.Overlay\TigerClaw.Overlay.csproj'
        $arguments = @('msbuild', $project, '/restore', "/m:$Jobs", '/nr:false',
            "/p:Configuration=$Configuration", '/p:Platform=AnyCPU',
            "/p:PlatformTarget=$Arch", '/p:Prefer32Bit=false', "/p:OutDir=$OutputDirectory/", '/v:minimal')
        if ($Arch -eq 'ARM64') { $arguments += '/p:TigerClawTargetFramework=net481' }
        Invoke-Checked (Get-Command dotnet.exe).Source $arguments
    } else {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path $vswhere)) { throw 'Visual Studio Installer/vswhere.exe is required.' }
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json | Select-Object -First 1
        if (-not $installation) { throw 'Visual Studio C++ tools are required.' }
        $major = ([version]$installation.installationVersion).Major
        $generator = switch ($major) {
            18 { 'Visual Studio 18 2026' }
            17 { 'Visual Studio 17 2022' }
            default { throw "Unsupported Visual Studio version: $major" }
        }
        $cmake = Join-Path $installation.installationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
        if (-not (Test-Path $cmake)) { $cmake = (Get-Command cmake.exe).Source }
        $build = Join-Path $root "next\_native_build\overlay-$Arch"
        Invoke-Checked $cmake @('-S', (Join-Path $root 'next\TigerClaw.Overlay.Native'), '-B', $build,
            '-G', $generator, '-A', $Arch, "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)")
        Invoke-Checked $cmake @('--build', $build, '--config', $Configuration, '--target', 'overlay_native', '-j', "$Jobs")
        Copy-Item (Join-Path $build "$Configuration\TigerClaw.Overlay.exe") $OutputDirectory -Force
        # Only the obsolete WPF sidecar config; never clean the output directory.
        $legacyConfig = Join-Path $OutputDirectory 'TigerClaw.Overlay.exe.config'
        if (Test-Path $legacyConfig) { Remove-Item -LiteralPath $legacyConfig }
    }
    $sounds = Join-Path $OutputDirectory 'sounds'
    [void](New-Item -ItemType Directory -Force -Path $sounds)
    foreach ($name in @('KeyNormal.wav', 'KeySpace.wav', 'KeyFunc.wav')) {
        Copy-Item (Join-Path $root "next\TigerClaw.Overlay\sounds\$name") $sounds -Force
    }
    Copy-Item (Join-Path $root 'next\TigerClaw.Overlay.Native\THIRD-PARTY-NOTICES.txt') `
        (Join-Path $OutputDirectory 'Overlay-THIRD-PARTY-NOTICES.txt') -Force
    if (-not (Test-Path (Join-Path $OutputDirectory 'TigerClaw.Overlay.exe'))) { throw 'Overlay executable missing.' }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
