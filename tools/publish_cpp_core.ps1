param(
    [string]$Destination,
    [ValidateSet("ARM64", "x64")][string]$Architecture = "ARM64",
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [string]$OutputDirectory,
    [switch]$BuildOnly,
    [switch]$NoRestart,
    [ValidateRange(1, 32)][int]$Jobs = 2
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$backup = $null
$replaced = $false
$wasRunning = $false
$target = $null
$restartArguments = '--with-overlay --silent'

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed: $LASTEXITCODE" }
}
function Assert-Architecture([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid executable: $Path" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 24) { throw 'Invalid PE header offset' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $(if ($Architecture -eq 'ARM64') { 0xAA64 } else { 0x8664 })) { throw "Core architecture does not match $Architecture" }
    } finally { $reader.Dispose() }
}
function Get-TargetProcesses([string]$Path) {
    @(Get-CimInstance Win32_Process -Filter "Name = 'TigerClaw.Core.exe'" | Where-Object {
        $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $Path, [StringComparison]::OrdinalIgnoreCase)
    })
}
function Read-Response($Reader) {
    $task = $Reader.ReadLineAsync()
    if (-not $task.Wait(2000)) { throw 'Core response timeout' }
    if ($null -eq $task.Result) { throw 'Core closed the pipe' }
    $task.Result | ConvertFrom-Json
}
function Connect-Core {
    $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'BimeIPC', [IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(1000); return $pipe } catch { $pipe.Dispose(); throw }
}
function Test-TargetHello([string]$Path, [switch]$ExitCore) {
    $pipe = $null; $reader = $null; $writer = $null
    try {
        $pipe = Connect-Core
        $reader = New-Object IO.StreamReader($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = New-Object IO.StreamWriter($pipe, (New-Object Text.UTF8Encoding($false)), 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine('{"type":"hello","seq":1}')
        $hello = Read-Response $reader
        if (-not $hello.success -or $hello.protocol_version -ne 2 -or
            -not [string]::Equals($hello.core_path, $Path, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($ExitCore) {
            $writer.WriteLine('{"type":"exit_core","seq":2}')
            # C# can shut down before flushing the acknowledgement. Ownership
            # was verified by hello; the retained process is checked below.
            try { $null = Read-Response $reader } catch { }
        }
        return $true
    } catch { return $false }
    finally {
        if ($writer) { $writer.Dispose() }; if ($reader) { $reader.Dispose() }; if ($pipe) { $pipe.Dispose() }
    }
}
function Stop-TargetCore([string]$Path) {
    $identities = @(Get-TargetProcesses $Path)
    if ($identities.Count -eq 0) { return }
    # Open process objects before shutdown; no /IM or global process-name kill.
    $processes = @($identities | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
    $null = Test-TargetHello $Path -ExitCore
    foreach ($process in $processes) {
        try {
            if (-not $process.WaitForExit(5000)) { $process.Kill(); if (-not $process.WaitForExit(5000)) { throw 'Core did not exit' } }
        } finally { $process.Dispose() }
    }
}
function Start-TargetCore([string]$Path) {
    $process = Start-Process -FilePath $Path -ArgumentList $restartArguments -WorkingDirectory (Split-Path -Parent $Path) -PassThru
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.ElapsedMilliseconds -lt 30000) {
            if ($process.HasExited) { throw "Published Core exited: $($process.ExitCode)" }
            if (Test-TargetHello $Path) { return }
            Start-Sleep -Milliseconds 200
        }
        throw 'Published Core did not answer its production pipe'
    } finally { $process.Dispose() }
}
try {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ tools are required.' }
    $component = if ($Architecture -eq 'ARM64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $installation = & $vswhere -latest -products '*' -requires $component -format json | ConvertFrom-Json | Select-Object -First 1
    if (-not $installation) { throw "Install Visual Studio C++ $Architecture build tools first." }
    $generator = switch (([version]$installation.installationVersion).Major) {
        18 { 'Visual Studio 18 2026' }
        17 { 'Visual Studio 17 2022' }
        default { throw 'Unsupported Visual Studio version' }
    }
    $cmake = Join-Path $installation.installationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (-not (Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake.exe).Source }
    $ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
    $build = Join-Path $root ("next\_run\CoreNativePublish\" + $Architecture)
    Write-Host "[1/4] Configure and build native $Architecture Core (production and isolated test executables)"
    Invoke-Checked $cmake @('-S', (Join-Path $root 'next\TigerClaw.Core.Native'), '-B', $build, '-G', $generator,
        '-A', $Architecture, "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)", '-DBUILD_TESTING=ON')
    Invoke-Checked $cmake @('--build', $build, '--config', $Configuration, '--parallel', "$Jobs")
    $artifact = Join-Path $build ($Configuration + "\TigerClaw.Core.exe")
    Assert-Architecture $artifact
    Write-Host '[2/4] Run CTests and inspect production identity (no production startup)'
    Invoke-Checked $ctest @('--test-dir', $build, '-C', $Configuration, '--output-on-failure')
    $capabilityText = (& $artifact --capabilities | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Core capability probe failed' }
    $capability = $capabilityText | ConvertFrom-Json
    if ($capability.implementation -ne 'TigerClaw.Core' -or $capability.backend -ne 'cpp' -or -not $capability.production_entrypoint) { throw 'Wrong Core artifact' }
    $newHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    if ($OutputDirectory) {
        if (-not $BuildOnly) { throw 'OutputDirectory requires BuildOnly; use Destination for runtime replacement.' }
        $null = New-Item -ItemType Directory -Force -Path $OutputDirectory
        Copy-Item -LiteralPath $artifact -Destination (Join-Path $OutputDirectory 'TigerClaw.Core.exe') -Force
    }
    if ($BuildOnly) { Write-Host "Build-only passed: $artifact`nSHA256: $newHash"; exit 0 }

    if ($Architecture -ne 'ARM64') { throw 'Runtime replacement is ARM64-only; use BuildOnly for x64 packaging.' }
    if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $root 'release_arm64' }
    $Destination = [IO.Path]::GetFullPath($Destination)
    $target = Join-Path $Destination 'TigerClaw.Core.exe'
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Existing Core required: $target. Run publish_arm64.bat first." }
    if ([string]::Equals($target, $artifact, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish destination must differ from build output' }
    $running = @(Get-TargetProcesses $target); $wasRunning = $running.Count -gt 0
    if ($wasRunning -and $running[0].CommandLine -match '(?i)--without-overlay') { $restartArguments = '--without-overlay --silent' }
    $backup = Join-Path $Destination ('backup-before-cpp-core-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $null = New-Item -ItemType Directory -Path $backup
    $oldHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    # Backup is complete and verified before any process is stopped.
    $backupCore = Join-Path $backup 'TigerClaw.Core.exe'
    Copy-Item -LiteralPath $target -Destination $backupCore
    if ((Get-FileHash -LiteralPath $backupCore -Algorithm SHA256).Hash -ne $oldHash) { throw 'Core backup verification failed' }
    $manifest = [ordered]@{ target=$target; oldSha256=$oldHash; newSha256=$newHash; wasRunning=$wasRunning; restartArguments=$restartArguments; build=$build; status='prepared' }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'publish.json') -Encoding UTF8
    Write-Host "[3/4] Replace only $target (backup: $backup)"
    $incoming = Join-Path $Destination ('.TigerClaw.Core.cpp-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    Copy-Item -LiteralPath $artifact -Destination $incoming
    try {
        Stop-TargetCore $target
        [IO.File]::Replace($incoming, $target, [NullString]::Value)
        $replaced = $true
    } finally { if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming } }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $newHash) { throw 'Published Core hash mismatch' }
    Write-Host '[4/4] Preserve prior running/stopped state and verify restart'
    if ($wasRunning -and -not $NoRestart) { Start-TargetCore $target }
    $manifest.status = 'completed'
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'publish.json') -Encoding UTF8
    Write-Host "Published C++ ARM64 Core: $target`nBackup: $backup`nSHA256: $newHash"
    exit 0
} catch {
    $failure = $_
    if ($replaced -and $backup -and $target) {
        try {
            Stop-TargetCore $target
            $restore = Join-Path (Split-Path -Parent $target) ('.Core-rollback-' + [Guid]::NewGuid().ToString('N') + '.tmp')
            Copy-Item -LiteralPath (Join-Path $backup 'TigerClaw.Core.exe') -Destination $restore
            [IO.File]::Replace($restore, $target, [NullString]::Value)
            if ($wasRunning -and -not $NoRestart) { Start-TargetCore $target }
            $manifest.status = 'rolled-back'; $manifest.error = $failure.ToString()
            $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'publish.json') -Encoding UTF8
            Write-Host "Previous Core restored from $backup"
        } catch { Write-Error "Rollback failed; backup retained at ${backup}: $_" -ErrorAction Continue }
    } elseif ($wasRunning -and $target -and -not $NoRestart) {
        # Replacement can fail after stopping the old binary (e.g. antivirus lock).
        try { if (@(Get-TargetProcesses $target).Count -eq 0) { Start-TargetCore $target } } catch { }
    }
    Write-Error $failure -ErrorAction Continue
    exit 1
}
