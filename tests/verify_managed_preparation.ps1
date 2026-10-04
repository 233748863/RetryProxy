#requires -Version 7.0
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\RetryProxy.App\bin\x64\Debug\net9.0-windows10.0.22621.0\RetryProxy.exe'),
    [switch]$Screenshot
)
# M5：使用真实临时配置验证多 Key 准备、退出恢复和删除联动；所有请求只到本机假上游。
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runtime = Join-Path $tempRoot ('RetryProxyM4-' + [Guid]::NewGuid().ToString('N'))
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
$configPath = Join-Path $runtime 'User\config.json'
$eventsPath = Join-Path $runtime 'upstream-events.jsonl'
$secrets = @{
    a1 = 'sk-managed-fixture-a1-only'; a2 = 'sk-managed-fixture-a2-only'
    a3 = 'sk-managed-fixture-a3-only'; b1 = 'sk-managed-fixture-b1-only'
}
$codexToken = '00112233445566778899aabbccddeeff'
$claudeToken = 'ffeeddccbbaa99887766554433221100'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class M5Capture {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$shots = Join-Path $PSScriptRoot ('..\.tmp\M5-' + [Guid]::NewGuid().ToString('N'))
function Capture([string]$Name) {
    if (-not $Screenshot) { return }
    New-Item -ItemType Directory -Path $shots -Force | Out-Null
    $null = [M5Capture]::SetForegroundWindow($window)
    Start-Sleep -Milliseconds 650
    if ([M5Capture]::GetForegroundWindow() -ne $window) { throw '测试窗口未置前，取消截图以免捕获其它窗口' }
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    $bitmap = [Drawing.Bitmap]::new($bounds.Right - $bounds.Left, $bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $shots ($Name + '.png')))
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Write-Host "截图：$(Join-Path $shots ($Name + '.png'))"
}

function Wait-For([scriptblock]$Condition, [string]$Message, [int]$Seconds = 30) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        if ($process -and $process.HasExited) { throw "测试程序意外退出：$Message" }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Message
}

function Find-Elements([string]$Value, [switch]$ByName) {
    $property = if ($ByName) { [Windows.Automation.AutomationElement]::NameProperty } else { [Windows.Automation.AutomationElement]::AutomationIdProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($property, $Value)
    $uia = [Windows.Automation.AutomationElement]::FromHandle($window)
    foreach ($item in $uia.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) { $item }
    foreach ($popup in [RetryProxyTrayVerification]::WindowsForProcess($process.Id)) {
        if ($popup -eq $window -or -not [RetryProxyTrayVerification]::IsWindowVisible($popup)) { continue }
        $popupRoot = [Windows.Automation.AutomationElement]::FromHandle($popup)
        foreach ($item in $popupRoot.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) { $item }
    }
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

function Invoke-Control([string]$Value, [switch]$ByName) {
    Wait-For { @(Find-Elements $Value -ByName:$ByName | Where-Object { $_.Current.IsEnabled }).Count -gt 0 } "缺少可操作控件：$Value"
    Invoke-Element @(Find-Elements $Value -ByName:$ByName | Where-Object { $_.Current.IsEnabled })[0]
}

function Set-Field([string]$Id, [string]$Value) {
    Wait-For { @(Find-Elements $Id | Where-Object { $_.Current.IsEnabled }).Count -eq 1 } "缺少可编辑字段：$Id"
    $element = @(Find-Elements $Id)[0]
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); return }
    $edits = $element.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit))
    foreach ($edit in $edits) {
        if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); return }
    }
    throw "字段无法编辑：$Id"
}

function Select-Provider([string]$Name) {
    Wait-For { @(Find-Elements 'PreparationProvider' | Where-Object { $_.Current.IsEnabled }).Count -eq 1 } '准备供应商下拉框尚未启用'
    $picker = @(Find-Elements 'PreparationProvider')[0]
    $picker.SetFocus()
    $expand = $picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        $items = $picker.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
        $item = @($items | Where-Object { $_.Current.Name -eq $Name })[0]
        if ($null -eq $item) { throw "缺少供应商选项：$Name" }
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } finally { $expand.Collapse() }
}

function Select-Key([string]$KeyId) {
    Wait-For { @(Find-Elements "PreparationKey_$KeyId" | Where-Object { $_.Current.IsEnabled }).Count -eq 1 } "Key 无法勾选：$KeyId"
    $check = @(Find-Elements "PreparationKey_$KeyId")[0]
    $toggle = $check.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { throw "Key 未保持勾选：$KeyId" }
}

function Read-Config { [IO.File]::ReadAllText($configPath) | ConvertFrom-Json }
function Read-Events {
    if (Test-Path -LiteralPath $eventsPath) {
        foreach ($line in [IO.File]::ReadAllLines($eventsPath)) { if ($line.Trim()) { $line | ConvertFrom-Json } }
    }
}
function Task-ForKey([string]$KeyId) {
    $tasks = @((Read-Config).preparations | Where-Object { $_.providerSource -eq 'list' -and $_.providerId -eq 'a' -and $_.keyId -eq $KeyId })
    if ($tasks.Count -ne 1) { throw "Key $KeyId 对应的固定任务数量应为 1，实际为 $($tasks.Count)" }
    return $tasks[0]
}
function Test-TaskStatus([string]$TaskId, [string]$Expected) {
    return @(Find-Elements "PreparationTaskStatus_$TaskId" | Where-Object { $_.Current.Name -like $Expected }).Count -eq 1
}
function Test-KeyStatus([string]$KeyId, [string]$Expected) {
    return @(Find-Elements "PreparationStatus_$KeyId" | Where-Object { $_.Current.Name -like $Expected }).Count -eq 1
}
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Start-App {
    $script:process = if ($Screenshot) {
        Start-Process -FilePath (Join-Path $runtime 'RetryProxy.exe') -WorkingDirectory $runtime -PassThru
    } else {
        [RetryProxyTrayVerification]::StartPrivateProcess((Join-Path $runtime 'RetryProxy.exe'), $runtime)
    }
    # GetProcessById 返回的对象须在退出前取得句柄，才能在退出后读取原生退出码。
    $null = $process.Handle
    Wait-For {
        $script:window = [RetryProxyTrayVerification]::FindWindow($process.Id, 'LLM Retry Proxy', $null)
        return $window -ne [IntPtr]::Zero
    } '隔离程序未出现主窗口'
    Wait-For { @(Find-Elements 'ProviderNavigation').Count -eq 1 } '隔离程序导航未加载'
    Invoke-Control 'ProviderNavigation'
    Wait-For { @(Find-Elements 'ProxySettings').Count -eq 1 } '默认供应商页面未加载'
    Invoke-Control 'SelectCodex'
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

function Close-Drawer {
    Invoke-Control 'DrawerCancel'
    Wait-For { @(Find-Elements 'PreparationDrawer').Count -eq 0 -or @(Find-Elements '放弃修改' -ByName).Count -gt 0 } '准备抽屉没有关闭或显示放弃确认'
    if (@(Find-Elements 'PreparationDrawer').Count -gt 0) { Invoke-Control '放弃修改' -ByName }
    Wait-For { @(Find-Elements 'PreparationDrawer').Count -eq 0 } '准备抽屉未关闭'
    Start-Sleep -Milliseconds 250
}

function Open-KeyMenu([string]$KeyId) {
    Wait-For { @(Find-Elements $KeyId).Count -eq 1 } "找不到 Key 行：$KeyId"
    $uia = [Windows.Automation.AutomationElement]::FromHandle($window)
    # WPF 的 Grid/Border 不一定有自动化节点。按树内顺序定位所选 Key 后紧随的操作按钮，
    # 遇到下一把 Key 的切换按钮前仍找不到菜单就失败，绝不落到其他 Key 的删除入口。
    $found = $false
    foreach ($element in $uia.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        $id = $element.Current.AutomationId
        if ($id -eq $KeyId) { $found = $true; continue }
        if (-not $found) { continue }
        if ($id -in @('a1', 'a2', 'a3', 'b1')) { break }
        if ($element.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $element.Current.Name -eq 'Key 操作') {
            Invoke-Element $element
            return
        }
    }
    throw "无法安全定位 Key 操作菜单：$KeyId"
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
    $env:RETRY_PROXY_CLAUDE_CLI = Join-Path $runtime 'missing-claude.exe'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\preparation-codex.ps1') -Destination (Join-Path $runtime 'preparation-codex.ps1')
    $env:RETRY_PROXY_CODEX_CLI = Join-Path $runtime 'preparation-codex.ps1'
    $gate = Join-Path $runtime 'allow-preparation'
    New-Item -ItemType File -Path $gate | Out-Null
    $env:RETRY_PROXY_TEST_PREPARATION_GATE = $gate
    $ports = [Collections.Generic.HashSet[int]]::new()
    while ($ports.Count -lt 3) { $null = $ports.Add((Get-FreePort)) }
    $portList = @($ports)
    $upstreamPort = $portList[0]
    $codexPort = $portList[1]
    $claudePort = $portList[2]
    $job = Start-Job -ArgumentList $upstreamPort, $runtime, $secrets -ScriptBlock {
        param($Port, $Directory, $Keys)
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        New-Item -ItemType File -Path (Join-Path $Directory 'upstream.ready') | Out-Null
        try {
            while ($true) {
                $pending = $listener.GetContextAsync()
                while (-not $pending.IsCompleted) { Start-Sleep -Milliseconds 25 }
                $context = $pending.GetAwaiter().GetResult()
                $path = $context.Request.Url.AbsolutePath
                $authorization = $context.Request.Headers['Authorization']
                $keyId = @($Keys.Keys | Where-Object { $authorization -ceq ('Bearer ' + $Keys[$_]) })[0]
                $requestBody = $null
                if ($context.Request.HasEntityBody) {
                    $reader = [IO.StreamReader]::new($context.Request.InputStream)
                    try { $requestBody = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                }
                $valid = $null -ne $keyId -and $path -in @('/v1/responses', '/v1/models')
                $record = @{ keyId = $keyId; path = $path; valid = $valid; model = $requestBody.model } | ConvertTo-Json -Compress
                [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'), $record + "`n")
                $body = if (-not $valid) { '{"error":{"message":"invalid fixture credential or path"}}' }
                    elseif ($path -eq '/v1/models') { '{"data":[{"id":"preparation-test-model"}]}' }
                    else { "data: {`"type`":`"response.completed`",`"response`":{`"status`":`"completed`",`"usage`":{`"input_tokens`":40,`"output_tokens`":12},`"output`":[{`"content`":[{`"type`":`"output_text`",`"text`":`"准备成功`"}]}]}}`n`n" }
                $context.Response.StatusCode = if ($valid) { 200 } else { 401 }
                $context.Response.ContentType = if ($path -eq '/v1/models' -or -not $valid) { 'application/json' } else { 'text/event-stream' }
                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Close() }
    }
    Wait-For { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } '本机假上游未启动'
    $proxy = @{
        schema_version = 7; selected_route_id = 'fixture-codex'
        providers = @(
            @{ id = 'a'; name = '准备供应商 A'; client_type = 'codex'; base_url = "http://127.0.0.1:$upstreamPort"; models = @{ model = 'preparation-test-model' }; keys = @(
                @{ id = 'a1'; name = 'PLUS'; api_key = $secrets.a1 },
                @{ id = 'a2'; name = 'PRO'; api_key = $secrets.a2 },
                @{ id = 'a3'; name = 'PRO+'; api_key = $secrets.a3 }) },
            @{ id = 'b'; name = '当前供应商 B'; client_type = 'codex'; base_url = "http://127.0.0.1:$upstreamPort"; models = @{ model = 'preparation-test-model' }; keys = @(
                @{ id = 'b1'; name = '当前 Key'; api_key = $secrets.b1 }) }
        )
        routes = @(
            @{ id = 'fixture-codex'; name = 'Codex'; client_type = 'codex'; listen_port = $codexPort; current_provider_id = 'b'; current_key_id = 'b1'; local_token = $codexToken; keepalive_enabled = $false },
            @{ id = 'fixture-claude'; name = 'Claude Code'; client_type = 'claude'; listen_port = $claudePort; current_provider_id = ''; current_key_id = ''; local_token = $claudeToken; keepalive_enabled = $false }
        )
    }
    $all = @{
        proxy = $proxy; preparations = @()
        commonConfig = @{ clientSetupCompleted = $true; isFirstRun = $false; exitToTray = $false; startMinimized = $false }
        otherConfig = @{ uiCultureInfoName = 'zh-Hans' }
    }
    [IO.File]::WriteAllText($configPath, ($all | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
    Start-App
    Invoke-Control 'PreparationNavigation'
    Wait-For { @(Find-Elements 'AddPreparation').Count -eq 1 } '准备页面未加载'
    Invoke-Control 'AddPreparation'
    Wait-For { @(Find-Elements 'PreparationDrawer').Count -eq 1 } '准备抽屉未打开'
    Invoke-Control 'PreparationListProvider'
    Select-Provider '准备供应商 A'
    foreach ($keyId in @('a1', 'a2', 'a3')) { Select-Key $keyId }
    Capture 'preparation-drawer'
    # 模型留空，确认每把 Key 均从供应商设置解析有效模型。
    Set-Field 'PreparationModel' ''
    Set-Field 'PreparationIdleMinutes' '0.5'
    Invoke-Control 'DrawerSave'
    Wait-For { @(Find-Elements 'PreparationDrawer').Count -eq 0 } '批量提交后抽屉未关闭'
    Wait-For { @((Read-Config).preparations).Count -eq 3 } '一次勾选三把 Key 没有创建三项任务'
    $taskIds = @{}
    foreach ($keyId in @('a1', 'a2', 'a3')) {
        $task = Task-ForKey $keyId
        $taskIds[$keyId] = $task.id
        if ($task.apiKey -or $task.model -or -not $task.wasRunning) { throw "准备任务错误复制密钥、固化模型或缺少运行标记：$keyId" }
        Wait-For { Test-TaskStatus $taskIds[$keyId] '已准备*' } "准备任务未完成：$keyId"
    }
    foreach ($keyId in @('a1', 'a2', 'a3')) {
        if (@(Read-Events | Where-Object { $_.keyId -eq $keyId -and $_.model -eq 'preparation-test-model' }).Count -eq 0) {
            throw "假上游未收到对应 Key 与有效模型：$keyId"
        }
    }
    if (@(Read-Events | Where-Object { -not $_.valid -or $_.keyId -eq 'b1' }).Count -gt 0) { throw '批量准备使用了错误凭据或当前通道 Key' }
    Invoke-Control 'ProviderNavigation'
    Invoke-Control 'Fold_a'
    foreach ($keyId in @('a1', 'a2', 'a3')) {
        Wait-For { Test-KeyStatus $keyId '已准备*' } "供应商页没有同步准备状态：$keyId"
        if (@(Find-Elements "PrepareKey_$keyId" | Where-Object { $_.Current.Name -eq '停止' }).Count -ne 1) { throw "供应商 Key 行缺少停止入口：$keyId" }
    }
    Capture 'provider-preparation-states'
    Invoke-Control 'PreparationNavigation'
    Capture 'preparation-task-groups'
    Invoke-Control 'AddPreparation'
    Wait-For { @(Find-Elements 'PreparationDrawer').Count -eq 1 } '第二次准备抽屉未打开'
    Invoke-Control 'PreparationListProvider'
    Select-Provider '准备供应商 A'
    foreach ($keyId in @('a1', 'a2', 'a3')) {
        Wait-For { @(Find-Elements "PreparationKey_$keyId").Count -eq 1 } "已有任务的 Key 没有出现在列表：$keyId"
        $check = @(Find-Elements "PreparationKey_$keyId")[0]
        if ($check.Current.IsEnabled -or $check.Current.Name -notlike '*已有任务*') { throw "已有任务的 Key 仍可重复勾选：$keyId" }
    }
    Close-Drawer
    Invoke-Control "StopPreparation_$($taskIds.a1)"
    Wait-For { Test-TaskStatus $taskIds.a1 '已停止' } '手动停止第一项失败'
    Wait-For { -not (Task-ForKey 'a1').wasRunning } '手动停止没有清除持久运行标记'
    if (-not (Test-TaskStatus $taskIds.a2 '已准备*') -or -not (Test-TaskStatus $taskIds.a3 '已准备*')) { throw '手动停止一项影响了其他准备任务' }
    Stop-App
    if ((Task-ForKey 'a1').wasRunning -or -not (Task-ForKey 'a2').wasRunning -or -not (Task-ForKey 'a3').wasRunning) {
        throw '正常退出没有保留两项运行、一项停止的恢复意图'
    }
    $beforeRestart = @{}
    foreach ($keyId in @('a1', 'a2', 'a3')) { $beforeRestart[$keyId] = @(Read-Events | Where-Object { $_.keyId -eq $keyId }).Count }
    Start-App
    Invoke-Control 'PreparationNavigation'
    Wait-For { Test-TaskStatus $taskIds.a1 '已停止' } '重启后手动停止的任务自行启动了'
    foreach ($keyId in @('a2', 'a3')) {
        Wait-For { Test-TaskStatus $taskIds[$keyId] '已准备*' } "重启后任务没有恢复：$keyId"
        Wait-For { @(Read-Events | Where-Object { $_.keyId -eq $keyId }).Count -gt $beforeRestart[$keyId] } "重启后没有重新请求假上游：$keyId"
        if ((Task-ForKey $keyId).id -ne $taskIds[$keyId]) { throw "重启重新创建了重复任务：$keyId" }
    }
    if (@(Read-Events | Where-Object { $_.keyId -eq 'a1' }).Count -ne $beforeRestart.a1) { throw '手动停止的 Key 在重启后产生了准备请求' }
    if (@((Read-Config).preparations).Count -ne 3) { throw '重启改变了准备任务总数' }
    Invoke-Control 'ProviderNavigation'
    Invoke-Control 'Fold_a'
    Wait-For { Test-KeyStatus 'a2' '已准备*' } '重启后供应商状态没有同步'
    Open-KeyMenu 'a2'
    Invoke-Control '删除' -ByName
    Invoke-Control '确认删除' -ByName
    Wait-For {
        $saved = Read-Config
        return @($saved.proxy.providers | Where-Object id -eq 'a' | ForEach-Object keys | Where-Object id -eq 'a2').Count -eq 0 -and
            @($saved.preparations | Where-Object keyId -eq 'a2').Count -eq 0
    } '删除运行中 Key 没有同时移除其准备任务'
    if (@((Read-Config).preparations).Count -ne 2) { throw '删除一把 Key 误删其他准备任务' }
    Wait-For { Test-KeyStatus 'a3' '已准备*' } '删除第二把 Key 打断了第三把 Key'
    Invoke-Control 'PreparationNavigation'
    Wait-For { @(Find-Elements "PreparationTaskStatus_$($taskIds.a2)").Count -eq 0 } '已删除 Key 的准备任务仍显示在页面'
    if (-not (Test-TaskStatus $taskIds.a1 '已停止') -or -not (Test-TaskStatus $taskIds.a3 '已准备*')) { throw '删除联动改变了不相关任务状态' }
    Stop-App
    $finalConfig = Read-Config
    if (@($finalConfig.preparations | Where-Object keyId -eq 'a2').Count -gt 0) { throw '退出把已删除任务重新写回配置' }
    foreach ($task in $finalConfig.preparations) { if ($task.apiKey) { throw '管理来源任务复制了真实密钥' } }
    if ([IO.File]::ReadAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json')) -cne '{}' -or
        [IO.File]::ReadAllText((Join-Path $env:CODEX_HOME 'config.toml')) -cne '') { throw '独立准备修改了客户端配置' }
    $logText = [IO.File]::ReadAllText((Join-Path $runtime 'logs\retry-proxy.log'))
    foreach ($secret in @($secrets.Values) + @($codexToken, $claudeToken)) {
        if ($logText.Contains($secret, [StringComparison]::Ordinal)) { throw '准备日志包含密钥或通道口令' }
    }
    if (@(Read-Events | Where-Object { -not $_.valid }).Count -gt 0) { throw '假上游接收到错误认证或路径' }
    $passed = $true
    Write-Host 'PASS：三 Key 批量准备、供应商状态同步、已有任务禁选、手动停止不恢复、运行任务重启恢复、删除 Key 联动与密钥隔离。'
}
finally {
    if ($process) {
        if (-not $process.HasExited) {
            $null = [RetryProxyTrayVerification]::PostMessage($window, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero)
            if (-not $process.WaitForExit(30000)) { $process.Kill(); $null = $process.WaitForExit(5000) }
        }
        $process.Dispose()
    }
    [RetryProxyTrayVerification]::ClosePrivateDesktop()
    if ($job) { Stop-Job $job; Remove-Job $job -Force }
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    if ($passed -and (Test-Path -LiteralPath $runtime)) {
        $target = Get-Item -LiteralPath $runtime
        $resolved = $target.FullName.TrimEnd('\')
        if ($resolved -ne [IO.Path]::GetFullPath($runtime) -or
            -not $resolved.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            ($target.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '测试清理路径异常，已保留目录' }
        Remove-Item -LiteralPath $runtime -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $runtime) { Write-Host "失败现场保留：$runtime" }
}
