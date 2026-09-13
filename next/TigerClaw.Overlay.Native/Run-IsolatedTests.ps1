$ErrorActionPreference = 'Stop'
$logPath = Join-Path $PSScriptRoot 'native-overlay-tests.log'
$testNames = @('overlay_model_tests.exe', 'overlay_transport_tests.exe')
$results = @("OS: $([Environment]::OSVersion.VersionString)", "Process architecture: $env:PROCESSOR_ARCHITECTURE")
$failed = $false
foreach ($testName in $testNames) {
    $testPath = Join-Path $PSScriptRoot $testName
    $results += "=== $testName ==="
    $process = $null
    try {
        if (-not (Test-Path -LiteralPath $testPath -PathType Leaf)) {
            throw "Missing test executable: $testPath"
        }
        $results += "SHA256: $((Get-FileHash -LiteralPath $testPath -Algorithm SHA256).Hash)"
        $stdoutPath = Join-Path $PSScriptRoot "$testName.stdout.log"
        $stderrPath = Join-Path $PSScriptRoot "$testName.stderr.log"
        # Separate redirected files avoid PowerShell 5.1 treating native stderr
        # as a terminating error before the test result can be recorded.
        $process = Start-Process -FilePath $testPath -WorkingDirectory $PSScriptRoot -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $null = $process.Handle
        if (-not $process.WaitForExit(45000)) {
            # Only this test process, never a runtime process selected by name.
            $process.Kill()
            $process.WaitForExit()
            throw "Test exceeded 45 seconds"
        }
        $process.WaitForExit()
        $code = $process.ExitCode
        if ($null -eq $code) { throw 'Test exit code unavailable' }
        $results += @(Get-Content -LiteralPath $stdoutPath -Encoding UTF8)
        $results += @(Get-Content -LiteralPath $stderrPath -Encoding UTF8)
        $results += "Exit code: $code"
        if ($code -ne 0) { $failed = $true }
    }
    catch {
        $results += "Runner failure: $($_.Exception.Message)"
        $failed = $true
    }
    finally {
        if ($null -ne $process) { $process.Dispose() }
        $results | Set-Content -LiteralPath $logPath -Encoding UTF8
    }
}
$results | Set-Content -LiteralPath $logPath -Encoding UTF8
$results | ForEach-Object { Write-Host $_ }
Write-Host "Log: $logPath"
if ($failed) { exit 1 }
