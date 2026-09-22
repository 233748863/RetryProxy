param(
    [string]$PathPrefix = 'D:\RetryProxy',
    [string]$OutFile = 'D:\RetryProxy\build\capture.png',
    [int]$SettleMs = 800,
    [string]$Click = '',
    [int]$AfterClickMs = 700,
    [string]$ScrollAt = '',
    [int]$ScrollTicks = -5,
    [switch]$EscapeFirst
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CaptureNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public struct RECT { public int L, T, R, B; }
}
'@

[CaptureNative]::SetProcessDPIAware() | Out-Null

$proc = Get-Process RetryProxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PathPrefix*" } | Select-Object -First 1
if (-not $proc) { Write-Error "no RetryProxy process under $PathPrefix"; exit 1 }

# 调试版会先弹一个控制台窗口，主窗口句柄要等标题就绪再取。
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0 -and $proc.MainWindowTitle -eq 'LLM Retry Proxy') { break }
    Start-Sleep -Milliseconds 300
}
$h = $proc.MainWindowHandle
if ($h -eq 0 -or $proc.MainWindowTitle -ne 'LLM Retry Proxy') { Write-Error "process $($proc.Id) has no main window (title '$($proc.MainWindowTitle)')"; exit 1 }

if ([CaptureNative]::GetForegroundWindow() -ne $h) {
    [CaptureNative]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds $SettleMs
}

$r = New-Object CaptureNative+RECT
[CaptureNative]::GetWindowRect($h, [ref]$r) | Out-Null

if ($EscapeFirst) {
    [CaptureNative]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [CaptureNative]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

if ($Click) {
    $xy = $Click.Split(',')
    $cx = $r.L + [int]$xy[0]; $cy = $r.T + [int]$xy[1]
    [CaptureNative]::SetCursorPos($cx, $cy) | Out-Null
    Start-Sleep -Milliseconds 120
    [CaptureNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [CaptureNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $AfterClickMs
    [CaptureNative]::GetWindowRect($h, [ref]$r) | Out-Null
}

if ($ScrollAt) {
    $xy = $ScrollAt.Split(',')
    $sx = $r.L + [int]$xy[0]; $sy = $r.T + [int]$xy[1]
    [CaptureNative]::SetCursorPos($sx, $sy) | Out-Null
    Start-Sleep -Milliseconds 120
    $delta = [int]$ScrollTicks * 120
    [CaptureNative]::mouse_event(0x0800, 0, 0, $delta, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $AfterClickMs
}

$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($OutFile)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $OutFile ($w x $hh) from pid $($proc.Id)"
