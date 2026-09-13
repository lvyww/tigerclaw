$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_task_graph.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('TigerClaw Build Tests ' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $scratch)
function Assert($value, $message) { if (-not $value) { throw $message } }
function Task($name, $dependencies, $code) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
    [pscustomobject]@{
        Name = $name; DependsOn = @($dependencies); FilePath = "$PSHOME\powershell.exe"
        Arguments = '-NoProfile -EncodedCommand ' + $encoded; WorkingDirectory = $scratch
    }
}
try {
    foreach ($limit in @(1, 2)) {
        $dir = Join-Path $scratch "limit-$limit"
        [void](New-Item -ItemType Directory -Path $dir)
        $tasks = @(
            (Task 'Core' @() "[IO.File]::WriteAllText('$dir\Core.start',[DateTime]::UtcNow.Ticks); Start-Sleep -Milliseconds 700; [IO.File]::WriteAllText('$dir\Core.end',[DateTime]::UtcNow.Ticks)"),
            (Task 'UI' @() "[IO.File]::WriteAllText('$dir\UI.start',[DateTime]::UtcNow.Ticks); Start-Sleep -Milliseconds 700; [IO.File]::WriteAllText('$dir\UI.end',[DateTime]::UtcNow.Ticks)"),
            (Task 'Embed' @('Core') "if (-not (Test-Path '$dir\Core.end')) { exit 9 }; [IO.File]::WriteAllText('$dir\Embed.end','ok')"),
            (Task 'Native' @('Embed') "if (-not (Test-Path '$dir\Embed.end')) { exit 9 }; exit 0")
        )
        $results = @(Invoke-BuildTaskGraph $tasks (Join-Path $dir 'logs') $limit)
        Assert ($results.Count -eq 4) 'Not all tasks completed.'
        $overlap = [long](Get-Content "$dir\UI.start") -lt [long](Get-Content "$dir\Core.end")
        Assert ($overlap -eq ($limit -eq 2)) 'Concurrency/serial scheduling mismatch.'
    }
    $failureTasks = @(
        (Task 'Fail' @() 'exit 7'),
        (Task 'AlreadyRunning' @() "Start-Sleep -Milliseconds 900; [IO.File]::WriteAllText('$scratch\drained','ok')"),
        (Task 'Deploy' @('Fail') "[IO.File]::WriteAllText('$scratch\deployed','bad')")
    )
    $rejected = $false
    try { Invoke-BuildTaskGraph $failureTasks (Join-Path $scratch 'failure') 2 | Out-Null }
    catch { $rejected = $true }
    Assert $rejected 'Failure was ignored.'
    Assert (-not (Test-Path "$scratch\deployed")) 'Dependent task ran after failure.'
    Assert (Test-Path "$scratch\drained") 'Returned before active child completed.'
    $cycle = @((Task 'A' @('B') 'exit 0'), (Task 'B' @('A') 'exit 0'))
    $rejected = $false
    try { Invoke-BuildTaskGraph $cycle (Join-Path $scratch 'cycle') 2 | Out-Null }
    catch { $rejected = $true }
    Assert $rejected 'Dependency cycle accepted.'

    # Exercise the real cmd worker quoting with a path containing spaces.
    $batch = Join-Path $scratch 'worker fixture.bat'
    [IO.File]::WriteAllText($batch, "@echo off`r`nif not `"%~1`"==`"--worker`" exit /b 8`r`nexit /b 0`r`n", [Text.Encoding]::ASCII)
    & "$PSHOME\powershell.exe" -NoProfile -File (Join-Path $PSScriptRoot 'publish_arm64_build.ps1') -BatchPath $batch -MaxParallel 2
    Assert ($LASTEXITCODE -eq 0) 'Production graph/worker command failed.'

    # Test timestamp preservation on copies only, never the live header.
    $header = Join-Path $scratch 'EmbeddedBuildInfo.h'
    Copy-Item (Join-Path $PSScriptRoot '..\BimeTSF2\SampleIME\EmbeddedBuildInfo.h') $header
    $update = Join-Path $PSScriptRoot 'update_embedded_build_info.ps1'
    & $update -Path $header -TextLogEnabled 0
    $stamp = (Get-Item $header).LastWriteTimeUtc
    Start-Sleep -Milliseconds 100
    & $update -Path $header -TextLogEnabled 0
    Assert ((Get-Item $header).LastWriteTimeUtc -eq $stamp) 'Unchanged header was rewritten.'
    foreach ($enabled in @(0, 1)) {
        & $update -Path $header -TextLogEnabled $enabled
        $raw = Get-Content $header -Raw
        foreach ($flag in @('TEXT', 'VERBOSE')) {
            Assert ($raw -match ("#define BIME_EMBED_${flag}_LOG_ENABLED $enabled\b")) 'Log flag was not updated.'
        }
        Assert ($raw -notmatch 'TRIAL_EXPIRE|XOR_KEY') 'Obsolete expiry metadata remains.'
        Assert ($raw -match '#define BIME_EMBED_PROTOCOL_VERSION 2') 'Protocol version was changed.'
    }
    Write-Host "All publish scheduler tests passed. Isolated logs: $scratch"
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
