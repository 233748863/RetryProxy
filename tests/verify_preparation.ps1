param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\RetryProxy.exe'),
    [switch]$WithChannels,
    [switch]$CompactWindow
)
# 在私有桌面和临时目录验收独立准备，全部请求只到本机测试上游。
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runtime = Join-Path $tempRoot ('RetryProxyPreparation-' + [Guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
foreach ($name in @('RETRY_PROXY_CONFIG_JSON', 'RETRY_PROXY_CODEX_CLI', 'RETRY_PROXY_TEST_PREPARATION_GATE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$process = $null
$job = $null
$window = [IntPtr]::Zero
$initialBounds = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')

function Wait-For([scriptblock]$Condition, [string]$Message, [int]$Seconds = 15) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        if ($null -ne $process -and $process.HasExited) { throw "Test application exited unexpectedly ($($process.ExitCode)): $Message" }
        Start-Sleep -Milliseconds 75
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw $Message
}

function Find-Elements([string]$Value, [switch]$ById) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    $property = if ($ById) { [Windows.Automation.AutomationElement]::AutomationIdProperty } else { [Windows.Automation.AutomationElement]::NameProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($property, $Value)
    return $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Control([string]$Value, [switch]$ById) {
    Wait-For { @(Find-Elements $Value -ById:$ById).Count -gt 0 } "Missing control: $Value"
    foreach ($element in (Find-Elements $Value -ById:$ById)) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
        if ($element.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem) {
            $element.SetFocus()
            Start-Sleep -Milliseconds 200
            $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0100, [UIntPtr]13, [IntPtr]0x001C0001)
            $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0101, [UIntPtr]13, [IntPtr]::new([int64]0xC01C0001))
            return
        }
    }
    throw "Control cannot be invoked: $Value"
}

function Set-Field([string]$Id, [string]$Value) {
    $element = @(Find-Elements $Id -ById)[0]
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); return }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
    foreach ($edit in $element.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); return }
    }
    throw "Field cannot be edited: $Id"
}

function Assert-WindowStable([string]$Context) {
    # 只观察窗口，覆盖布局和窗口恢复的延迟；验收过程不改变窗口位置或尺寸。
    foreach ($sample in 1..4) {
        Start-Sleep -Milliseconds 150
        $bounds = [RetryProxyTrayVerification]::Bounds($window)
        if (-not $bounds.Equals($initialBounds)) { throw "Window bounds changed during $Context" }
    }
}

function Assert-DialogActionsVisible {
    $startButton = @(Find-Elements '开始后台准备' | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button })[0]
    if ($null -eq $startButton) { throw 'Preparation dialog action is missing' }
    $buttonBounds = $startButton.Current.BoundingRectangle
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    if ($buttonBounds.IsEmpty -or $buttonBounds.Width -le 0 -or $buttonBounds.Height -le 0 -or
        $buttonBounds.Left -lt $bounds.Left -or $buttonBounds.Top -lt $bounds.Top -or
        $buttonBounds.Right -gt $bounds.Right -or $buttonBounds.Bottom -gt $bounds.Bottom) {
        throw 'Preparation dialog action is clipped'
    }
}

function Get-PreparationDialogLayout {
    $layout = @{}
    foreach ($id in @('PreparationDialog', 'PreparationCodex', 'PreparationClaude', 'PreparationLocalProvider', 'PreparationCustomProvider',
        'PreparationProviderUrl', 'PreparationApiKey', 'PreparationModel', 'PreparationIdleMinutes')) {
        $element = @(Find-Elements $id -ById | Where-Object { -not $_.Current.IsOffscreen })[0]
        if ($null -ne $element) { $layout[$id] = $element.Current.BoundingRectangle }
    }
    $primaryButton = @(Find-Elements '开始后台准备' | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button })[0]
    if ($null -eq $primaryButton) { throw 'Preparation dialog action is missing' }
    $layout['PrimaryButton'] = $primaryButton.Current.BoundingRectangle
    return $layout
}

function Assert-ClientSwitchKeepsDialogLayout([string]$Mode) {
    Start-Sleep -Milliseconds 300
    $expected = Get-PreparationDialogLayout
    foreach ($client in @('PreparationClaude', 'PreparationCodex', 'PreparationClaude', 'PreparationCodex')) {
        Invoke-Control $client -ById
        foreach ($sample in 1..3) {
            Start-Sleep -Milliseconds 150
            $actual = Get-PreparationDialogLayout
            foreach ($control in $expected.Keys) {
                if (-not $actual.ContainsKey($control)) { throw "$Mode client switch hid $control" }
                foreach ($dimension in @('X', 'Y', 'Width', 'Height')) {
                    if ([Math]::Abs($actual[$control].$dimension - $expected[$control].$dimension) -gt 0.5) {
                        throw "$Mode client switch changed $control $dimension from $($expected[$control].$dimension) to $($actual[$control].$dimension)"
                    }
                }
            }
        }
    }
    Assert-DialogActionsVisible
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Add-Preparation([string]$Minutes) {
    Invoke-Control 'AddPreparation' -ById
    Wait-For { @(Find-Elements 'PreparationCodex' -ById).Count -gt 0 } 'Preparation dialog did not open'
    Assert-WindowStable 'opening the preparation dialog'
    if (@(Find-Elements 'PreparationClaude' -ById).Count -eq 0) { throw 'Independent client selector is missing' }
    Assert-ClientSwitchKeepsDialogLayout 'Local provider'
    Invoke-Control 'PreparationCustomProvider' -ById
    Assert-ClientSwitchKeepsDialogLayout 'Custom provider'
    Assert-WindowStable 'switching the preparation client and provider source'
    Set-Field 'PreparationProviderUrl' "http://127.0.0.1:$upstreamPort"
    Set-Field 'PreparationApiKey' 'sk-prepare-fixture'
    Invoke-Control '获取模型'
    Wait-For { @(Find-Elements '获取模型' | Where-Object { $_.Current.IsEnabled }).Count -gt 0 } 'Model lookup did not finish'
    Set-Field 'PreparationModel' 'preparation-test-model'
    Set-Field 'PreparationIdleMinutes' $Minutes
    Assert-DialogActionsVisible
    Invoke-Control '开始后台准备'
    Wait-For { @(Find-Elements 'PreparationModel' -ById).Count -eq 0 } 'Preparation dialog did not close'
    Assert-WindowStable 'starting preparation and closing the dialog'
}

try {
    New-Item -ItemType Directory -Path $runtime | Out-Null
    Get-ChildItem -LiteralPath (Split-Path -Parent $ExePath) -File | Copy-Item -Destination $runtime
    $translations = Join-Path (Split-Path -Parent $ExePath) 'User\I18n'
    if (Test-Path -LiteralPath $translations) {
        New-Item -ItemType Directory -Path (Join-Path $runtime 'User') | Out-Null
        Copy-Item -LiteralPath $translations -Destination (Join-Path $runtime 'User\I18n') -Recurse
    }
    $upstreamPort = Get-FreePort
    $eventPath = Join-Path $runtime 'upstream-events.jsonl'
    $gate = Join-Path $runtime 'allow-preparation'
    New-Item -ItemType File -Path $gate | Out-Null
    $job = Start-Job -ArgumentList $upstreamPort, $runtime -ScriptBlock {
        param($Port, $Directory)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        New-Item -ItemType File -Path (Join-Path $Directory 'upstream.ready') | Out-Null
        try {
            while ($true) {
                $pending = $listener.GetContextAsync()
                while (-not $pending.IsCompleted) { Start-Sleep -Milliseconds 50 }
                $context = $pending.GetAwaiter().GetResult()
                $path = $context.Request.Url.AbsolutePath
                $authOk = $context.Request.Headers['Authorization'] -eq 'Bearer sk-prepare-fixture'
                $record = @{ path = $path; authOk = $authOk } | ConvertTo-Json -Compress
                [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'), $record + "`n")
                $body = if ($path -eq '/v1/models') { '{"data":[{"id":"preparation-test-model"}]}' } else { "data: {`"type`":`"response.completed`",`"response`":{`"status`":`"completed`"}}`n`n" }
                $context.Response.ContentType = if ($path -eq '/v1/models') { 'application/json' } else { 'text/event-stream' }
                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Close() }
    }
    Wait-For { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } 'Fixture upstream did not start'
    $config = @{ schema_version = 6; upstream_base_url = ''; providers = @(); routes = @(); desired_running = $false }
    if ($WithChannels) {
        $config.providers = @(@{ name = 'fixture'; base_url = "http://127.0.0.1:$upstreamPort" })
        $config.routes = @(
            @{ id = 'fixture-codex'; name = 'fixture-codex'; provider_name = 'fixture'; client_type = 'codex'; listen_port = (Get-FreePort); desired_running = $false },
            @{ id = 'fixture-claude'; name = 'fixture-claude'; provider_name = 'fixture'; client_type = 'claude'; listen_port = (Get-FreePort); desired_running = $false }
        )
        $config.selected_route_id = 'fixture-codex'
    }
    $env:RETRY_PROXY_CONFIG_JSON = $config | ConvertTo-Json -Depth 8 -Compress
    $env:RETRY_PROXY_CODEX_CLI = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'fixtures\preparation-codex.ps1')).ProviderPath
    $env:RETRY_PROXY_TEST_PREPARATION_GATE = $gate
    $process = [RetryProxyTrayVerification]::StartPrivateProcess((Join-Path $runtime 'RetryProxy.exe'), $runtime)
    Wait-For {
        $script:window = [RetryProxyTrayVerification]::FindWindow($process.Id, 'LLM Retry Proxy', $null)
        return $window -ne [IntPtr]::Zero
    } 'Application window did not start'
    Wait-For { @(Find-Elements 'PreparationNavigation' -ById).Count -gt 0 } 'Application navigation did not load'
    if ($CompactWindow) {
        # 仅调整私有桌面的测试窗口；按初始窗口比例缩小，兼容系统显示缩放。
        $bounds = [RetryProxyTrayVerification]::Bounds($window)
        $compactWidth = [int][Math]::Round(($bounds.Right - $bounds.Left) * 760.0 / 900)
        $compactHeight = [int][Math]::Round(($bounds.Bottom - $bounds.Top) * 520.0 / 600)
        [RetryProxyTrayVerification]::SetBounds($window, $bounds.Left, $bounds.Top, $compactWidth, $compactHeight)
        Wait-For {
            $current = [RetryProxyTrayVerification]::Bounds($window)
            return $current.Right - $current.Left -eq $compactWidth -and $current.Bottom - $current.Top -eq $compactHeight
        } 'Private test window did not reach its compact size'
    }
    $initialBounds = [RetryProxyTrayVerification]::Bounds($window)
    Invoke-Control 'PreparationNavigation' -ById
    Wait-For { @(Find-Elements '尚未添加准备任务').Count -gt 0 } 'Empty preparation page was not shown'
    Assert-WindowStable 'opening the preparation page'
    Add-Preparation '5'
    Wait-For { @(Find-Elements '保活中').Count -gt 0 } 'First preparation did not complete'
    Assert-WindowStable 'completing preparation'
    Write-Host 'Independent preparation and model lookup passed.'

    Remove-Item -LiteralPath $gate
    Add-Preparation '7.5'
    Wait-For { @(Find-Elements '终止准备').Count -gt 0 } 'Second preparation was not pending'
    Invoke-Control '终止准备'
    Wait-For { @(Find-Elements '已停止').Count -gt 0 } 'Second task did not stop'
    if (@(Find-Elements '保活中').Count -eq 0) { throw 'Stopping the second task interrupted the first task' }
    New-Item -ItemType File -Path $gate | Out-Null
    Invoke-Control '开始准备'
    Wait-For { @(Find-Elements '保活中').Count -ge 2 } 'Second task did not restart independently'
    Assert-WindowStable 'adding and restarting a second preparation task'

    Invoke-Control '首页'
    if ($WithChannels) {
        Wait-For { @(Find-Elements 'ChannelConfiguration' -ById).Count -gt 0 } 'Home configuration is missing'
        $configuration = @(Find-Elements 'ChannelConfiguration' -ById)[0]
        $configuration.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Wait-For { @(Find-Elements '通道选择').Count -gt 0 } 'Home channel selector is missing'
        $picker = @(Find-Elements '通道选择')[0]
        $expand = $picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
        $items = $picker.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
        $channel = @($items | Where-Object { $_.Current.Name -like 'fixture-claude*' })[0]
        if ($null -eq $channel) { throw 'Second channel was not found' }
        $channel.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $expand.Collapse()
        Wait-For {
            $selection = $picker.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
            return $selection.Count -gt 0 -and $selection[0].Current.Name -like 'fixture-claude*'
        } 'Home did not switch to the second channel'
    }
    Assert-WindowStable 'opening the home page and selecting a channel'
    Invoke-Control 'PreparationNavigation' -ById
    Wait-For { @(Find-Elements '保活中').Count -ge 2 } 'Navigation or channel selection changed preparation state'
    if (@(Find-Elements '准备 1 · Codex').Count -eq 0 -or @(Find-Elements '准备 2 · Codex').Count -eq 0) { throw 'Task identities changed with the channel' }
    Assert-WindowStable 'returning to the preparation tasks'
    Invoke-Control '停止保活'
    Wait-For { @(Find-Elements '已停止').Count -gt 0 } 'First task did not stop'
    Invoke-Control '停止保活'
    Wait-For { @(Find-Elements '已停止').Count -ge 2 } 'Second task did not stop'
    Invoke-Control '删除'
    Invoke-Control '删除'
    Wait-For { @(Find-Elements '尚未添加准备任务').Count -gt 0 } 'Stopped tasks could not be removed'
    Assert-WindowStable 'stopping and removing preparation tasks'
    Invoke-Control 'AddPreparation' -ById
    Wait-For { @(Find-Elements 'PreparationCodex' -ById).Count -gt 0 } 'Preparation dialog did not reopen'
    Assert-DialogActionsVisible
    Invoke-Control '取消'
    Wait-For { @(Find-Elements 'PreparationCodex' -ById).Count -eq 0 } 'Preparation dialog was not cancelled'
    Assert-WindowStable 'cancelling the preparation dialog'
    $events = @(Get-Content -LiteralPath $eventPath | ForEach-Object { $_ | ConvertFrom-Json })
    if (@($events | Where-Object { -not $_.authOk }).Count -gt 0) { throw 'Preparation forwarded an incorrect credential' }
    if (@($events | Where-Object path -eq '/v1/models').Count -ne 2) { throw 'Model lookup did not use the selected provider' }
    $log = Get-Content -LiteralPath (Join-Path $runtime 'logs\retry-proxy.log') -Raw
    if ($log.Contains('sk-prepare-fixture')) { throw 'Preparation logged its API key' }
    if (Test-Path -LiteralPath (Join-Path $runtime 'User\config.json')) { throw 'Preparation persisted temporary settings' }
    Write-Host "Preparation UI verification passed; window and dialog bounds stayed unchanged. WithChannels=$($WithChannels.IsPresent), CompactWindow=$($CompactWindow.IsPresent)"
}
catch {
    $logPath = Join-Path $runtime 'logs\retry-proxy.log'
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 20 | Write-Host }
    throw
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero)
            if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force }
        }
        $process.Dispose()
    }
    [RetryProxyTrayVerification]::ClosePrivateDesktop()
    if ($null -ne $job) { Stop-Job $job; Remove-Job $job -Force }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    if (Test-Path -LiteralPath $runtime) {
        $resolved = (Resolve-Path -LiteralPath $runtime).ProviderPath.TrimEnd('\')
        if ($resolved -ne [IO.Path]::GetFullPath($runtime) -or -not $resolved.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Test cleanup path escaped the temporary directory'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
