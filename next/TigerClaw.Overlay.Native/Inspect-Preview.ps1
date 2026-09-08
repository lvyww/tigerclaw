param(
    [Parameter(Mandatory = $true)][int]$PreviewProcessId,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $PreviewProcessId
if ([IO.Path]::GetFileName($process.Path) -ne 'TigerClaw.Overlay.Native.Preview.exe') {
    throw 'Only the isolated native preview can be inspected.'
}
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class NativePreviewInspector {
    public delegate bool Callback(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static IntPtr Find(uint processId, string className) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, data) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != processId) return true;
            var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
            if (name.ToString() == className) { result = window; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@
$null = [IO.Directory]::CreateDirectory($OutputDirectory)
$oldDpi = [NativePreviewInspector]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$foreground = [NativePreviewInspector]::GetForegroundWindow()
$candidate = [NativePreviewInspector]::Find($PreviewProcessId, 'TigerClaw.Native.Candidate.v1')
$status = [NativePreviewInspector]::Find($PreviewProcessId, 'TigerClaw.Native.Status.v1')
if ($candidate -eq [IntPtr]::Zero -or $status -eq [IntPtr]::Zero) { throw 'Preview windows not created.' }
$results = @()
try {
    foreach ($mode in @('horizontal', 'vertical', 'code-only', 'horizontal-restored')) {
        if ($mode -ne 'horizontal') {
            $null = [NativePreviewInspector]::SendMessage($candidate, 0x208, [IntPtr]::Zero, [IntPtr]::Zero)
        }
        Start-Sleep -Milliseconds 250
        $rect = New-Object NativePreviewInspector+Rect
        if (-not [NativePreviewInspector]::GetWindowRect($candidate, [ref]$rect)) { throw 'Candidate bounds unavailable.' }
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        if (-not [NativePreviewInspector]::IsWindowVisible($candidate) -or $width -lt 2 -or $height -lt 2) {
            throw "Candidate not rendered in $mode"
        }
        $bitmap = New-Object Drawing.Bitmap($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $bitmap.Save((Join-Path $OutputDirectory "$mode.png"), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
        $results += [pscustomobject]@{ mode=$mode; x=$rect.Left; y=$rect.Top; width=$width; height=$height }
        if ([NativePreviewInspector]::GetForegroundWindow() -ne $foreground) { throw "Focus changed during $mode" }
    }
    if ($results[0].width -ne $results[3].width -or $results[0].height -ne $results[3].height) {
        throw 'Returning to horizontal mode did not restore the original layout.'
    }
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $null = [NativePreviewInspector]::PostMessage($status, 0x205, [IntPtr]::Zero, [IntPtr]::Zero)
        $menu = [IntPtr]::Zero
        for ($poll = 0; $poll -lt 40; $poll++) {
            Start-Sleep -Milliseconds 25
            $menu = [NativePreviewInspector]::Find($PreviewProcessId, '#32768')
            if ($menu -ne [IntPtr]::Zero -and [NativePreviewInspector]::IsWindowVisible($menu)) { break }
        }
        if ($menu -eq [IntPtr]::Zero -or -not [NativePreviewInspector]::IsWindowVisible($menu)) { throw 'Native menu did not open.' }
        if ([NativePreviewInspector]::GetForegroundWindow() -ne $foreground) { throw 'Menu opening changed foreground focus.' }
        $null = [NativePreviewInspector]::SendMessage($status, 0x1F, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        if ([NativePreviewInspector]::IsWindowVisible($menu)) { throw 'Native menu did not dismiss on cancellation.' }
    }
    $process.Refresh()
    $report = [pscustomobject]@{
        processId=$PreviewProcessId; windows=$results; focusPreserved=$true; menuOpenCancelCycles=2;
        workingSetBytes=$process.WorkingSet64; privateCommitBytes=$process.PrivateMemorySize64;
        handles=$process.HandleCount; binarySHA256=(Get-FileHash -LiteralPath $process.Path).Hash
        workload='Synthetic preview; no Core IPC, no typing audio initialized'
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'preview-inspection.json') -Encoding UTF8
    $report | ConvertTo-Json -Depth 5
}
finally { $null = [NativePreviewInspector]::SetThreadDpiAwarenessContext($oldDpi) }
