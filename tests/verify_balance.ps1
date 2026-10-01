#requires -Version 7.0
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\RetryProxy.App\bin\x64\Debug\net9.0-windows10.0.22621.0\RetryProxy.exe'),
    [switch]$Screenshot
)
# M6：真实临时配置、私有桌面、本机假上游；只操作本脚本启动的进程。
# -Screenshot 才使用当前桌面，图片只截测试窗口，输出到 .tmp/M6-<唯一编号>/。
# RetryProxyM4- 是现有客户端隔离校验的白名单前缀，不能改成 RetryProxyM6-。
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runtime = Join-Path $tempRoot ('RetryProxyM4-' + [Guid]::NewGuid().ToString('N'))
$shots = Join-Path $PSScriptRoot ('..\.tmp\M6-' + [Guid]::NewGuid().ToString('N'))
$configPath = Join-Path $runtime 'User\config.json'
$eventsPath = Join-Path $runtime 'upstream-events.jsonl'
$scenarioPath = Join-Path $runtime 'scenario.txt'
$releasePath = Join-Path $runtime 'release-balance'
$environmentNames = @(
    'RETRY_PROXY_CONFIG_JSON', 'RETRY_PROXY_UI_TEST_ROOT', 'CLAUDE_CONFIG_DIR', 'CODEX_HOME',
    'RETRY_PROXY_CLAUDE_CLI', 'RETRY_PROXY_CODEX_CLI', 'RETRY_PROXY_TEST_PREPARATION_GATE',
    'UPSTREAM_BASE_URL', 'RETRY_PROXY_PORT', 'RETRY_MAX_RETRIES', 'RETRY_TIMEOUT_SECONDS',
    'RETRY_GENERATION_TIMEOUT_SECONDS', 'RETRY_TOTAL_TIMEOUT_SECONDS', 'RETRY_BASE_DELAY_SECONDS', 'RETRY_MAX_DELAY_SECONDS'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$process = $null
$job = $null
$window = [IntPtr]::Zero
$passed = $false
$secrets = @{
    'u-main' = 'sk-balance-fixture-main-only'; 'u-zero' = 'sk-balance-fixture-zero-only'
    'u-invalid' = 'sk-balance-fixture-invalid-only'; 'a-main' = 'sk-balance-fixture-auto-only'
    'b-main' = 'sk-balance-fixture-billing-only'; 'b-unlimited' = 'sk-balance-fixture-unlimited-only'
    'n-main' = 'sk-balance-fixture-none-only'
}
$keyProviders = @{
    'u-main' = 'usage'; 'u-zero' = 'usage'; 'u-invalid' = 'usage'; 'a-main' = 'auto'
    'b-main' = 'billing'; 'b-unlimited' = 'billing'; 'n-main' = 'none'
}
$codexToken = [Guid]::NewGuid().ToString('N')
$claudeToken = [Guid]::NewGuid().ToString('N')
$modeValues = @('auto', 'usage', 'user_balance', 'openai_billing', 'none')
$modeNames = @('自动识别', '通用用量接口', '余额接口', 'OpenAI 兼容账单接口', '不查询')

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class M6Window {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
'@

function Wait-For([scriptblock]$Condition, [string]$Message, [int]$Seconds = 30) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($process -and $process.HasExited) { throw "测试程序意外退出：$Message" }
        if ($job -and $job.State -in @('Failed', 'Stopped', 'Completed')) { throw '本机假上游意外停止' }
        try { if (& $Condition) { return } }
        catch [Windows.Automation.ElementNotAvailableException] { } # 刷新重建控件时，下一轮重新定位。
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Message
}

function Find-Elements([string]$Value, [switch]$ByName) {
    $property = if ($ByName) { [Windows.Automation.AutomationElement]::NameProperty } else { [Windows.Automation.AutomationElement]::AutomationIdProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($property, $Value)
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    foreach ($element in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) { $element }
    # 菜单在独立弹窗中，枚举范围仍严格限制为隔离进程。
    foreach ($handle in [RetryProxyTrayVerification]::WindowsForProcess($process.Id)) {
        if ($handle -eq $window -or -not [RetryProxyTrayVerification]::IsWindowVisible($handle)) { continue }
        $popup = [Windows.Automation.AutomationElement]::FromHandle($handle)
        foreach ($element in $popup.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) { $element }
    }
}

function Get-Control([string]$Id) {
    $elements = @(Find-Elements $Id)
    if ($elements.Count -ne 1) { throw "控件数量应为 1：$Id，实际为 $($elements.Count)" }
    return $elements[0]
}

function Invoke-Element($Element) {
    if (-not $Element.Current.IsEnabled) { throw "控件尚未启用：$($Element.Current.AutomationId)" }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
    if ($Element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
    if ($Element.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem) {
        $Element.SetFocus()
        $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0100, [UIntPtr]::new(13), [IntPtr]0x001C0001)
        $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0101, [UIntPtr]::new(13), [IntPtr]::new([int64]0xC01C0001))
        return
    }
    throw "控件不支持操作：$($Element.Current.AutomationId)"
}

function Invoke-Control([string]$Id) {
    Wait-For { @(Find-Elements $Id | Where-Object { $_.Current.IsEnabled }).Count -eq 1 } "缺少可操作控件：$Id"
    Invoke-Element (Get-Control $Id)
}

function Find-Menu([string]$Prefix) {
    # WPF 子菜单可同时从父弹窗与自身句柄访问；按同一个自动化节点去重。
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($handle in [RetryProxyTrayVerification]::WindowsForProcess($process.Id)) {
        if (-not [RetryProxyTrayVerification]::IsWindowVisible($handle)) { continue }
        $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)
        foreach ($item in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($item.Current.Name.StartsWith($Prefix, [StringComparison]::Ordinal) -and -not $item.Current.IsOffscreen -and
                $seen.Add(($item.GetRuntimeId() -join ','))) { $item }
        }
    }
}

function Get-ScrollHost([string]$ScrollId) {
    $scroll = Get-Control $ScrollId
    $ancestor = $scroll
    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
    while ($ancestor) {
        $pattern = $null
        if ($ancestor.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and
            $pattern.Current.VerticallyScrollable) { return $ancestor }
        $ancestor = $walker.GetParent($ancestor)
    }
    return $scroll
}

function Get-Viewport($Scroll) {
    $viewport = $Scroll.Current.BoundingRectangle
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    $viewport.Intersect([Windows.Rect]::new($bounds.Left, $bounds.Top, $bounds.Right - $bounds.Left, $bounds.Bottom - $bounds.Top))
    return $viewport
}

function Show-Control([string]$Id, [string]$ScrollId = 'PageScroll') {
    # NavigationView 可能承担页面的外层滚动；内层 PageScroll 的矩形可大于窗口，不能当作可见视口。
    Wait-For { @(Find-Elements $Id).Count -eq 1 } "缺少控件：$Id"
    # WPF-UI 外层滚动器不暴露 ScrollPattern；聚焦可交互控件会请求父层 BringIntoView。
    # 余额文本不可聚焦，定位同卡片的底部按钮，使整行进入视口，再校验余额自身的矩形。
    $focusId = if ($Id.StartsWith('BalanceStatus_', [StringComparison]::Ordinal)) { 'PrepareAll_' + $keyProviders[$Id.Substring(14)] } else { $Id }
    $focus = Get-Control $focusId
    if ($focus.Current.IsEnabled -and $focus.Current.IsKeyboardFocusable) {
        $focus.SetFocus()
        Start-Sleep -Milliseconds 200
    }
    $scroll = Get-ScrollHost $ScrollId
    $pattern = $scroll.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    $range = $null
    if (-not $pattern.Current.VerticallyScrollable) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($window)
        $bars = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ScrollBar))
        foreach ($bar in @($bars | Sort-Object { $_.Current.BoundingRectangle.Right } -Descending)) {
            $candidate = $null
            if ($bar.Current.Orientation -eq [Windows.Automation.OrientationType]::Vertical -and $bar.Current.IsEnabled -and
                -not $bar.Current.IsOffscreen -and $bar.Current.BoundingRectangle.Left -ge $scroll.Current.BoundingRectangle.Left -and
                $bar.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern, [ref]$candidate) -and
                $candidate.Current.Maximum -gt $candidate.Current.Minimum) { $range = $candidate; break }
        }
    }
    foreach ($percent in @(-1) + @(0..20 | ForEach-Object { $_ * 5 })) {
        if ($percent -ge 0 -and ($pattern.Current.VerticallyScrollable -or $range)) {
            if ($range) { $range.SetValue($range.Current.Minimum + ($range.Current.Maximum - $range.Current.Minimum) * $percent / 100) }
            else { $pattern.SetScrollPercent([Windows.Automation.ScrollPattern]::NoScroll, $percent) }
            Start-Sleep -Milliseconds 100
        }
        $element = Get-Control $Id
        $box = $element.Current.BoundingRectangle
        $viewport = Get-Viewport $scroll
        if (-not $element.Current.IsOffscreen -and -not $box.IsEmpty -and $box.Height -gt 0 -and
            $box.Top -ge $viewport.Top - 1 -and $box.Bottom -le $viewport.Bottom + 1) { return }
        if (-not $pattern.Current.VerticallyScrollable -and -not $range) { break }
    }
    Capture 'scroll-failure'
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    foreach ($candidate in $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        $scrollPattern = $null
        if ($candidate.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scrollPattern)) {
            Write-Host "Scroll diagnostics: $($candidate.Current.AutomationId) $($candidate.Current.BoundingRectangle) scrollable=$($scrollPattern.Current.VerticallyScrollable) percent=$($scrollPattern.Current.VerticalScrollPercent) view=$($scrollPattern.Current.VerticalViewSize)"
        }
    }
    throw "无法把控件完整滚动到视口：$Id；控件=$box；视口=$viewport；滚动框=$($scroll.Current.AutomationId)"
}

function Assert-InViewport([string]$Id, [string]$ScrollId = 'PageScroll') {
    $element = Get-Control $Id
    $box = $element.Current.BoundingRectangle
    $viewport = Get-Viewport (Get-ScrollHost $ScrollId)
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    if ($element.Current.IsOffscreen -or $box.IsEmpty -or $box.Width -le 0 -or $box.Height -le 0 -or
        $box.Left -lt $viewport.Left - 1 -or $box.Right -gt $viewport.Right + 1 -or
        $box.Top -lt $viewport.Top - 1 -or $box.Bottom -gt $viewport.Bottom + 1 -or
        $box.Left -lt $bounds.Left -or $box.Right -gt $bounds.Right -or $box.Top -lt $bounds.Top -or $box.Bottom -gt $bounds.Bottom) {
        Capture 'viewport-failure'
        throw "窄窗口控件越出视口：$Id；控件=$box；视口=$viewport；窗口=$($bounds.Left),$($bounds.Top),$($bounds.Right),$($bounds.Bottom)；屏外=$($element.Current.IsOffscreen)"
    }
}

function Capture([string]$Name) {
    if (-not $Screenshot) { return }
    $null = [M6Window]::SetForegroundWindow($window)
    Start-Sleep -Milliseconds 650
    if ([M6Window]::GetForegroundWindow() -ne $window) { throw '测试窗口未置前，取消截图以免捕获其它窗口' }
    New-Item -ItemType Directory -Path $shots -Force | Out-Null
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    $bitmap = [Drawing.Bitmap]::new($bounds.Right - $bounds.Left, $bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $shots ($Name + '.png')))
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Write-Host "截图：$(Join-Path $shots ($Name + '.png'))"
}

function Read-Config { [IO.File]::ReadAllText($configPath) | ConvertFrom-Json }
function Read-Provider([string]$Id) { (Read-Config).proxy.providers | Where-Object id -eq $Id }
function Read-Events {
    if (-not (Test-Path -LiteralPath $eventsPath)) { return }
    # 假上游只追加完整行；读写竞争时忽略最后一个尚未换行的片段，下一轮再读。
    $lines = [IO.File]::ReadAllText($eventsPath).Split("`n")
    for ($index = 0; $index -lt $lines.Length - 1; $index++) {
        if ($lines[$index].Trim()) { $lines[$index] | ConvertFrom-Json }
    }
}
function Event-Count { @(Read-Events).Count }
function Key-Events([string]$KeyId, [int]$Since = 0) { Read-Events | Where-Object { $_.sequence -gt $Since -and $_.keyId -eq $KeyId } }
function Set-Scenario([string]$Name) {
    $temporary = $scenarioPath + '.new'
    [IO.File]::WriteAllText($temporary, $Name, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $scenarioPath, $true)
}
function Test-Balance([string]$KeyId, [string]$Expected) {
    $items = @(Find-Elements "BalanceStatus_$KeyId")
    if ($items.Count -ne 1) { return $false }
    if ($Expected -eq 'Key 已失效') { return ($items[0].Current.Name -replace '\s', '') -ceq 'Key已失效' }
    return $items[0].Current.Name -ceq $Expected
}
function Wait-Balance([string]$KeyId, [string]$Expected) {
    Wait-For { Test-Balance $KeyId $Expected } "余额显示不符合预期：$KeyId，应为 $Expected"
}
function Wait-Idle([string]$ProviderId) {
    Wait-For { @(Find-Elements "RefreshBalance_$ProviderId" | Where-Object { $_.Current.IsEnabled }).Count -eq 1 } "余额刷新未结束：$ProviderId"
}
function Refresh-Balance([string]$ProviderId, [string]$KeyId, [string]$Expected) {
    Show-Control "RefreshBalance_$ProviderId"
    Wait-Idle $ProviderId
    $since = Event-Count
    Invoke-Control "RefreshBalance_$ProviderId"
    Wait-For { @(Key-Events $KeyId $since).Count -gt 0 } "手动刷新没有发出请求：$ProviderId"
    Wait-Balance $KeyId $Expected
    Wait-Idle $ProviderId
}
function Assert-NoSecrets([string]$Text, [string]$Label) {
    foreach ($secret in @($secrets.Values) + @($codexToken, $claudeToken)) {
        if ($Text.Contains($secret, [StringComparison]::Ordinal)) { throw "$Label 包含完整密钥或本地口令" }
    }
}
function Assert-Paths([string]$KeyId, [int]$Since, [string[]]$Expected) {
    $paths = @(Key-Events $KeyId $Since | ForEach-Object path)
    if (($paths -join '|') -cne ($Expected -join '|')) { throw "接口请求顺序不符合预期：$KeyId（实际 $($paths.Count) 次，预期 $($Expected.Count) 次）" }
}
function Assert-Hidden([string]$ProviderId, [string]$KeyId) {
    Show-Control "ProviderMenu_$ProviderId"
    Show-Control $KeyId
    foreach ($id in @("BalanceStatus_$KeyId", "RefreshBalance_$ProviderId")) {
        foreach ($element in @(Find-Elements $id)) {
            $box = $element.Current.BoundingRectangle
            if (-not $box.IsEmpty -and $box.Width -gt 0 -and $box.Height -gt 0) { throw "不查询模式仍显示控件：$id" }
        }
    }
}
function Assert-NoRequests([string]$ProviderId, [int]$Since = 0) {
    # 负向断言保留 2 秒观察期，覆盖切页后排队的异步刷新。
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($process.HasExited -or $job.State -ne 'Running') { throw '观察不查询模式时隔离程序或假上游停止' }
        if (@(Read-Events | Where-Object { $_.sequence -gt $Since -and $_.providerId -eq $ProviderId }).Count -gt 0) {
            throw "不查询模式仍发出请求：$ProviderId"
        }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt 2)
}

function Open-BalanceEditor([string]$ProviderId) {
    Show-Control "ProviderMenu_$ProviderId"
    Invoke-Control "ProviderMenu_$ProviderId"
    Wait-For { @(Find-Menu '编辑' | Where-Object { $_.Current.Name -eq '编辑' }).Count -eq 1 } '供应商菜单缺少编辑入口'
    Invoke-Element @(Find-Menu '编辑' | Where-Object { $_.Current.Name -eq '编辑' })[0]
    Wait-For { @(Find-Elements 'BalanceQueryMode').Count -eq 1 } '供应商编辑抽屉缺少余额查询方式'
    Show-Control 'BalanceQueryMode' 'BodyScroll'
    if ((Get-Control 'BalanceQueryMode').Current.Name -cne '余额查询方式') { throw '余额查询下拉框缺少约定的辅助功能名称' }
}

function Set-BalanceMode([string]$ProviderId, [int]$Index) {
    $previousIndex = [Array]::IndexOf($modeValues, (Read-Provider $ProviderId).balance_query.mode)
    Open-BalanceEditor $ProviderId
    $picker = Get-Control 'BalanceQueryMode'
    $expand = $picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
        $items = @($picker.FindAll([Windows.Automation.TreeScope]::Descendants, $condition))
        if ($items.Count -ne 5) { throw '余额查询方式必须包含五个选项' }
        for ($position = 0; $position -lt 5; $position++) {
            $actual = $items[$position].Current.Name -replace '\s', ''
            $expected = $modeNames[$position] -replace '\s', ''
            if ($actual -cne $expected -and -not ($position -eq 0 -and $actual -ceq 'Auto')) { throw "余额查询选项顺序错误：索引 $position" }
        }
        if ($previousIndex -lt 0 -or -not $items[$previousIndex].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
            throw '编辑抽屉未选中已保存的余额查询方式'
        }
        $items[$Index].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } finally { $expand.Collapse() }
    Invoke-Control 'DrawerSave'
    Wait-For { @(Find-Elements 'BalanceQueryMode').Count -eq 0 } '供应商保存后抽屉未关闭'
    Wait-For { (Read-Provider $ProviderId).balance_query.mode -ceq $modeValues[$Index] } '余额查询方式没有写入真实临时配置'
    if ($Index -ne 0 -and (Read-Provider $ProviderId).balance_query.detected) { throw '显式查询方式仍保留旧的自动识别结果' }
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}
function Start-App {
    $script:process = if ($Screenshot) {
        Start-Process -FilePath (Join-Path $runtime 'RetryProxy.exe') -WorkingDirectory $runtime -PassThru
    } else { [RetryProxyTrayVerification]::StartPrivateProcess((Join-Path $runtime 'RetryProxy.exe'), $runtime) }
    $null = $process.Handle
    Wait-For {
        $script:window = [RetryProxyTrayVerification]::FindWindow($process.Id, 'LLM Retry Proxy', $null)
        return $window -ne [IntPtr]::Zero
    } '隔离程序没有出现主窗口'
    Invoke-Control 'ProviderNavigation'
    Invoke-Control 'SelectCodex'
    Wait-For { @(Find-Elements 'CurrentProviderKey').Count -eq 1 } '供应商页面未加载'
}
function Stop-App {
    $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero)
    if (-not $process.WaitForExit(30000)) { throw '隔离程序未正常退出' }
    if ($process.ExitCode -ne 0) { throw "隔离程序退出码：$($process.ExitCode)" }
    $process.Dispose()
    $script:process = $null
    $script:window = [IntPtr]::Zero
    [RetryProxyTrayVerification]::ClosePrivateDesktop()
}

function Check-Tray {
    $tray = [RetryProxyTrayVerification]::FindWindowByTitlePrefix($process.Id, 'wpfui_th_')
    if ($tray -eq [IntPtr]::Zero) { throw '隔离程序缺少托盘窗口' }
    $null = [RetryProxyTrayVerification]::PostMessage($tray, 2048, [UIntPtr]1, [IntPtr]0x0203)
    Wait-For { -not [RetryProxyTrayVerification]::IsWindowVisible($window) } '托盘没有隐藏测试窗口'
    $null = [RetryProxyTrayVerification]::PostMessage($tray, 2048, [UIntPtr]1, [IntPtr]0x0204)
    $null = [RetryProxyTrayVerification]::PostMessage($tray, 2048, [UIntPtr]1, [IntPtr]0x0205)
    Wait-For { @(Find-Menu 'Codex：用量接口 · 普通余额').Count -eq 1 } '托盘缺少当前客户端菜单'
    $client = @(Find-Menu 'Codex：用量接口 · 普通余额')[0]
    $client.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $expected = @{
        '用量接口 · 普通余额' = '23.45 USD'; '用量接口 · 零余额' = '0.00 USD'
        '用量接口 · 失效 Key' = 'Key已失效'; '自动识别 · 余额 Key' = '86.20 CNY'
        '账单接口 · 普通账单' = '42.25 USD'; '账单接口 · 无限账单' = '不限额'
    }
    foreach ($prefix in $expected.Keys) {
        Wait-For { @(Find-Menu $prefix).Count -eq 1 } "托盘缺少 Key 菜单：$prefix"
        $name = @(Find-Menu $prefix)[0].Current.Name
        Assert-NoSecrets $name '托盘菜单'
        if (($name -replace '\s', '') -notlike ('*' + ($expected[$prefix] -replace '\s', '') + '*')) { throw "托盘没有显示余额：$prefix" }
    }
    Wait-For { @(Find-Menu '不查询 · 静默 Key').Count -eq 1 } '托盘缺少不查询的 Key'
    if (@(Find-Menu '不查询 · 静默 Key')[0].Current.Name -cne '不查询 · 静默 Key') { throw '不查询模式在托盘仍显示余额占位符' }
    Invoke-Element @(Find-Menu '用量接口 · 零余额')[0]
    Wait-For { @((Read-Config).proxy.routes | Where-Object id -eq 'fixture-codex')[0].current_key_id -eq 'u-zero' } '托盘切换没有落盘'
    $null = [RetryProxyTrayVerification]::PostMessage($tray, 2048, [UIntPtr]1, [IntPtr]0x0203)
    Wait-For { [RetryProxyTrayVerification]::IsWindowVisible($window) } '托盘没有恢复测试窗口'
    Wait-For { (Get-Control 'CurrentProviderKey').Current.Name -like '*零余额*' } '隐藏窗口时的托盘切换没有更新页面'
    Show-Control 'u-main'
    Invoke-Control 'u-main'
    Wait-For { (Get-Control 'CurrentProviderKey').Current.Name -like '*普通余额*' } '托盘切换后页面切换失效'
}

try {
    New-Item -ItemType Directory -Path $runtime, (Join-Path $runtime 'User') | Out-Null
    Get-ChildItem -LiteralPath (Split-Path -Parent $ExePath) -File |
        Where-Object { $_.Name -match '\.(exe|dll)$|\.(deps|runtimeconfig)\.json$' } | Copy-Item -Destination $runtime
    $translations = Join-Path (Split-Path -Parent $ExePath) 'User\I18n'
    if (-not (Test-Path -LiteralPath $translations)) { $translations = Join-Path $PSScriptRoot '..\src\RetryProxy.App\User\I18n' }
    Copy-Item -LiteralPath $translations -Destination (Join-Path $runtime 'User\I18n') -Recurse
    foreach ($name in $environmentNames) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    $env:RETRY_PROXY_UI_TEST_ROOT = $runtime
    $env:CLAUDE_CONFIG_DIR = Join-Path $runtime 'claude'
    $env:CODEX_HOME = Join-Path $runtime 'codex'
    New-Item -ItemType Directory -Path $env:CLAUDE_CONFIG_DIR, $env:CODEX_HOME | Out-Null
    [IO.File]::WriteAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json'), '{}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'config.toml'), '', [Text.UTF8Encoding]::new($false))
    # 即使误启动保活/准备，也只能运行这个立即退出的假 CLI，不会回退到 PATH 上的真实客户端。
    $fakeCli = Join-Path $runtime 'blocked-cli.ps1'
    [IO.File]::WriteAllText($fakeCli, @'
[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'unexpected-cli.txt'), "blocked`n")
[Console]::Error.WriteLine('M6 fixture blocks CLI requests')
exit 87
'@, [Text.UTF8Encoding]::new($true))
    $env:RETRY_PROXY_CLAUDE_CLI = $fakeCli
    $env:RETRY_PROXY_CODEX_CLI = $fakeCli
    Set-Scenario 'normal'
    $ports = [Collections.Generic.HashSet[int]]::new()
    while ($ports.Count -lt 3) { $null = $ports.Add((Get-FreePort)) }
    $portList = @($ports)
    $upstreamPort = $portList[0]
    $codexPort = $portList[1]
    $claudePort = $portList[2]
    $job = Start-Job -ArgumentList $upstreamPort, $runtime, $secrets, $keyProviders -ScriptBlock {
        param($Port, $Directory, $Keys, $Owners)
        $ErrorActionPreference = 'Stop'
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $sequence = 0
        try {
            $listener.Start()
            [IO.File]::WriteAllText((Join-Path $Directory 'upstream.ready'), 'ready')
            while ($true) {
                $pending = $listener.GetContextAsync()
                while (-not $pending.IsCompleted) { Start-Sleep -Milliseconds 25 }
                $context = $pending.GetAwaiter().GetResult()
                $path = $context.Request.Url.AbsolutePath
                $authorization = $context.Request.Headers['Authorization']
                $matchesByKey = @($Keys.Keys | Where-Object { $authorization -ceq ('Bearer ' + $Keys[$_]) })
                $keyId = if ($matchesByKey.Count -eq 1) { $matchesByKey[0] } else { '' }
                $providerId = if ($path -match '^/(usage|auto|billing|none)/') { $Matches[1] } else { '' }
                $endpoint = if ($providerId) { $path.Substring($providerId.Length + 1) } else { $path }
                $valid = $keyId -and $Owners[$keyId] -ceq $providerId -and $providerId -ne 'none' -and
                    $context.Request.HttpMethod -ceq 'GET' -and $context.Request.Url.Query -eq '' -and
                    $context.Request.Headers['User-Agent'] -cmatch '^RetryProxy/[A-Za-z0-9._+\-]+$' -and
                    $endpoint -in @('/v1/usage', '/user/balance', '/v1/dashboard/billing/subscription', '/v1/dashboard/billing/usage')
                $scenario = [IO.File]::ReadAllText((Join-Path $Directory 'scenario.txt'))
                $sequence++
                # 记录身份编号与认证是否正确，不记录 Authorization 或返回的恶意错误正文。
                $record = @{ sequence = $sequence; providerId = $providerId; keyId = $keyId; path = $path; valid = [bool]$valid; scenario = $scenario }
                [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'), ($record | ConvertTo-Json -Compress) + "`n")
                $status = 404
                $body = @{ error = @{ message = 'fixture endpoint unavailable' } }
                if (-not $valid) { $status = 400 }
                elseif ($providerId -eq 'usage' -and $endpoint -eq '/v1/usage') {
                    $status = 200
                    switch ($keyId) {
                        'u-main' {
                            if ($scenario -eq 'failure') {
                                $status = 503
                                $body = @{ error = @{ message = 'fixture rejected secret: ' + $Keys[$keyId] } }
                            } else {
                                if ($scenario -eq 'slow') {
                                    $wait = [Diagnostics.Stopwatch]::StartNew()
                                    while (-not (Test-Path -LiteralPath (Join-Path $Directory 'release-balance'))) {
                                        if ($wait.Elapsed.TotalSeconds -gt 8) { throw '测试端没有及时释放余额响应' }
                                        Start-Sleep -Milliseconds 25
                                    }
                                }
                                $amount = if ($scenario -eq 'normal') { 12.30 } else { 23.45 }
                                $body = @{ remaining = $amount; unit = 'USD' }
                            }
                        }
                        'u-zero' { $body = @{ quota = @{ remaining = 0 } } }
                        'u-invalid' { $body = @{ is_active = $false } }
                    }
                }
                elseif ($providerId -eq 'auto' -and $endpoint -eq '/user/balance') {
                    $status = 200; $body = @{ data = @{ balance = 86.20; currency = 'CNY' } }
                }
                elseif ($providerId -in @('billing', 'auto') -and $endpoint -eq '/v1/dashboard/billing/subscription') {
                    $status = 200
                    $body = @{ hard_limit_usd = $(if ($keyId -eq 'b-unlimited') { 100000000 } else { 50 }) }
                }
                elseif ($providerId -in @('billing', 'auto') -and $endpoint -eq '/v1/dashboard/billing/usage') {
                    $status = 200; $body = @{ total_usage = 775 }
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6 -Compress))
                $context.Response.StatusCode = $status
                $context.Response.ContentType = 'application/json; charset=utf-8'
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Close() }
    }
    Wait-For { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } '本机假上游未启动'
    $providers = @(
        @{ id = 'usage'; name = '用量接口'; balance_query = @{ mode = 'usage' }; keys = @(
            @{ id = 'u-main'; name = '普通余额'; api_key = $secrets['u-main'] },
            @{ id = 'u-zero'; name = '零余额'; api_key = $secrets['u-zero'] },
            @{ id = 'u-invalid'; name = '失效 Key'; api_key = $secrets['u-invalid'] }) },
        @{ id = 'auto'; name = '自动识别'; balance_query = @{ mode = 'auto' }; keys = @(
            @{ id = 'a-main'; name = '余额 Key'; api_key = $secrets['a-main'] }) },
        @{ id = 'billing'; name = '账单接口'; balance_query = @{ mode = 'openai_billing' }; keys = @(
            @{ id = 'b-main'; name = '普通账单'; api_key = $secrets['b-main'] },
            @{ id = 'b-unlimited'; name = '无限账单'; api_key = $secrets['b-unlimited'] }) },
        @{ id = 'none'; name = '不查询'; balance_query = @{ mode = 'none' }; keys = @(
            @{ id = 'n-main'; name = '静默 Key'; api_key = $secrets['n-main'] }) }
    )
    for ($index = 0; $index -lt $providers.Count; $index++) {
        $providers[$index].client_type = 'codex'
        $providers[$index].base_url = "http://127.0.0.1:$upstreamPort/$($providers[$index].id)/v1"
        $providers[$index].models = @{ model = 'balance-test-model' }
        $providers[$index].sort_index = $index
    }
    $config = @{
        proxy = @{
            schema_version = 7; selected_route_id = 'fixture-codex'; providers = $providers
            routes = @(
                @{ id = 'fixture-codex'; name = 'Codex'; client_type = 'codex'; listen_port = $codexPort; current_provider_id = 'usage'; current_key_id = 'u-main'; local_token = $codexToken; keepalive_enabled = $false },
                @{ id = 'fixture-claude'; name = 'Claude Code'; client_type = 'claude'; listen_port = $claudePort; current_provider_id = ''; current_key_id = ''; local_token = $claudeToken; keepalive_enabled = $false }
            )
        }
        preparations = @()
        commonConfig = @{ clientSetupCompleted = $true; isFirstRun = $false; exitToTray = $false; startMinimized = $false }
        otherConfig = @{ uiCultureInfoName = 'zh-Hans' }
    }
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
    Start-App
    foreach ($pair in @(@('u-main', '12.30 USD'), @('u-zero', '0.00 USD'), @('u-invalid', 'Key 已失效'),
        @('a-main', '86.20 CNY'), @('b-main', '42.25 USD'), @('b-unlimited', '不限额'))) { Wait-Balance $pair[0] $pair[1] }
    foreach ($id in @('usage', 'auto', 'billing')) { Wait-Idle $id }
    Assert-Paths 'u-main' 0 @('/usage/v1/usage')
    Assert-Paths 'u-zero' 0 @('/usage/v1/usage')
    Assert-Paths 'u-invalid' 0 @('/usage/v1/usage')
    Assert-Paths 'a-main' 0 @('/auto/v1/usage', '/auto/user/balance')
    Assert-Paths 'b-main' 0 @('/billing/v1/dashboard/billing/subscription', '/billing/v1/dashboard/billing/usage')
    Assert-Paths 'b-unlimited' 0 @('/billing/v1/dashboard/billing/subscription')
    Wait-For { (Read-Provider 'auto').balance_query.mode -eq 'auto' -and (Read-Provider 'auto').balance_query.detected -eq 'user_balance' } '自动识别结果没有落盘'
    Show-Control 'BalanceStatus_u-main'
    if ((Get-Control 'BalanceStatus_u-main').Current.HelpText -notmatch '查询于\s*\d{2}:\d{2}') { throw '正常余额缺少查询时间提示' }
    Capture 'balances'
    Assert-Hidden 'none' 'n-main'
    Assert-NoRequests 'none'

    Show-Control 'RefreshBalance_usage'
    Show-Control 'BalanceStatus_u-main'
    Set-Scenario 'slow'
    $beforeRefresh = Event-Count
    Invoke-Control 'RefreshBalance_usage'
    try {
        Wait-For {
            return @(Key-Events 'u-main' $beforeRefresh).Count -eq 1 -and -not (Get-Control 'RefreshBalance_usage').Current.IsEnabled
        } '手动刷新没有到达假上游，或查询期间按钮没有禁用' 5
        if (-not (Test-Balance 'u-main' '12.30 USD')) { throw '刷新过程中清空或覆盖了上次余额' }
        Capture 'refresh-retains-balance'
    } finally { [IO.File]::WriteAllText($releasePath, 'release') }
    Wait-Balance 'u-main' '23.45 USD'
    Wait-Idle 'usage'
    foreach ($id in @('u-main', 'u-zero', 'u-invalid')) { Assert-Paths $id $beforeRefresh @('/usage/v1/usage') }
    if (@(Read-Events | Where-Object { $_.sequence -gt $beforeRefresh -and $_.providerId -ne 'usage' }).Count -gt 0) { throw '刷新一个供应商时误查询了其它供应商' }
    Set-Scenario 'failure'
    Refresh-Balance 'usage' 'u-main' '—'
    $hint = (Get-Control 'BalanceStatus_u-main').Current.HelpText
    if ($hint -notmatch '503') { throw '余额失败没有提供可读的 HTTP 状态原因' }
    Assert-NoSecrets $hint '余额失败提示'
    foreach ($handle in [RetryProxyTrayVerification]::WindowsForProcess($process.Id)) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
        foreach ($element in $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
            Assert-NoSecrets ($element.Current.Name + "`n" + $element.Current.HelpText) '失败后的界面文本'
        }
    }
    Show-Control 'BalanceStatus_u-main'
    Capture 'balance-failure'
    Set-Scenario 'refreshed'
    Refresh-Balance 'usage' 'u-main' '23.45 USD'

    # 重启使用同一个真实 config.json；不注入配置 JSON，也不重新写测试配置。
    Stop-App
    if ((Read-Provider 'auto').balance_query.detected -ne 'user_balance') { throw '正常退出丢失已识别接口' }
    $beforeRestart = Event-Count
    Start-App
    Wait-Balance 'a-main' '86.20 CNY'
    foreach ($id in @('usage', 'auto', 'billing')) { Wait-Idle $id }
    Assert-Paths 'a-main' $beforeRestart @('/auto/user/balance')
    Wait-Balance 'u-main' '23.45 USD'
    Wait-Balance 'u-zero' '0.00 USD'
    Wait-Balance 'u-invalid' 'Key 已失效'
    Wait-Balance 'b-main' '42.25 USD'
    Wait-Balance 'b-unlimited' '不限额'
    Assert-Hidden 'none' 'n-main'
    Assert-NoRequests 'none'
    Check-Tray

    # 通过供应商菜单编辑并保存，覆盖五种选项的顺序、回显和配置值。
    # 自动识别供应商的用量接口返回 404，显式选择用量时必须失败，不能偷偷回退。
    foreach ($case in @(
        @{ index = 1; text = '—'; paths = @('/auto/v1/usage') },
        @{ index = 2; text = '86.20 CNY'; paths = @('/auto/user/balance') },
        @{ index = 3; text = '42.25 USD'; paths = @('/auto/v1/dashboard/billing/subscription', '/auto/v1/dashboard/billing/usage') }
    )) {
        $beforeMode = Event-Count
        Set-BalanceMode 'auto' $case.index
        Refresh-Balance 'auto' 'a-main' $case.text
        $actualPaths = @(Key-Events 'a-main' $beforeMode | ForEach-Object path)
        foreach ($path in $case.paths) { if ($path -cnotin $actualPaths) { throw "显式查询方式没有请求对应接口：$($case.index)" } }
        if (@($actualPaths | Where-Object { $_ -cnotin $case.paths }).Count -gt 0) { throw '显式查询方式请求了其它接口' }
    }
    Set-BalanceMode 'auto' 4
    $beforeNone = Event-Count
    Assert-Hidden 'auto' 'a-main'
    Invoke-Control 'PreparationNavigation'
    Invoke-Control 'ProviderNavigation'
    Assert-Hidden 'auto' 'a-main'
    Assert-NoRequests 'auto' $beforeNone
    $beforeAuto = Event-Count
    Set-BalanceMode 'auto' 0
    Refresh-Balance 'auto' 'a-main' '86.20 CNY'
    Wait-For { (Read-Provider 'auto').balance_query.detected -eq 'user_balance' } '恢复自动识别没有重新记住接口'
    $autoPaths = @(Key-Events 'a-main' $beforeAuto | ForEach-Object path)
    if ($autoPaths.Count -lt 2 -or $autoPaths[0] -cne '/auto/v1/usage' -or
        @($autoPaths | Select-Object -Skip 1 | Where-Object { $_ -cne '/auto/user/balance' }).Count -gt 0) { throw '恢复自动识别后的尝试顺序错误' }

    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    $scale = [M6Window]::GetDpiForWindow($window) / 96.0
    if ($scale -le 0) { throw '无法获取测试窗口缩放比例' }
    # 760×520 与既有窄窗口验收一致，避免 720 外框触发客户端区域最小宽度恢复。
    [RetryProxyTrayVerification]::SetBounds($window, $bounds.Left, $bounds.Top, [int][Math]::Ceiling(760 * $scale), [int][Math]::Ceiling(520 * $scale))
    Start-Sleep -Milliseconds 800
    Wait-For {
        $size = [RetryProxyTrayVerification]::Bounds($window)
        return ($size.Right - $size.Left) -le [Math]::Ceiling(780 * $scale)
    } '测试窗口没有缩到窄窗口尺寸'
    foreach ($providerId in @('usage', 'auto', 'billing')) {
        Show-Control "RefreshBalance_$providerId"
        Assert-InViewport "RefreshBalance_$providerId"
        Assert-InViewport "ProviderMenu_$providerId"
    }
    foreach ($keyId in @('u-main', 'u-zero', 'u-invalid', 'a-main', 'b-main', 'b-unlimited')) {
        Show-Control "BalanceStatus_$keyId"
        Assert-InViewport "BalanceStatus_$keyId"
        Assert-InViewport $keyId
        Assert-InViewport "PrepareKey_$keyId"
    }
    Show-Control 'BalanceStatus_u-main'
    Capture 'narrow-balances'
    Open-BalanceEditor 'usage'
    Assert-InViewport 'BalanceQueryMode' 'BodyScroll'
    Capture 'narrow-balance-editor'
    Invoke-Control 'DrawerCancel'
    Wait-For { @(Find-Elements 'BalanceQueryMode').Count -eq 0 } '未修改的余额抽屉没有关闭'
    Assert-NoRequests 'none'
    Stop-App
    if ([IO.File]::ReadAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json')) -cne '{}' -or
        [IO.File]::ReadAllText((Join-Path $env:CODEX_HOME 'config.toml')) -cne '') { throw '余额查询修改了客户端配置' }
    if (Test-Path -LiteralPath (Join-Path $runtime 'unexpected-cli.txt')) { throw '余额验收期间误启动了 CLI，已由假 CLI 阻断' }
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $runtime 'logs') -File -Recurse)
    if ($logs.Count -eq 0) { throw '未找到可检查的应用日志' }
    foreach ($log in $logs) { Assert-NoSecrets ([IO.File]::ReadAllText($log.FullName)) '应用日志' }
    if (@(Read-Events | Where-Object { -not $_.valid }).Count -gt 0) { throw '假上游收到错误认证、请求头、路径或不查询供应商的请求' }
    $passed = $true
    Write-Host 'PASS：三类余额接口、逐 Key 状态、自动识别落盘及重启直达、手动刷新保留旧值、失败脱敏、五种查询方式、None 不请求、托盘余额与切换、窄窗口视口。'
}
finally {
    try {
        if ($process) {
            if (-not $process.HasExited) {
                # 只关闭本次 Start-App 返回的进程，绝不按进程名寻找或结束现有实例。
                if (Test-Path -LiteralPath $runtime) { [IO.File]::WriteAllText($releasePath, 'release') }
                $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero)
                if (-not $process.WaitForExit(30000)) { $process.Kill(); $null = $process.WaitForExit(5000) }
            }
            $process.Dispose()
        }
        [RetryProxyTrayVerification]::ClosePrivateDesktop()
    } finally {
        if ($job) { Stop-Job $job; Remove-Job $job -Force }
        foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
        if ($passed -and (Test-Path -LiteralPath $runtime)) {
            $target = Get-Item -LiteralPath $runtime
            $resolved = $target.FullName.TrimEnd('\')
            if ($resolved -ne [IO.Path]::GetFullPath($runtime) -or
                -not $resolved.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
                ($target.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '测试清理路径异常，已保留目录' }
            Remove-Item -LiteralPath $runtime -Recurse -Force
        } elseif (Test-Path -LiteralPath $runtime) { Write-Host "失败现场保留：$runtime" }
    }
}
