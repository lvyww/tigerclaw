$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('TigerClaw TSF Publish Tests ' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $scratch)
function Assert($value, $message) { if (-not $value) { throw $message } }
function Reject($action, $message) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    Assert $rejected $message
}
function Fixture($path, [uint16]$machine, [byte]$marker) {
    [void](New-Item -ItemType Directory -Force -Path (Split-Path $path))
    $bytes = New-Object byte[] 128
    [BitConverter]::GetBytes([uint16]0x5A4D).CopyTo($bytes, 0)
    [BitConverter]::GetBytes([int]64).CopyTo($bytes, 60)
    [BitConverter]::GetBytes([int]0x4550).CopyTo($bytes, 64)
    [BitConverter]::GetBytes($machine).CopyTo($bytes, 68)
    [BitConverter]::GetBytes([uint16]0x2000).CopyTo($bytes, 86)
    $bytes[127] = $marker
    [IO.File]::WriteAllBytes($path, $bytes)
}
$verify = Join-Path $PSScriptRoot 'verify_tsf_artifact.ps1'
$publish = Get-Content (Join-Path $PSScriptRoot '..\publish.bat') -Raw
Assert ($publish.Contains('/t:Rebuild')) 'TSF must rebuild current artifacts.'
Assert ($publish.Contains('/p:OutDir="%TSF_BUILD_ROOT%\%TSF_PLATFORM%\\"')) 'Explicit TSF output missing.'
Assert ($publish.Contains('obj\publish-x64-tsf\%TSF_PLATFORM%\\')) 'Isolated intermediates missing.'
Assert (-not $publish.Contains('if not exist "%TSF_X86_DLL%" set')) 'Legacy fallback returned.'
foreach ($arch in @('Win32', 'x64')) {
    $variable = if ($arch -eq 'Win32') { 'TSF_X86_DLL' } else { 'TSF_X64_DLL' }
    Assert ($publish.Contains(('set "{0}=%TSF_BUILD_ROOT%\{1}\TigerClaw.dll"' -f $variable, $arch))) 'Copy path differs from build path.'
    Assert ($publish.Contains(('-Source "%{0}%" -Architecture {1} -Destination' -f $variable, $arch))) 'Copy verification missing.'
}
$source = Join-Path $scratch 'next\_run\Release\tsf\Win32\TigerClaw.dll'
$legacy = Join-Path $scratch 'BimeTSF2\SampleIME\Win32\Release\TigerClaw.dll'
$copy = Join-Path $scratch 'copy.dll'
Fixture $legacy 0x14C 1
Reject { & $verify -Source $source -Architecture Win32 } 'Missing current artifact accepted despite old DLL.'
Fixture $source 0x8664 2
Reject { & $verify -Source $source -Architecture Win32 } 'Wrong architecture accepted.'
Fixture $source 0x14C 2
Copy-Item $source $copy
& $verify -Source $source -Architecture Win32 -Destination $copy
Copy-Item $legacy $copy -Force
Reject { & $verify -Source $source -Architecture Win32 -Destination $copy } 'Stale same-architecture copy accepted.'
Fixture $copy 0x8664 2
Reject { & $verify -Source $source -Architecture Win32 -Destination $copy } 'Wrong destination architecture accepted.'
[IO.File]::WriteAllText($source, 'truncated')
Reject { & $verify -Source $source -Architecture Win32 } 'Malformed PE accepted.'

# Run the production batch subroutine against an isolated MSBuild fixture.
# This exercises cmd quoting, OutDir/IntDir propagation and failure exit codes.
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
Assert (Test-Path $msbuild) 'MSBuild is required for the batch integration test.'
[void](New-Item -ItemType Directory -Path "$scratch\tools")
Copy-Item $verify "$scratch\tools\verify_tsf_artifact.ps1"
$project = Join-Path $scratch 'fixture.proj'
$projectText = @'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Target Name="Rebuild">
    <Error Condition="'$(OutDir)' == '' Or '$(IntDir)' == ''" Text="Missing explicit output directories" />
    <MakeDir Directories="$(OutDir);$(IntDir)" />
    <Copy SourceFiles="$(MSBuildProjectDirectory)\fresh.dll" DestinationFiles="$(OutDir)TigerClaw.dll" Condition="'$(FixtureSkipCopy)' != 'true'" />
  </Target>
</Project>
'@
[IO.File]::WriteAllText($project, $projectText)
$subroutine = $publish.Substring($publish.LastIndexOf(':BuildTsfRelease'))
$batch = Join-Path $scratch 'build fixture.bat'
$batchText = @"
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "ROOT=$scratch"
set "MSBUILD=$msbuild"
set "TSF_PROJECT=$project"
set "TSF_BUILD_ROOT=$scratch\next\_run\Release\tsf"
set "TSF_TOOLSET_ARGS=/p:FixtureSkipCopy=%~2"
call :BuildTsfRelease %~1
exit /b %errorlevel%
$subroutine
"@
[IO.File]::WriteAllText($batch, ($batchText -replace "\r?\n", "`r`n"), [Text.Encoding]::ASCII)
foreach ($arch in @('Win32', 'x64')) {
    $machine = if ($arch -eq 'Win32') { 0x14C } else { 0x8664 }
    $selected = Join-Path $scratch "next\_run\Release\tsf\$arch\TigerClaw.dll"
    Fixture "$scratch\fresh.dll" $machine 7
    Fixture $selected $machine 1
    & $env:ComSpec /d /c "`"$batch`" $arch false" | Out-Null
    Assert ($LASTEXITCODE -eq 0) 'Production build subroutine failed.'
    & $verify -Source "$scratch\fresh.dll" -Architecture $arch -Destination $selected
    Fixture "$scratch\fresh.dll" 0xAA64 7
    # Windows PowerShell wraps native stderr as errors; these failures are expected.
    try {
        $ErrorActionPreference = 'Continue'
        & $env:ComSpec /d /c "`"$batch`" $arch false" *> "$scratch\wrong-$arch.log"
    } finally { $ErrorActionPreference = 'Stop' }
    Assert ($LASTEXITCODE -ne 0) 'Batch accepted wrong architecture.'
    Move-Item $selected "$scratch\removed-$arch.dll"
    try {
        $ErrorActionPreference = 'Continue'
        & $env:ComSpec /d /c "`"$batch`" $arch true" *> "$scratch\missing-$arch.log"
    } finally { $ErrorActionPreference = 'Stop' }
    Assert ($LASTEXITCODE -ne 0) 'Batch accepted missing current artifact.'
}
Write-Host "TSF publish tests passed. Fixtures: $scratch"
