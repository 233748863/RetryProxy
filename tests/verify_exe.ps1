param(
    [string]$ExePath = (Join-Path $PSScriptRoot "..\dist\RetryProxy.exe"),
    [switch]$VerifyTray,
    [switch]$UseCurrentDesktop
)
# C# 版端到端验收（移植自 D:\API-Proxy\tests\verify_rust_exe.ps1）。
# 与 Rust 版的差异：托盘钩子窗口按标题前缀 wpfui_th_ 查找，托盘操作一律用回调消息 2048 + WM_LBUTTONDBLCLK（显示/隐藏切换）；
# 最小化保持在任务栏（IsIconic），隐藏到托盘只改可见性；缓存明细改为页面（导航项“缓存明细”，标题“缓存明细 · E2E”）；
# 配置注入时不写 User\config.json。

$ErrorActionPreference = "Stop"
if ($UseCurrentDesktop -and -not $VerifyTray) { throw '-UseCurrentDesktop requires -VerifyTray' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$distDir = Split-Path -Parent $ExePath
$oldConfig = $env:RETRY_PROXY_CONFIG_JSON
$TrayCallback = 2048       # WPF-UI NOTIFYICONDATA.uCallbackMessage
$TrayIconId = 1            # 第一个 NotifyIcon
$LButtonDblClk = 0x0203

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-TrayCondition([scriptblock]$Condition, [string]$Failure, [int]$Seconds = 10) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($timer.Elapsed.TotalSeconds -ge $Seconds) {
            if ($VerifyTray -and $null -ne $mainWindow -and $mainWindow -ne [IntPtr]::Zero) {
                $bounds = [RetryProxyTrayVerification]::Bounds($mainWindow)
                $Failure += " (visible=$([RetryProxyTrayVerification]::IsWindowVisible($mainWindow)), minimized=$([RetryProxyTrayVerification]::IsIconic($mainWindow)), bounds=$($bounds.Left),$($bounds.Top),$($bounds.Right),$($bounds.Bottom))"
            }
            throw $Failure
        }
        Start-Sleep -Milliseconds 50
    }
}

function Send-TrayWindowMessage([IntPtr]$Window, [uint32]$Message, [uint64]$Value, [long]$Detail = 0) {
    if (-not [RetryProxyTrayVerification]::PostMessage($Window, $Message, [UIntPtr]$Value, [IntPtr]$Detail)) {
        throw "Could not send window message $Message"
    }
}

function Invoke-TrayDoubleClick([IntPtr]$Tray) {
    Send-TrayWindowMessage $Tray $TrayCallback $TrayIconId $LButtonDblClk
}

function Find-UiElement([IntPtr]$Window, [string]$Name, [switch]$ById) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Window)
    $property = if ($ById) { [System.Windows.Automation.AutomationElement]::AutomationIdProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $condition = [System.Windows.Automation.PropertyCondition]::new($property, $Name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-UiElement([IntPtr]$Window, [string]$Name, [switch]$ById) {
    # 同名元素可能有多个（导航项本身 + 它内部的文本），取第一个带可操作模式的。
    Wait-TrayCondition { $null -ne (Find-UiElement $Window $Name -ById:$ById) } "UI element was not available: $Name"
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Window)
    $property = if ($ById) { [System.Windows.Automation.AutomationElement]::AutomationIdProperty } else { [System.Windows.Automation.AutomationElement]::NameProperty }
    $condition = [System.Windows.Automation.PropertyCondition]::new($property, $Name)
    $types = @()
    foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $types += $element.Current.ControlType.ProgrammaticName
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
        if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
        # 导航项：同名的是容器 DataItem / 文本，真正带 SelectionItem 的 TabItem 在其子树里。
        foreach ($child in $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($child.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
            if ($child.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        }
    }
    throw "UI element has no invokable pattern: $Name ($($types -join ', '))"
}

function Invoke-NavigationItem([IntPtr]$Window, [string]$Name) {
    # WPF-UI 的导航项在 UIA 树里只暴露为无模式的 DataItem：先让它获得焦点，再向主窗口投递回车。
    Wait-TrayCondition { $null -ne (Find-UiElement $Window $Name) } "Navigation item was not available: $Name"
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Window)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $item = $null
    foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem) { $item = $element }
    }
    if ($null -eq $item) { throw "Navigation item container not found: $Name" }
    $item.SetFocus()
    Start-Sleep -Milliseconds 300
    $enter = [UIntPtr]::new([uint64]13)
    [RetryProxyTrayVerification]::PostMessage($Window, 0x0100, $enter, [IntPtr]::new([int64]0x001C0001)) | Out-Null
    [RetryProxyTrayVerification]::PostMessage($Window, 0x0101, $enter, [IntPtr]::new([int64]0xC01C0001)) | Out-Null
}

if ($VerifyTray) {
    Add-Type -AssemblyName System.Net.Http
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')
}

$tempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath.TrimEnd('\')
$runtimeDir = Join-Path $tempRoot ("retry-csharp-e2e-" + [guid]::NewGuid().ToString("N"))
$runtimeExe = Join-Path $runtimeDir "RetryProxy.exe"
$upstreamPort = Get-FreeTcpPort
$proxyPort = Get-FreeTcpPort
$job = $null
$process = $null
$client = $null
$streamResponse = $null
$mainWindow = [IntPtr]::Zero
New-Item -ItemType Directory -Path $runtimeDir | Out-Null

function Start-App {
    if ($VerifyTray -and -not $UseCurrentDesktop) {
        return [RetryProxyTrayVerification]::StartPrivateProcess($runtimeExe, $runtimeDir)
    }
    return Start-Process -FilePath $runtimeExe -WorkingDirectory $runtimeDir -PassThru
}

try {
    Copy-Item -LiteralPath $ExePath -Destination $runtimeExe
    if (Test-Path -LiteralPath (Join-Path $distDir 'User')) {
        Copy-Item -LiteralPath (Join-Path $distDir 'User') -Destination (Join-Path $runtimeDir 'User') -Recurse
    }
    $job = Start-Job -ArgumentList $upstreamPort, $runtimeDir -ScriptBlock {
        param($Port, $RuntimeDir)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        $count = 0
        $generationCount = 0
        try {
            while ($true) {
                $pendingContext = $listener.GetContextAsync()
                while (-not $pendingContext.IsCompleted) { Start-Sleep -Milliseconds 50 }
                $context = $pendingContext.GetAwaiter().GetResult()
                $count++
                if ($context.Request.Url.AbsolutePath -in @('/v1/messages', '/v1/chat/completions')) {
                    $reply = if ($context.Request.Url.AbsolutePath -eq '/v1/messages') {
                        '{"type":"message","model":"claude-test","stop_reason":"end_turn","content":[],"usage":{"input_tokens":100,"output_tokens":10,"cache_read_input_tokens":800,"cache_creation_input_tokens":100}}'
                    } else {
                        '{"model":"gpt-test","choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":10,"prompt_tokens_details":{"cached_tokens":700}}}'
                    }
                    $body = [Text.Encoding]::UTF8.GetBytes($reply)
                    $context.Response.ContentType = 'application/json'
                    $context.Response.ContentLength64 = $body.Length
                    $context.Response.OutputStream.Write($body, 0, $body.Length)
                    $context.Response.Close()
                    continue
                }
                if ($context.Request.Url.AbsolutePath -eq '/v1/responses') {
                    $generationCount++
                    $context.Response.ContentType = 'text/event-stream; charset=utf-8'
                    $context.Response.SendChunked = $true
                    if ($generationCount -eq 1) {
                        $context.Response.Headers['x-request-id'] = 'old-generation-attempt'
                        try {
                            $body = [Text.Encoding]::UTF8.GetBytes("data: {`"type`":`"response.created`",`"response`":{`"id`":`"old-generation-attempt`",`"output`":[]}}`n`n")
                            $context.Response.OutputStream.Write($body, 0, $body.Length)
                            $timer = [Diagnostics.Stopwatch]::StartNew()
                            while ($timer.Elapsed.TotalSeconds -lt 2) {
                                $body = [Text.Encoding]::UTF8.GetBytes("data: {`"type`":`"keepalive`"}`n`n")
                                $context.Response.OutputStream.Write($body, 0, $body.Length)
                                $context.Response.OutputStream.Flush()
                                Start-Sleep -Milliseconds 50
                            }
                        }
                        catch [Net.HttpListenerException] {}
                        catch [ObjectDisposedException] {}
                        finally { $context.Response.Close() }
                    }
                    else {
                        $context.Response.Headers['x-request-id'] = 'new-generation-attempt'
                        $body = [Text.Encoding]::UTF8.GetBytes("data: {`"type`":`"response.output_text.delta`",`"delta`":`"generation-recovered`"}`n`ndata: {`"type`":`"response.completed`",`"response`":{`"output`":[]}}`n`n")
                        $context.Response.OutputStream.Write($body, 0, $body.Length)
                        $context.Response.Close()
                    }
                    continue
                }
                if ($context.Request.Url.AbsolutePath -eq '/tray-stream') {
                    $context.Response.ContentType = 'text/event-stream'
                    $context.Response.SendChunked = $true
                    $body = [Text.Encoding]::UTF8.GetBytes("data: {`"type`":`"response.output_text.delta`",`"delta`":`"before-minimize`"}`n`n")
                    $context.Response.OutputStream.Write($body, 0, $body.Length)
                    $context.Response.OutputStream.Flush()
                    New-Item -ItemType File -Path (Join-Path $RuntimeDir 'stream-started') | Out-Null
                    $timer = [Diagnostics.Stopwatch]::StartNew()
                    while (-not (Test-Path -LiteralPath (Join-Path $RuntimeDir 'finish-stream'))) {
                        if ($timer.Elapsed.TotalSeconds -ge 25) { throw 'Tray stream was not released' }
                        Start-Sleep -Milliseconds 50
                    }
                    $body = [Text.Encoding]::UTF8.GetBytes("data: {`"type`":`"response.output_text.delta`",`"delta`":`"after-minimize`"}`n`ndata: {`"type`":`"response.completed`",`"response`":{`"status`":`"completed`",`"usage`":{`"input_tokens`":1,`"output_tokens`":2}}}`n`n")
                    $context.Response.OutputStream.Write($body, 0, $body.Length)
                    $context.Response.Close()
                    continue
                }
                if ($count -eq 1) {
                    $context.Response.StatusCode = 500
                    $body = [Text.Encoding]::UTF8.GetBytes('{"error":"retry"}')
                }
                else {
                    $context.Response.StatusCode = 200
                    $body = [Text.Encoding]::UTF8.GetBytes('{"result":"proxy-ok"}')
                }
                $context.Response.ContentType = "application/json"
                $context.Response.ContentLength64 = $body.Length
                $context.Response.OutputStream.Write($body, 0, $body.Length)
                $context.Response.Close()
            }
        }
        finally { $listener.Stop(); $listener.Close() }
    }

    $config = @{
        schema_version = 6
        client_type = 'codex'
        upstream_base_url = "http://127.0.0.1:$upstreamPort"
        listen_port = $proxyPort
        max_retries = 1
        timeout_seconds = 10.0
        generation_timeout_seconds = 0.5
        base_delay_seconds = 0.0
        max_delay_seconds = 0.0
        desired_running = $true
        selected_route_id = "e2e"
        providers = @(@{ name = "local"; base_url = "http://127.0.0.1:$upstreamPort" })
        routes = @(@{
            id = "e2e"; name = "E2E"; provider_name = "local"; client_type = 'codex'; listen_port = $proxyPort
            max_retries = 1; timeout_seconds = 10.0; base_delay_seconds = 0.0
            generation_timeout_seconds = 0.5
            max_delay_seconds = 0.0; desired_running = $true
        })
    } | ConvertTo-Json -Depth 8

    $env:RETRY_PROXY_CONFIG_JSON = $config
    $process = Start-App

    $health = $null
    for ($attempt = 0; $attempt -lt 300; $attempt++) {
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 1
            break
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $health) { throw "Health endpoint did not start" }
    $result = Invoke-RestMethod "http://127.0.0.1:$proxyPort/test" -TimeoutSec 15
    $final = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
    if ($result.result -ne "proxy-ok") { throw "Unexpected proxy response" }
    if ($final.metrics.retry_count -ne 1) { throw "Unexpected retry count" }
    $generationResult = Invoke-WebRequest "http://127.0.0.1:$proxyPort/v1/responses" -Method Post -ContentType 'application/json' -Body '{"stream":true}' -UseBasicParsing -TimeoutSec 15
    $expectedGeneration = "data: {`"type`":`"response.output_text.delta`",`"delta`":`"generation-recovered`"}`n`ndata: {`"type`":`"response.completed`",`"response`":{`"output`":[]}}`n`n"
    if ($generationResult.StatusCode -ne 200 -or $generationResult.Content -ne $expectedGeneration) {
        throw 'Generation retry did not return the new attempt unchanged'
    }
    if ([string]$generationResult.Headers['x-request-id'] -ne 'new-generation-attempt') {
        throw 'Generation retry leaked headers from the abandoned attempt'
    }
    Wait-TrayCondition {
        (Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2).metrics.active_requests -eq 0
    } 'Generation retry did not release request state'
    $final = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
    if ($final.metrics.retry_count -ne 2 -or $final.metrics.failed_requests -ne 0) {
        throw 'Generation retry did not perform exactly one safe retry'
    }
    Invoke-RestMethod "http://127.0.0.1:$proxyPort/v1/messages" -Method Post -ContentType 'application/json' -Body '{"model":"claude-test","messages":[],"max_tokens":10}' -TimeoutSec 5 | Out-Null
    Invoke-RestMethod "http://127.0.0.1:$proxyPort/v1/chat/completions" -Method Post -ContentType 'application/json' -Body '{"model":"gpt-test","messages":[]}' -TimeoutSec 5 | Out-Null
    Wait-TrayCondition {
        (Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2).metrics.active_requests -eq 0
    } 'Cache requests did not finish'
    $cacheHealth = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
    if ($cacheHealth.metrics.statistics_date -ne (Get-Date -Format 'yyyy-MM-dd') -or
        $null -ne $cacheHealth.metrics.statistics_warning -or
        $cacheHealth.metrics.cache.input_tokens -ne 2000 -or
        $cacheHealth.metrics.cache.cached_tokens -ne 1500 -or
        $cacheHealth.metrics.cache.cache_creation_tokens -ne 100 -or
        $cacheHealth.metrics.gpt_cache.measured_requests -ne 1) {
        throw 'Daily channel cache did not combine GPT and Claude usage correctly'
    }
    Write-Host 'Proxy, generation retry and cache checks passed.'
    $dailyBeforeRestart = $null
    if ($VerifyTray) {
        Wait-TrayCondition {
            [RetryProxyTrayVerification]::FindWindow($process.Id, 'LLM Retry Proxy', $null) -ne [IntPtr]::Zero
        } 'Main window was not created'
        Wait-TrayCondition {
            [RetryProxyTrayVerification]::FindWindowByTitlePrefix($process.Id, 'wpfui_th_') -ne [IntPtr]::Zero
        } 'Tray icon hook window was not created'
        $mainWindow = [RetryProxyTrayVerification]::FindWindow($process.Id, 'LLM Retry Proxy', $null)
        $trayWindow = [RetryProxyTrayVerification]::FindWindowByTitlePrefix($process.Id, 'wpfui_th_')
        Wait-TrayCondition { [RetryProxyTrayVerification]::IsUsable($mainWindow) } 'Main window did not become usable'

        # 首页卡片直达运行概况；单通道与批量启停统一在通道管理，切换与重启保留统计。
        Wait-TrayCondition { $null -ne (Find-UiElement $mainWindow 'HomeOverviewCard' -ById) } 'Startup did not open the application home'
        Invoke-UiElement $mainWindow 'HomeOverviewCard' -ById
        Wait-TrayCondition { $null -ne (Find-UiElement $mainWindow 'ManageChannels' -ById) } 'Home card did not open the overview'
        foreach ($id in @('ToggleChannel', 'EnableAllChannels', 'DisableAllChannels', 'ChannelKeepAlive')) {
            if ($null -ne (Find-UiElement $mainWindow $id -ById)) { throw "Management control is still on the overview: $id" }
        }
        Wait-TrayCondition {
            $counter = Find-UiElement $mainWindow 'OverviewTotalRequests' -ById
            return $null -ne $counter -and $counter.Current.Name -eq [string]$cacheHealth.metrics.total_requests
        } 'Overview did not show the current channel statistics'
        Invoke-NavigationItem $mainWindow '通道管理'
        Wait-TrayCondition { $null -ne (Find-UiElement $mainWindow 'ToggleChannel' -ById) } 'Channel management did not load'
        foreach ($operation in @('single', 'all')) {
            $stopControl = if ($operation -eq 'single') { 'ToggleChannel' } else { 'DisableAllChannels' }
            $startControl = if ($operation -eq 'single') { 'ToggleChannel' } else { 'EnableAllChannels' }
            Invoke-UiElement $mainWindow $stopControl -ById
            Wait-TrayCondition {
                try { $null = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 1; return $false }
                catch { return $true }
            } "Channel management failed to stop the proxy: $operation"
            Wait-TrayCondition { (Find-UiElement $mainWindow $startControl -ById).Current.IsEnabled } 'Channel start did not become available'
            Invoke-UiElement $mainWindow $startControl -ById
            Wait-TrayCondition {
                try { $null = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 1; return $true }
                catch { return $false }
            } "Channel management failed to start the proxy: $operation"
        }
        Invoke-NavigationItem $mainWindow '软件设置'
        Wait-TrayCondition { $null -ne (Find-UiElement $mainWindow 'SwitchAppearance' -ById) } 'Appearance setting is missing'
        Invoke-UiElement $mainWindow 'SwitchAppearance' -ById
        Invoke-NavigationItem $mainWindow '运行概况'
        Wait-TrayCondition {
            $counter = Find-UiElement $mainWindow 'OverviewTotalRequests' -ById
            return $null -ne $counter -and $counter.Current.Name -eq [string]$cacheHealth.metrics.total_requests
        } 'Navigation or channel restart lost statistics'
        Write-Host 'Overview, channel management, single/all start-stop and software settings checks passed.'

        foreach ($maximized in @($false, $true)) {
            $command = if ($maximized) { 0xF030 } else { 0xF120 }
            Send-TrayWindowMessage $mainWindow 0x0112 $command
            Wait-TrayCondition { [RetryProxyTrayVerification]::IsZoomed($mainWindow) -eq $maximized } 'Window state did not change'
            foreach ($minimizeMethod in @('system-command', 'direct-native', 'system-command-source')) {
                if ($minimizeMethod -eq 'direct-native') {
                    [RetryProxyTrayVerification]::MinimizeDirectly($mainWindow)
                } else {
                    $minimizeCommand = if ($minimizeMethod -eq 'system-command-source') { 0xF022 } else { 0xF020 }
                    Send-TrayWindowMessage $mainWindow 0x0112 $minimizeCommand
                }
                Wait-TrayCondition { [RetryProxyTrayVerification]::IsIconic($mainWindow) } "Minimize failed: $minimizeMethod"
                Invoke-TrayDoubleClick $trayWindow
                Wait-TrayCondition { [RetryProxyTrayVerification]::IsUsable($mainWindow) } "Tray restore from taskbar failed: $minimizeMethod"
                if ([RetryProxyTrayVerification]::IsZoomed($mainWindow) -ne $maximized) { throw 'Restoring changed the maximized state' }
            }
            # 隐藏到托盘（再次双击托盘图标）与还原，最大化状态必须保留。
            Invoke-TrayDoubleClick $trayWindow
            Wait-TrayCondition { -not [RetryProxyTrayVerification]::IsWindowVisible($mainWindow) } 'Tray double-click did not hide the visible window'
            Start-Sleep -Milliseconds 300
            Invoke-TrayDoubleClick $trayWindow
            Wait-TrayCondition { [RetryProxyTrayVerification]::IsUsable($mainWindow) } 'Tray double-click did not restore the hidden window'
            if ([RetryProxyTrayVerification]::IsZoomed($mainWindow) -ne $maximized) { throw 'Restoring from tray changed the maximized state' }
        }
        Send-TrayWindowMessage $mainWindow 0x0112 0xF120
        Wait-TrayCondition {
            [RetryProxyTrayVerification]::IsUsable($mainWindow) -and -not [RetryProxyTrayVerification]::IsZoomed($mainWindow)
        } 'Window did not return to normal size before cache checks'
        Write-Host 'Tray minimize, hide and restore checks passed.'

        # 缓存明细是页面：导航后标题带通道名，代理不受影响。
        Invoke-NavigationItem $mainWindow '缓存明细'
        Wait-TrayCondition { $null -ne (Find-UiElement $mainWindow '缓存明细 · E2E') } 'Cache page did not show the channel title'
        $cachePageResult = Invoke-RestMethod "http://127.0.0.1:$proxyPort/test" -TimeoutSec 5
        if ($process.HasExited -or $cachePageResult.result -ne 'proxy-ok') { throw 'Opening the cache page interrupted the proxy' }
        Invoke-NavigationItem $mainWindow '运行概况'
        Write-Host 'Cache page check passed.'

        $client = [Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        $streamRequest = $client.GetAsync("http://127.0.0.1:$proxyPort/tray-stream")
        Wait-TrayCondition { Test-Path -LiteralPath (Join-Path $runtimeDir 'stream-started') } 'Stream did not start'
        Invoke-TrayDoubleClick $trayWindow
        Wait-TrayCondition { -not [RetryProxyTrayVerification]::IsWindowVisible($mainWindow) } 'Window could not hide while streaming'
        $hiddenHealth = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
        if ($hiddenHealth.metrics.active_requests -ne 1) { throw 'Hiding interrupted the active stream' }
        New-Item -ItemType File -Path (Join-Path $runtimeDir 'finish-stream') | Out-Null
        $streamResponse = $streamRequest.GetAwaiter().GetResult()
        $streamBody = $streamResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $streamResponse.IsSuccessStatusCode -or $streamBody -notmatch 'before-minimize' -or
            $streamBody -notmatch 'after-minimize' -or $streamBody -notmatch 'response.completed') {
            throw 'Stream did not complete while hidden'
        }
        $hiddenResult = Invoke-RestMethod "http://127.0.0.1:$proxyPort/test" -TimeoutSec 5
        Start-Sleep -Milliseconds 750
        if ($hiddenResult.result -ne 'proxy-ok' -or [RetryProxyTrayVerification]::IsWindowVisible($mainWindow)) {
            throw 'Background request failed or restored the hidden window'
        }
        Invoke-TrayDoubleClick $trayWindow
        Wait-TrayCondition { [RetryProxyTrayVerification]::IsUsable($mainWindow) } 'Tray double-click did not restore the hidden window after streaming'
        $final = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
        if ($final.metrics.failed_requests -ne 0 -or $final.metrics.active_requests -ne 0) {
            throw 'Tray operations left failed or unfinished requests'
        }
        Write-Host 'Hidden streaming checks passed.'

        Send-TrayWindowMessage $mainWindow 0x0112 0xF120
        Wait-TrayCondition {
            [RetryProxyTrayVerification]::IsUsable($mainWindow) -and -not [RetryProxyTrayVerification]::IsZoomed($mainWindow)
        } 'Window did not return to normal size'
        Start-Sleep -Milliseconds 500
        $healthyBounds = [RetryProxyTrayVerification]::Bounds($mainWindow)
        $damagedBounds = @(
            @{ Left = -32000; Top = -32000; Width = 160; Height = 28 },
            @{ Left = $healthyBounds.Left; Top = $healthyBounds.Top; Width = 160; Height = 28 },
            @{ Left = -32000; Top = -32000; Width = 1000; Height = 700 }
        )
        $recoveryLogPath = Join-Path $runtimeDir 'logs\retry-proxy.log'
        $recoveryPattern = 'WindowRect \{[^\r\n]+ -> WindowRect \{'
        $recoveredCount = 0
        foreach ($damage in $damagedBounds) {
            $recoveredCount++
            [RetryProxyTrayVerification]::SetBounds($mainWindow, $damage.Left, $damage.Top, $damage.Width, $damage.Height)
            # WPF 会立刻把 160×28 钳到 MinWidth/MinHeight，只核对位置是否已注入。
            Wait-TrayCondition {
                $bounds = [RetryProxyTrayVerification]::Bounds($mainWindow)
                ($bounds.Left -eq $damage.Left -and $bounds.Top -eq $damage.Top) -or [RetryProxyTrayVerification]::IsUsable($mainWindow)
            } 'Could not reproduce the damaged window rectangle'
            Wait-TrayCondition { [RetryProxyTrayVerification]::IsUsable($mainWindow) } 'Damaged window did not recover automatically'
            $restoredBounds = [RetryProxyTrayVerification]::Bounds($mainWindow)
            if (-not $restoredBounds.Equals($healthyBounds)) { throw 'Recovery did not preserve the last valid window rectangle' }
            # 每次注入都要留下一条恢复日志，等到日志出现再注入下一条，避免两次注入被合并成一次恢复。
            Wait-TrayCondition {
                [regex]::Matches((Get-Content -LiteralPath $recoveryLogPath -Raw -Encoding UTF8), $recoveryPattern).Count -ge $recoveredCount
            } "Window recovery did not log injected failure #$recoveredCount" 5
        }
        Start-Sleep -Milliseconds 500
        $recoveryLog = Get-Content -LiteralPath $recoveryLogPath -Raw -Encoding UTF8
        if ([regex]::Matches($recoveryLog, $recoveryPattern).Count -ne $damagedBounds.Count) {
            throw 'Window recovery did not record exactly one diagnostic for each injected failure'
        }
        $recoveredResult = Invoke-RestMethod "http://127.0.0.1:$proxyPort/test" -TimeoutSec 5
        if ($recoveredResult.result -ne 'proxy-ok') { throw 'Window recovery interrupted the proxy' }
        Write-Host 'Window recovery checks passed.'
        Wait-TrayCondition {
            (Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2).metrics.active_requests -eq 0
        } 'Last request did not finish before restart'
        $dailyBeforeRestart = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
        Send-TrayWindowMessage $mainWindow 0x0010 0
        Wait-TrayCondition { $process.HasExited } 'Closing the window no longer exits the program' 20
        Write-Host 'Close-to-exit check passed.'
    }
    if ($null -eq $dailyBeforeRestart) {
        $dailyBeforeRestart = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
    }
    foreach ($dailyRestart in 1..2) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        if (-not $process.WaitForExit(5000)) { throw 'Test application did not stop for restart verification' }
        $process.Dispose()
        $process = $null
        $mainWindow = [IntPtr]::Zero
        if ($VerifyTray) { [RetryProxyTrayVerification]::ClosePrivateDesktop() }
        # A rotated-away ordinary log must not erase already saved daily statistics.
        foreach ($dailySuffix in @('', '.1', '.2', '.3')) {
            $dailyOldLog = Join-Path $runtimeDir ('logs\retry-proxy.log' + $dailySuffix)
            if (Test-Path -LiteralPath $dailyOldLog) { Remove-Item -LiteralPath $dailyOldLog -Force }
        }
        $process = Start-App
        $dailyRestored = $null
        for ($attempt = 0; $attempt -lt 300; $attempt++) {
            try {
                $dailyRestored = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 1
                break
            } catch { Start-Sleep -Milliseconds 100 }
        }
        if ($null -eq $dailyRestored) { throw 'Application did not restart with saved statistics' }
        $dailyExpectedJson = $dailyBeforeRestart.metrics | ConvertTo-Json -Depth 12 -Compress
        $dailyActualJson = $dailyRestored.metrics | ConvertTo-Json -Depth 12 -Compress
        if ($dailyActualJson -ne $dailyExpectedJson) {
            Write-Host "expected: $dailyExpectedJson"
            Write-Host "actual:   $dailyActualJson"
            throw 'Daily request or cache statistics changed after restart'
        }
        if ($dailyRestart -eq 1) {
            Invoke-RestMethod "http://127.0.0.1:$proxyPort/v1/chat/completions" -Method Post -ContentType 'application/json' -Body '{"model":"gpt-test","messages":[]}' -TimeoutSec 5 | Out-Null
            Wait-TrayCondition {
                (Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2).metrics.active_requests -eq 0
            } 'Post-restart request did not finish'
            $dailyBeforeRestart = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
            if ($dailyBeforeRestart.metrics.total_requests -ne $dailyRestored.metrics.total_requests + 1 -or
                $dailyBeforeRestart.metrics.cache.input_tokens -ne 3000 -or
                $dailyBeforeRestart.metrics.cache.cached_tokens -ne 2200) {
                throw 'New requests did not continue the restored daily statistics'
            }
        }
    }
    Write-Host 'Restart and daily statistics checks passed.'
    if (Test-Path (Join-Path $runtimeDir "config.json")) { throw "EXE created config.json" }
    if (Test-Path (Join-Path $runtimeDir "User\config.json")) { throw "EXE created User\config.json while the configuration was injected" }
    [pscustomobject]@{ Result = $result.result; Retries = $final.metrics.retry_count; GenerationVerified = $true; ConfigFile = $false; TrayVerified = $VerifyTray.IsPresent; NaturalTrayEvents = $UseCurrentDesktop.IsPresent; WindowRecoveryVerified = $VerifyTray.IsPresent; CachePageVerified = $VerifyTray.IsPresent; DailyStatisticsVerified = $true; RestartCount = 2 }
}
catch {
    $logPath = Join-Path $runtimeDir 'logs\retry-proxy.log'
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Encoding UTF8 -Tail 30 | Write-Host }
    throw
}
finally {
    if ($null -ne $streamResponse) { $streamResponse.Dispose() }
    if ($null -ne $client) { $client.Dispose() }
    if ($null -ne $oldConfig) { $env:RETRY_PROXY_CONFIG_JSON = $oldConfig }
    else { Remove-Item Env:RETRY_PROXY_CONFIG_JSON -ErrorAction SilentlyContinue }
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    if ($null -ne $process) { $process.WaitForExit(5000) | Out-Null; $process.Dispose() }
    if ($VerifyTray) { [RetryProxyTrayVerification]::ClosePrivateDesktop() }
    if ($null -ne $job) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
    $resolvedRuntime = (Resolve-Path -LiteralPath $runtimeDir).ProviderPath.TrimEnd('\')
    if (-not $resolvedRuntime.Equals([IO.Path]::GetFullPath($runtimeDir), [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedRuntime.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolvedRuntime).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Runtime cleanup path escaped the temporary directory"
    }
    Remove-Item -LiteralPath $resolvedRuntime -Recurse -Force -ErrorAction SilentlyContinue
}
