$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Get-Content (Join-Path $root 'BimeTSF2\SampleIME\KeyEventSink.cpp') -Raw
function FunctionBody([string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing $signature" }
    $start = $source.IndexOf('{', $start)
    $depth = 1; $end = $start + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$bypass = FunctionBody 'BOOL CSampleIME::_BypassProtectedInput('
$methods = ''
foreach ($name in @('OnTestKeyDown', 'OnKeyDown', 'OnTestKeyUp', 'OnKeyUp')) {
    $body = FunctionBody "STDAPI CSampleIME::$name("
    $boundary = $body.IndexOf('Global::UpdateModifiers')
    if ($boundary -lt 0 -or $body.IndexOf('_BypassProtectedInput') -gt $boundary) { throw "Late guard: $name" }
    # Compile the actual entry prefix, ending at the first normal-input action.
    $prefix = $body.Substring(0, $boundary)
    $methods += "HRESULT $name(void* pContext, WPARAM wParam, LPARAM lParam, BOOL* pIsEaten) $prefix ++normalPath; return S_OK; }`n"
}
$code = @'
#include <windows.h>
#include <deque>
#include <cstdio>
#include <cstdlib>
constexpr UINT_PTR kFailedKeyFlushTimerId = 5;
int resets = 0;
void ResetCapsCompensationState(const char*) { ++resets; }
struct Probe {
 bool disabled = true;
 std::deque<int> _failedKeyQueue{1,2};
 bool response = true, event = true, refresh = true;
 int _keyUpForwardBudget = 4, normalPath = 0;
 HWND _msgWndHandle = nullptr;
 BOOL _IsKeyboardDisabled(void*) { return disabled; }
 void _ClearPendingResponseCache() { response = false; }
 void _ClearPendingKeyEvent() { event = false; }
 void _CancelCompositionRefresh() { refresh = false; }
'@
$code += "BOOL _BypassProtectedInput(void* context) $bypass`n$methods`n};`n"
$code += @'
void Check(bool value) { if (!value) std::exit(1); }
int main() {
 using Key = HRESULT (Probe::*)(void*, WPARAM, LPARAM, BOOL*);
 for (Key key : {&Probe::OnTestKeyDown,&Probe::OnKeyDown,&Probe::OnTestKeyUp,&Probe::OnKeyUp}) {
  Probe p; BOOL eaten = TRUE;
  Check((p.*key)(nullptr, 'A', 0, &eaten) == S_OK);
  Check(!eaten && !p.normalPath && p._failedKeyQueue.empty());
  Check(!p.response && !p.event && !p.refresh && !p._keyUpForwardBudget);
  p.disabled = false;
  Check((p.*key)(nullptr, 'A', 0, &eaten) == S_OK && p.normalPath == 1);
 }
 Check(resets == 4);
 std::puts("Protected input entry/queue/cache tests passed");
}
'@
$out = Join-Path $env:TEMP ('TigerClaw Protected Input ' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $out)
[IO.File]::WriteAllText("$out\test.cpp", $code)
$batch = @"
@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
cl /nologo /EHsc /std:c++17 "$out\test.cpp" /Fe:"$out\test.exe" /Fo:"$out\test.obj" /link user32.lib
if errorlevel 1 exit /b 1
"$out\test.exe"
exit /b %errorlevel%
"@
[IO.File]::WriteAllText("$out\run.bat", ($batch -replace "\r?\n", "`r`n"))
& $env:ComSpec /d /c "`"$out\run.bat`""
if ($LASTEXITCODE -ne 0) { throw 'Protected input tests failed' }
Write-Host "Isolated test source and executable: $out"
