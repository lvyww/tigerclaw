param(
    [Parameter(Mandatory = $true)][string]$BatchPath,
    [ValidateRange(1, 16)][int]$MaxParallel = 2
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_task_graph.ps1')
try {
    $root = Split-Path -Parent $BatchPath
    $logDirectory = Join-Path $root ('next\_run\ReleaseArm64\logs\' +
        (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $env:BUILD_WORKER_JOBS = [string][math]::Max(1, [math]::Min(8,
        [math]::Floor([Environment]::ProcessorCount / $MaxParallel)))
    $graph = @(
        @('Embed', @()), @('Core', @()), @('Overlay', @()), @('Sentence', @()),
        @('Dialog', @('Overlay')),
        @('Hook', @('Embed')), @('TsfWin32', @('Embed')),
        @('TsfX64', @('Embed')), @('TsfArm64', @('Embed')),
        @('Server', @('Embed')), @('Wrapper', @('Embed'))
    )
    $tasks = foreach ($node in $graph) {
        [pscustomobject]@{
            Name = $node[0]; DependsOn = @($node[1]); FilePath = $env:ComSpec
            Arguments = '/d /s /c ""' + $BatchPath + '" --worker ' + $node[0] + '"'
            WorkingDirectory = $root
        }
    }
    Write-Host "Build concurrency: $MaxParallel; logs: $logDirectory"
    Invoke-BuildTaskGraph -Tasks $tasks -LogDirectory $logDirectory -MaxParallel $MaxParallel |
        Format-Table -AutoSize
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
