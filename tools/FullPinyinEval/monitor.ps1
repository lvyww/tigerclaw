param(
    [Parameter(Mandatory=$true)][string]$ExperimentDirectory,
    [Parameter(Mandatory=$true)][string]$HostExecutable,
    [Parameter(Mandatory=$true)][string]$StopFile
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ExperimentDirectory).TrimEnd('\') + '\'
$hostPath = [IO.Path]::GetFullPath($HostExecutable)
$names = @('TigerClaw.Core.Tests', [IO.Path]::GetFileNameWithoutExtension($hostPath))
$writer = [IO.StreamWriter]::new((Join-Path $root 'memory.jsonl'), $true, [Text.UTF8Encoding]::new($false))
try {
    while (-not (Test-Path -LiteralPath $StopFile)) {
        foreach ($name in $names) {
            foreach ($process in [Diagnostics.Process]::GetProcessesByName($name)) {
                try {
                    $path = [IO.Path]::GetFullPath($process.MainModule.FileName)
                    if ($path -ne $hostPath -and -not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
                    $row = [ordered]@{
                        time = [DateTime]::UtcNow.ToString('o')
                        start = $process.StartTime.ToUniversalTime().ToString('o')
                        pid = $process.Id
                        component = $(if ($path -eq $hostPath) { 'qwen-host' } else { 'decoder-client' })
                        workingSet = $process.WorkingSet64
                        peakWorkingSet = $process.PeakWorkingSet64
                        privateBytes = $process.PrivateMemorySize64
                    }
                    $writer.WriteLine(($row | ConvertTo-Json -Compress))
                } catch [System.ComponentModel.Win32Exception] {
                    # The owned process can exit between enumeration and inspection.
                } catch [System.InvalidOperationException] {
                } finally { $process.Dispose() }
            }
        }
        $writer.Flush()
        Start-Sleep -Milliseconds 2000
    }
} finally { $writer.Dispose() }
