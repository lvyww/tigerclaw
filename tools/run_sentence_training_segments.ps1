param(
    [string]$PythonPath = "$env:LOCALAPPDATA\TigerClawML\venv-directml\Scripts\python.exe",
    [string]$DataPath = "C:\Archive\tigerclaw_sentence_ml\pilot200m",
    [string]$OutputPath = "C:\Archive\tigerclaw_sentence_ml\model10m",
    [int]$SegmentSteps = 10000,
    [int]$SegmentCount = 4
)

$ErrorActionPreference = "Continue"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$trainingScript = Join-Path $PSScriptRoot "train_sentence_neural.py"
$runnerLog = Join-Path $OutputPath "segment-runner.log"

Set-Location $repoRoot
for ($segment = 1; $segment -le $SegmentCount; $segment++) {
    $started = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$started] Starting segment $segment/$SegmentCount" |
        Tee-Object -FilePath $runnerLog -Append
    & $PythonPath $trainingScript `
        --data $DataPath `
        --output $OutputPath `
        --device directml `
        --train-tokens 200000000 `
        --batch-size 64 `
        --context-length 64 `
        --validation-interval 2000 `
        --checkpoint-interval 10000 `
        --log-interval 100 `
        --validation-batches 20 `
        --validation-batch-size 64 `
        --resume `
        --stop-after-steps $SegmentSteps 2>&1 |
        Tee-Object -FilePath $runnerLog -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Training segment $segment failed with exit code $LASTEXITCODE"
    }
    Start-Sleep -Seconds 5
}

$finished = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
"[$finished] All training segments completed" |
    Tee-Object -FilePath $runnerLog -Append
