#requires -Version 7.0
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '../src/RetryProxy.App/bin/x64/Debug/net9.0-windows10.0.22621.0/RetryProxy.exe'),
    [string]$OutputDirectory = ''
)
# 独立临时配置验证实际按钮、深浅变化及重启持久化，不操作正在使用的实例。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime_files.ps1')
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('.tmp/appearance-' + [guid]::NewGuid().ToString('N')) }
$runtime = Join-Path ([IO.Path]::GetTempPath()) ('RetryProxyM4-' + [guid]::NewGuid().ToString('N'))
$variables = @('RETRY_PROXY_CONFIG_JSON','RETRY_PROXY_UI_TEST_ROOT','CLAUDE_CONFIG_DIR','CODEX_HOME','RETRY_PROXY_CLAUDE_CLI','RETRY_PROXY_CODEX_CLI')
$saved = @{}; foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$app = $null; $window = [IntPtr]::Zero; $passed = $false
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AppearanceWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
function Wait-For([scriptblock]$Condition, [string]$Failure) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (!(& $Condition)) {
        if ($app -and $app.HasExited) { throw "测试程序已退出：$Failure" }
        if ($clock.Elapsed.TotalSeconds -gt 10) { throw $Failure }
        Start-Sleep -Milliseconds 100
    }
}
function Find-Control([string]$Id) {
    $element = [Windows.Automation.AutomationElement]::FromHandle($window)
    return $element.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Focus-App {
    $null = [AppearanceWindow]::SetForegroundWindow($window)
    Wait-For { [AppearanceWindow]::GetForegroundWindow() -eq $window } '测试窗口未取得前台焦点'
}
function Navigate([string]$Name) {
    Focus-App
    $ready = @{ Item = $null }
    Wait-For {
        $element = [Windows.Automation.AutomationElement]::FromHandle($window)
        $items = $element.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name))
        $ready.Item = @($items | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem -and $_.Current.IsEnabled -and !$_.Current.IsOffscreen })[0]
        $null -ne $ready.Item
    } "找不到可用导航项：$Name"
    $ready.Item.SetFocus()
    $null = [RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]::new(13),[IntPtr]0x001C0001)
    $null = [RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]::new(13),[IntPtr]0xC01C0001)
    $marker = switch ($Name) { '首页' { 'HomeProvidersCard' }; '软件设置' { 'SwitchAppearance' }; default { 'ProxyState' } }
    Wait-For { $null -ne (Find-Control $marker) } "未打开页面：$Name"
}
$configDbReaderScript = @'
import sqlite3, sys
connection = sqlite3.connect(sys.argv[1], timeout=5)
try:
    row = connection.execute("SELECT value FROM config WHERE key = ?", (sys.argv[2],)).fetchone()
finally:
    connection.close()
sys.stdout.write('' if row is None else row[0])
'@
function Read-ConfigDb([string]$Key) {
    $db = Join-Path $runtime 'User/config.db'
    if (!(Test-Path -LiteralPath $db)) { throw '配置库尚未创建' }
    $reader = Join-Path $runtime 'read-config-db.py'
    if (!(Test-Path -LiteralPath $reader)) { [IO.File]::WriteAllText($reader, $configDbReaderScript) }
    $output = @(& python -X utf8 $reader $db $Key 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "读取配置库失败：$($output -join ' ')" }
    return ($output -join "`n")
}
function Theme {
    # 主题存在配置库 common 行；SQLite 读写互不阻塞，无需旧文件时代的共享句柄。
    return (Read-ConfigDb 'common' | ConvertFrom-Json).currentThemeType
}
function Capture([string]$Name) {
    Focus-App; Start-Sleep -Milliseconds 350
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    $image = [Drawing.Bitmap]::new($bounds.Right-$bounds.Left,$bounds.Bottom-$bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$image.Size)
        $image.Save((Join-Path $OutputDirectory ($Name + '.png')))
        $values = foreach ($fraction in @(0.25,0.35,0.45,0.55,0.65)) {
            $pixel = $image.GetPixel([int]($image.Width*0.15),[int]($image.Height*$fraction))
            ($pixel.R+$pixel.G+$pixel.B)/3.0
        }
        return ($values | Sort-Object)[2]
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Switch-Theme {
    Focus-App
    $before = Theme
    $button = Find-Control 'SwitchAppearance'
    if (!$button -or !$button.Current.IsEnabled -or $button.Current.IsOffscreen) { throw '切换外观按钮不可用' }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { (Theme) -ne $before } '点击切换外观后，主题设置没有变化'
    return Theme
}
function Start-App {
    $script:app = Start-Process -FilePath (Join-Path $runtime 'RetryProxy.exe') -WorkingDirectory $runtime -PassThru
    $null = $app.Handle
    Wait-For { $script:window=[RetryProxyTrayVerification]::FindWindow($app.Id,'LLM Retry Proxy',$null); $window -ne [IntPtr]::Zero } '测试主窗口未出现'
    Wait-For { $null -ne (Find-Control 'HomeProvidersCard') } '启动时未显示首页'
    Navigate '供应商'
}
function Stop-App {
    $null = [RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero)
    if (!$app.WaitForExit(25000)) { throw '测试程序未正常退出' }
    if ($app.ExitCode -ne 0) { throw '测试程序退出码异常' }
    $script:app = $null
}
try {
    New-Item -ItemType Directory -Path $runtime,(Join-Path $runtime 'User'),$OutputDirectory | Out-Null
    Remove-Item Env:RETRY_PROXY_CONFIG_JSON -ErrorAction SilentlyContinue
    $env:RETRY_PROXY_UI_TEST_ROOT = $runtime
    $env:CLAUDE_CONFIG_DIR = Join-Path $runtime 'claude'; $env:CODEX_HOME = Join-Path $runtime 'codex'
    $env:RETRY_PROXY_CLAUDE_CLI = Join-Path $runtime 'missing-claude.exe'; $env:RETRY_PROXY_CODEX_CLI = Join-Path $runtime 'missing-codex.exe'
    New-Item -ItemType Directory -Path $env:CLAUDE_CONFIG_DIR,$env:CODEX_HOME | Out-Null
    $proxy = @{schema_version=7;selected_route_id='appearance-codex';providers=@();routes=@(
        @{id='appearance-codex';name='Codex';client_type='codex';listen_port=28080;current_provider_id='';current_key_id='';local_token='00112233445566778899aabbccddeeff';keepalive_enabled=$false},
        @{id='appearance-claude';name='Claude Code';client_type='claude';listen_port=28081;current_provider_id='';current_key_id='';local_token='ffeeddccbbaa99887766554433221100';keepalive_enabled=$false}
    )}
    @{proxy=$proxy;commonConfig=@{clientSetupCompleted=$true;isFirstRun=$false;exitToTray=$false;startMinimized=$false;currentThemeType=0};otherConfig=@{uiCultureInfoName='zh-Hans'}} |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runtime 'User/config.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $root 'src/RetryProxy.App/User/I18n') -Destination (Join-Path $runtime 'User/I18n') -Recurse
    Copy-RetryProxyRuntime -ExePath $ExePath -DestinationDirectory $runtime
    Start-App
    Navigate '软件设置'
    $initial = Theme; $initialBrightness = Capture 'initial-dark'
    Navigate '首页'; $null = Capture 'home-dark'; Navigate '软件设置'
    $steps = @(); $lightTheme = $null; $seenThemes = @($initial); $cycleClosed = $false
    for ($index=1; $index -le 6; $index++) {
        $theme = Switch-Theme
        $brightness = Capture "switch-$index"
        $steps += @{theme=$theme;brightness=$brightness}
        if ($brightness - $initialBrightness -ge 60) { $lightTheme = $theme }
        # Windows 11 可从无背景态进入四态循环；不要求循环一定回到最初的无背景态。
        if ($seenThemes -contains $theme) { $cycleClosed = $true; break }
        $seenThemes += $theme
    }
    if (!$cycleClosed -or $null -eq $lightTheme) { throw '没有完成包含深浅视觉变化的主题循环' }
    Navigate '供应商'; Navigate '软件设置'
    for ($index=0; $index -lt 6; $index++) { if ((Switch-Theme) -eq $lightTheme) { break } }
    if ((Theme) -ne $lightTheme) { throw '切页返回后外观按钮未切换到已验证的浅色主题' }
    Navigate '首页'; $null = Capture 'home-light'
    Stop-App
    Start-App
    Navigate '软件设置'
    if ((Theme) -ne $lightTheme) { throw '重启后没有保留主题设置' }
    if ((Capture 'restarted-light') - $initialBrightness -lt 60) { throw '重启后的实际窗口未使用已保存的浅色外观' }
    Navigate '首页'; $null = Capture 'home-restarted-light'
    Stop-App
    @{passed=$true;initialTheme=$initial;steps=$steps;restartTheme=$lightTheme;runtime=$runtime} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    $passed = $true
    Write-Host "PASS: 外观按钮、深浅循环、切页后操作与重启持久化；记录：$OutputDirectory"
} catch {
    if ($app -and !$app.HasExited) { try { $null = Capture 'failure' } catch {} }
    Write-Host "测试现场保留：$runtime；截图：$OutputDirectory"
    throw
} finally {
    if ($app -and !$app.HasExited) { $app.Kill(); $null = $app.WaitForExit(5000) }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    if ($passed) {
        $directory = Get-Item -LiteralPath $runtime
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '拒绝清理含链接的测试目录' }
        Remove-Item -LiteralPath $runtime -Recurse -Force
    }
}
