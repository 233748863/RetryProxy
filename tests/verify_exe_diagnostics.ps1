param()
# 直接提取真实验收函数，用确定性的窗口诊断替身覆盖“窗口已销毁”分支，不启动应用。
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'verify_exe.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'verify_exe.ps1 contains syntax errors' }
$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-TrayCondition' }, $true)
if ($null -eq $function) { throw 'Wait-TrayCondition was not found' }
. ([scriptblock]::Create($function.Extent.Text))
Add-Type @'
using System;
using System.ComponentModel;
public static class RetryProxyTrayVerification {
    public static bool FailBounds;
    public struct Rect { public int Left, Top, Right, Bottom; }
    public static Rect Bounds(IntPtr window) {
        if (FailBounds) throw new Win32Exception(1400);
        return new Rect { Left=10, Top=20, Right=910, Bottom=620 };
    }
    public static bool IsWindowVisible(IntPtr window) { return true; }
    public static bool IsIconic(IntPtr window) { return false; }
}
'@
function Assert-Timeout([string]$Expected) {
    $message = $null
    try { Wait-TrayCondition { $false } 'Original wait failure' 0 }
    catch { $message = $_.Exception.Message }
    if ($null -eq $message -or -not $message.StartsWith($Expected, [StringComparison]::Ordinal)) {
        throw "Original failure was lost: $message"
    }
    return $message
}
$VerifyTray = $true; $mainWindow = [IntPtr]::new(1)
[RetryProxyTrayVerification]::FailBounds = $true
Wait-TrayCondition { $true } 'A satisfied condition must not fail' 0
$message = Assert-Timeout 'Original wait failure (window diagnostics unavailable:'
if ($message.Contains('bounds=')) { throw 'Invalid window produced invented bounds' }
[RetryProxyTrayVerification]::FailBounds = $false
$null = Assert-Timeout 'Original wait failure (visible=True, minimized=False, bounds=10,20,910,620)'
$mainWindow = [IntPtr]::Zero
if ((Assert-Timeout 'Original wait failure') -cne 'Original wait failure') { throw 'Zero window handle triggered diagnostics' }
$mainWindow = [IntPtr]::new(1); $VerifyTray = $false
if ((Assert-Timeout 'Original wait failure') -cne 'Original wait failure') { throw 'Non-tray verification triggered window diagnostics' }
Write-Host 'PASS: five wait-diagnostic cases, including an invalid window handle.'
