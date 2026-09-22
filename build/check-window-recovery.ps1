param(
    [string]$PathPrefix = 'D:\RetryProxy',
    [string]$LogFile = 'D:\RetryProxy\src\RetryProxy.App\bin\Debug\net9.0-windows10.0.22621.0\logs\retry-proxy.log'
)
# 验收 7：注入三种异常矩形，期待每次都自动恢复且日志各出现一条 "WindowRect {..} -> WindowRect {..}"。
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RecoveryNative
{
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct RECT { public int L, T, R, B; }
}
'@
[RecoveryNative]::SetProcessDPIAware() | Out-Null
$proc = Get-Process RetryProxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PathPrefix*" } | Select-Object -First 1
if (-not $proc) { Write-Error "no RetryProxy process under $PathPrefix"; exit 1 }
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0 -and $proc.MainWindowTitle -eq 'LLM Retry Proxy') { break }
    Start-Sleep -Milliseconds 300
}
$h = $proc.MainWindowHandle
$orig = New-Object RecoveryNative+RECT
[RecoveryNative]::GetWindowRect($h, [ref]$orig) | Out-Null
$before = if (Test-Path $LogFile) { (Select-String -Path $LogFile -Pattern 'WindowRect \{' -SimpleMatch:$false).Count } else { 0 }
Write-Host ("original: {0},{1} {2}x{3}; log lines before: {4}" -f $orig.L, $orig.T, ($orig.R - $orig.L), ($orig.B - $orig.T), $before)

$cases = @(
    @{ x = -32000; y = -32000; w = 160; h = 28 },
    @{ x = $orig.L; y = $orig.T; w = 160; h = 28 },
    @{ x = -32000; y = -32000; w = 1000; h = 700 }
)
$ok = $true
foreach ($c in $cases) {
    [RecoveryNative]::SetWindowPos($h, [IntPtr]::Zero, $c.x, $c.y, $c.w, $c.h, 0x0014) | Out-Null
    Start-Sleep -Milliseconds 1500
    $r = New-Object RecoveryNative+RECT
    [RecoveryNative]::GetWindowRect($h, [ref]$r) | Out-Null
    $restored = ($r.L -eq $orig.L -and $r.T -eq $orig.T -and $r.R -eq $orig.R -and $r.B -eq $orig.B)
    Write-Host ("inject {0},{1} {2}x{3} -> now {4},{5} {6}x{7} restored={8}" -f $c.x, $c.y, $c.w, $c.h, $r.L, $r.T, ($r.R - $r.L), ($r.B - $r.T), $restored)
    if (-not $restored) { $ok = $false }
}
Start-Sleep -Milliseconds 500
$after = (Select-String -Path $LogFile -Pattern 'WindowRect \{').Count
Write-Host ("log lines after: {0} (new {1})" -f $after, ($after - $before))
Select-String -Path $LogFile -Pattern 'WindowRect \{' | Select-Object -Last 3 | ForEach-Object { $_.Line }
if (-not $ok -or ($after - $before) -ne 3) { Write-Error 'window recovery check failed'; exit 1 }
Write-Host 'window recovery check passed'
