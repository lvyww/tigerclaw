# Windows PowerShell 5.1 compatible. Each task owns its child process and logs.
function Invoke-BuildTaskGraph {
    param(
        [Parameter(Mandatory = $true)][array]$Tasks,
        [Parameter(Mandatory = $true)][string]$LogDirectory,
        [ValidateRange(1, 16)][int]$MaxParallel = 2
    )
    $ErrorActionPreference = 'Stop'
    $names = @{}
    foreach ($task in $Tasks) {
        if ($task.Name -notmatch '^[A-Za-z0-9_-]+$' -or $names.ContainsKey($task.Name)) {
            throw "Invalid or duplicate task name: $($task.Name)"
        }
        $names[$task.Name] = $true
    }
    foreach ($task in $Tasks) {
        foreach ($dependency in $task.DependsOn) {
            if (-not $names.ContainsKey($dependency)) { throw "Unknown dependency: $dependency" }
        }
    }
    # Validate cycles before launching any command.
    $visited = @{}
    while ($visited.Count -lt $Tasks.Count) {
        $before = $visited.Count
        foreach ($task in $Tasks) {
            if (@($task.DependsOn | Where-Object { -not $visited.ContainsKey($_) }).Count -eq 0) {
                $visited[$task.Name] = $true
            }
        }
        if ($before -eq $visited.Count) { throw 'Build dependency cycle.' }
    }
    [void](New-Item -ItemType Directory -Force -Path $LogDirectory)
    $pending = [Collections.Generic.List[object]]::new()
    foreach ($task in $Tasks) { $pending.Add($task) }
    $running = [Collections.Generic.List[object]]::new()
    $completed = @{}
    $results = [Collections.Generic.List[object]]::new()
    $failed = $false
    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            foreach ($entry in @($running.ToArray())) {
                if (-not $entry.Process.HasExited) { continue }
                $entry.Process.WaitForExit()
                $code = $entry.Process.ExitCode
                $entry.Watch.Stop()
                $result = [pscustomobject]@{
                    Name = $entry.Name; ExitCode = $code
                    Seconds = [math]::Round($entry.Watch.Elapsed.TotalSeconds, 2)
                }
                $results.Add($result)
                $completed[$entry.Name] = $code
                if ($code -ne 0) { $failed = $true }
                Write-Host ("[{0}] exit={1} elapsed={2}s" -f $result.Name, $code, $result.Seconds)
                $entry.Process.Dispose()
                [void]$running.Remove($entry)
            }
            if (-not $failed) {
                foreach ($task in @($pending.ToArray())) {
                    if ($running.Count -ge $MaxParallel) { break }
                    if (@($task.DependsOn | Where-Object { -not $completed.ContainsKey($_) }).Count) { continue }
                    Write-Host "Starting $($task.Name)"
                    $watch = [Diagnostics.Stopwatch]::StartNew()
                    $process = Start-Process -FilePath $task.FilePath -ArgumentList $task.Arguments `
                        -WorkingDirectory $task.WorkingDirectory -PassThru -NoNewWindow `
                        -RedirectStandardOutput (Join-Path $LogDirectory "$($task.Name).log") `
                        -RedirectStandardError (Join-Path $LogDirectory "$($task.Name).err.log")
                    # Cache the native handle while alive. Windows PowerShell's
                    # Start-Process can otherwise report a null ExitCode later.
                    $null = $process.Handle
                    $running.Add([pscustomobject]@{ Name = $task.Name; Process = $process; Watch = $watch })
                    [void]$pending.Remove($task)
                }
            }
            if ($failed -and $running.Count -eq 0) { break }
            if ($running.Count -gt 0) { Start-Sleep -Milliseconds 100 }
        }
    }
    finally {
        # Never return to publishing with unobserved child builds still writing.
        foreach ($entry in $running) {
            $entry.Process.WaitForExit()
            $entry.Process.Dispose()
        }
        $results.ToArray() | Export-Csv -NoTypeInformation -Path (Join-Path $LogDirectory 'timings.csv')
    }
    if ($failed) { throw "Build failed; deployment was not started. Logs: $LogDirectory" }
    return $results.ToArray()
}
