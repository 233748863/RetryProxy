param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\RetryProxy.App\bin\x64\Debug\net9.0-windows10.0.22621.0\RetryProxy.exe'),
    [switch]$WithChannels,
    [switch]$CompactWindow
)
# 在私有桌面和临时目录验收独立准备，全部请求只到本机测试上游。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime_files.ps1')
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$runtime = Join-Path $tempRoot ('RetryProxyM4-' + [Guid]::NewGuid().ToString('N'))
$savedEnvironment = @{}
foreach ($name in @('RETRY_PROXY_CONFIG_JSON', 'RETRY_PROXY_UI_TEST_ROOT', 'RETRY_PROXY_CLAUDE_CLI', 'RETRY_PROXY_CODEX_CLI', 'RETRY_PROXY_TEST_PREPARATION_GATE', 'CLAUDE_CONFIG_DIR', 'CODEX_HOME', 'UPSTREAM_BASE_URL', 'RETRY_PROXY_PORT', 'RETRY_MAX_RETRIES', 'RETRY_TIMEOUT_SECONDS', 'RETRY_GENERATION_TIMEOUT_SECONDS', 'RETRY_TOTAL_TIMEOUT_SECONDS', 'RETRY_BASE_DELAY_SECONDS', 'RETRY_MAX_DELAY_SECONDS')) {
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
    $matches = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($matches.Count -gt 0) { return $matches }
    foreach ($popup in [RetryProxyTrayVerification]::WindowsForProcess($process.Id)) {
        if ($popup -eq $window -or -not [RetryProxyTrayVerification]::IsWindowVisible($popup)) { continue }
        $popupRoot = [Windows.Automation.AutomationElement]::FromHandle($popup)
        $popupRoot.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    }
}

function Invoke-Control([string]$Value, [switch]$ById) {
    Wait-For { @(Find-Elements $Value -ById:$ById | Where-Object { $_.Current.IsEnabled }).Count -gt 0 } "Missing enabled control: $Value"
    foreach ($element in (Find-Elements $Value -ById:$ById | Where-Object { $_.Current.IsEnabled })) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
        if ($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
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
    Wait-For { @(Find-Elements $Id -ById | Where-Object { $_.Current.IsEnabled }).Count -gt 0 } "Missing enabled field: $Id"
    $element = @(Find-Elements $Id -ById)[0]
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); return }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
    foreach ($edit in $element.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); return }
    }
    throw "Field cannot be edited: $Id"
}

function Select-ComboItem([string]$Id, [string]$Text, [switch]$ByName) {
    $picker = @(Find-Elements $Id -ById:(-not $ByName))[0]
    if ($null -eq $picker) { throw "Missing picker: $Id" }
    # 抽屉出现后还有 200ms 动画，此时表单节点存在但暂时禁用。
    Wait-For { $picker.Current.IsEnabled } "Picker is not enabled: $Id"
    $picker.SetFocus()
    $expand = $picker.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
        $items = $picker.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
        $item = @($items | Where-Object { $_.Current.Name -like $Text })[0]
        if ($null -eq $item) { throw "Missing option $Text in $Id" }
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } finally { $expand.Collapse() }
}

function Get-ComboSelection([string]$Id) {
    $picker = @(Find-Elements $Id -ById)[0]
    $selection = $picker.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($selection.Count -gt 0) { return $selection[0].Current.Name }
    return ''
}

function Assert-WindowStable([string]$Context) {
    # 只观察窗口，覆盖布局和窗口恢复的延迟；验收过程不改变窗口位置或尺寸。
    foreach ($sample in 1..4) {
        Start-Sleep -Milliseconds 150
        $bounds = [RetryProxyTrayVerification]::Bounds($window)
        if (-not $bounds.Equals($initialBounds)) { throw "Window bounds changed during $Context" }
    }
}

function Get-LogCount {
    $counter = @(Find-Elements 'LogCounter' -ById)[0]
    if ($null -eq $counter) { return -1 }
    return [int]($counter.Current.Name.Split('/')[0].Trim())
}

function Assert-LogControlsVisible {
    $rectangles = @()
    foreach ($id in @('LogSourceAll', 'LogSourceProxy', 'LogSourceKeepAlive', 'LogSourcePreparation', 'LogSourceSystem', 'LogLevelAll', 'LogLevelWarning', 'LogSearch')) {
        $control = @(Find-Elements $id -ById)[0]
        if ($null -eq $control) { throw "Missing log control: $id" }
        $rect = $control.Current.BoundingRectangle
        if ($control.Current.IsOffscreen -or $rect.Width -le 0 -or $rect.Height -le 0 -or $rect.Left -lt $initialBounds.Left -or $rect.Right -gt $initialBounds.Right -or $rect.Top -lt $initialBounds.Top -or $rect.Bottom -gt $initialBounds.Bottom) {
            throw "Log control is outside the private window: $id"
        }
        foreach ($other in $rectangles) {
            if ($rect.IntersectsWith($other)) { throw "Log filters overlap: $id" }
        }
        $rectangles += $rect
    }
}

function Assert-DrawerActionsVisible {
    foreach ($id in @('DrawerSave', 'DrawerCancel')) {
        $button = @(Find-Elements $id -ById)[0]
        if ($null -eq $button) { throw "准备抽屉缺少操作按钮：$id" }
        $rect = $button.Current.BoundingRectangle
        $bounds = [RetryProxyTrayVerification]::Bounds($window)
        if ($button.Current.IsOffscreen -or $rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0 -or
            $rect.Left -lt $bounds.Left -or $rect.Top -lt $bounds.Top -or
            $rect.Right -gt $bounds.Right -or $rect.Bottom -gt $bounds.Bottom) {
            throw "准备抽屉操作按钮被裁切：$id"
        }
    }
}

function Assert-DrawerClient([string]$Client) {
    Wait-For { @(Find-Elements 'PreparationClient' -ById | Where-Object { $_.Current.Name -eq $Client }).Count -eq 1 } "准备抽屉未固定显示当前客户端：$Client"
    foreach ($id in @('PreparationCodex', 'PreparationClaude')) {
        if (@(Find-Elements $id -ById).Count -gt 0) { throw "准备抽屉仍包含独立客户端切换：$id" }
    }
    Assert-DrawerActionsVisible
}

function Get-Field([string]$Id) {
    $element = @(Find-Elements $Id -ById)[0]
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
    foreach ($edit in $element.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { return $pattern.Current.Value }
    }
    throw "无法读取字段：$Id"
}

function Get-TaskIds {
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    foreach ($element in $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($element.Current.AutomationId -match '^PreparationTaskStatus_(.+)$') { $Matches[1] }
    }
}

function Test-TaskStatus([string]$Id, [string]$Status) {
    return @(Find-Elements "PreparationTaskStatus_$Id" -ById | Where-Object { $_.Current.Name -like $Status }).Count -eq 1
}

function Close-PreparationDrawer {
    Invoke-Control 'DrawerCancel' -ById
    # 修改过的草稿由抽屉统一确认丢弃；从未修改则直接关闭。
    Wait-For { @(Find-Elements 'PreparationDrawer' -ById).Count -eq 0 -or @(Find-Elements '放弃修改').Count -gt 0 } '准备抽屉未关闭或未询问是否放弃修改'
    if (@(Find-Elements 'PreparationDrawer' -ById).Count -gt 0) { Invoke-Control '放弃修改' }
    Wait-For { @(Find-Elements 'PreparationDrawer' -ById).Count -eq 0 } '准备抽屉未关闭'
}

function Fetch-PreparationModels {
    $before = @(Get-Content -LiteralPath $eventPath -ErrorAction SilentlyContinue | Where-Object { $_ -match '/v1/models' }).Count
    Invoke-Control 'PreparationFetchModels' -ById
    Wait-For {
        $count = @(Get-Content -LiteralPath $eventPath -ErrorAction SilentlyContinue | Where-Object { $_ -match '/v1/models' }).Count
        return $count -gt $before -and @(Find-Elements 'PreparationFetchModels' -ById | Where-Object { $_.Current.IsEnabled }).Count -eq 1
    } '获取模型未完成'
    if ((Get-Field 'PreparationModel') -ne '') { throw '获取模型后自动填入了首个模型，应由用户主动选择' }
    Select-ComboItem 'PreparationModel' 'preparation-test-model'
    Wait-For { (Get-Field 'PreparationModel') -eq 'preparation-test-model' } '手动选择的模型未写入表单'
}

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Add-Preparation([string]$Minutes, [string]$Effort) {
    $before = @(Get-TaskIds)
    Invoke-Control 'AddPreparation' -ById
    Wait-For { @(Find-Elements 'PreparationDrawer' -ById).Count -eq 1 } '准备抽屉未打开'
    Assert-WindowStable '打开准备抽屉'
    Assert-DrawerClient 'Codex'
    Invoke-Control 'PreparationLocalProvider' -ById
    Assert-DrawerActionsVisible
    Invoke-Control 'PreparationCustomProvider' -ById
    Set-Field 'PreparationProviderUrl' "http://127.0.0.1:$upstreamPort"
    # 密码控件不允许 UIA 写值，先显示密钥，再操作对应明文输入框。
    Invoke-Control '显示密钥'
    Set-Field 'PreparationVisibleApiKey' 'sk-prepare-fixture'
    Set-Field 'PreparationModel' ''
    Fetch-PreparationModels
    Select-ComboItem 'PreparationReasoningEffort' '极限 · ultra'
    Select-ComboItem 'PreparationReasoningEffort' $Effort
    Set-Field 'PreparationIdleMinutes' $Minutes
    Assert-DrawerActionsVisible
    Invoke-Control 'DrawerSave' -ById
    Wait-For { @(Find-Elements 'PreparationDrawer' -ById).Count -eq 0 } '提交后准备抽屉未关闭'
    Wait-For { @(Get-TaskIds | Where-Object { $_ -notin $before }).Count -eq 1 } '未新增唯一准备任务'
    Assert-WindowStable '提交准备并关闭抽屉'
    return @(Get-TaskIds | Where-Object { $_ -notin $before })[0]
}

try {
    New-Item -ItemType Directory -Path $runtime | Out-Null
    Copy-RetryProxyRuntime -ExePath $ExePath -DestinationDirectory $runtime
    $translations = Join-Path (Split-Path -Parent $ExePath) 'User\I18n'
    if (Test-Path -LiteralPath $translations) {
        New-Item -ItemType Directory -Path (Join-Path $runtime 'User') | Out-Null
        Copy-Item -LiteralPath $translations -Destination (Join-Path $runtime 'User\I18n') -Recurse
    }
    # 清除当前终端的临时覆盖，确保地址和端口只来自下方本机测试配置。
    foreach ($name in $savedEnvironment.Keys) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    $env:RETRY_PROXY_UI_TEST_ROOT = $runtime
    $env:CLAUDE_CONFIG_DIR = Join-Path $runtime 'claude'
    $env:CODEX_HOME = Join-Path $runtime 'codex'
    $env:RETRY_PROXY_CLAUDE_CLI = Join-Path $runtime 'missing-claude.exe'
    New-Item -ItemType Directory -Path $env:CLAUDE_CONFIG_DIR, $env:CODEX_HOME -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json'), '{}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'config.toml'), '', [Text.UTF8Encoding]::new($false))
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
                $apiKeyOk = $context.Request.Headers['x-api-key'] -eq 'sk-prepare-fixture'
                $effort = $null
                if ($path -ne '/v1/models') {
                    $reader = [IO.StreamReader]::new($context.Request.InputStream)
                    try { $requestBody = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                    $effort = $requestBody.reasoning.effort
                }
                $record = @{ path = $path; authOk = $authOk; apiKeyOk = $apiKeyOk; effort = $effort } | ConvertTo-Json -Compress
                [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'), $record + "`n")
                $body = if ($path -eq '/v1/models') { '{"data":[{"id":"preparation-test-model"}]}' } else { "data: {`"type`":`"response.completed`",`"response`":{`"status`":`"completed`",`"usage`":{`"input_tokens`":40,`"output_tokens`":12},`"output`":[{`"content`":[{`"type`":`"output_text`",`"text`":`"准备成功`"}]}]}}`n`n" }
                $context.Response.ContentType = if ($path -eq '/v1/models') { 'application/json' } else { 'text/event-stream' }
                if ($path -eq '/v1/models' -and -not $authOk) {
                    $context.Response.StatusCode = 401
                    $body = '{"error":{"message":"fixture requires Bearer auth"}}'
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($body)
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Close() }
    }
    Wait-For { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } 'Fixture upstream did not start'
    # 每个客户端一条通道，无供应商时仍可查看统计；只使用随机测试端口。
    $codexPort = Get-FreePort
    $claudePort = Get-FreePort
    while ($claudePort -eq $codexPort) { $claudePort = Get-FreePort }
    $config = @{
        schema_version = 7; providers = @(); selected_route_id = 'fixture-codex'
        routes = @(
            @{ id = 'fixture-codex'; name = 'Codex'; client_type = 'codex'; listen_port = $codexPort; current_provider_id = ''; current_key_id = ''; local_token = '00112233445566778899aabbccddeeff'; desired_running = $false; keepalive_enabled = $false },
            @{ id = 'fixture-claude'; name = 'Claude Code'; client_type = 'claude'; listen_port = $claudePort; current_provider_id = ''; current_key_id = ''; local_token = 'ffeeddccbbaa99887766554433221100'; desired_running = $false; keepalive_enabled = $false }
        )
    }
    if ($WithChannels) {
        $longProvider = 'fixture-' + ('供应商长名称校验' * 8)
        $config.providers = @(
            @{ id = 'provider-codex'; client_type = 'codex'; name = 'fixture'; base_url = "http://127.0.0.1:$upstreamPort"; keys = @(@{ id = 'key-codex'; name = '主号'; api_key = 'sk-prepare-fixture' }) },
            @{ id = 'provider-claude'; client_type = 'claude'; name = $longProvider; base_url = "http://127.0.0.1:$upstreamPort"; keys = @(@{ id = 'key-claude'; name = '主号'; api_key = 'sk-prepare-fixture' }) }
        )
        $config.routes[0].current_provider_id = 'provider-codex'
        $config.routes[0].current_key_id = 'key-codex'
        $config.routes[1].current_provider_id = 'provider-claude'
        $config.routes[1].current_key_id = 'key-claude'
    }
    $env:RETRY_PROXY_CONFIG_JSON = $config | ConvertTo-Json -Depth 8 -Compress
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\preparation-codex.ps1') -Destination (Join-Path $runtime 'preparation-codex.ps1')
    $env:RETRY_PROXY_CODEX_CLI = Join-Path $runtime 'preparation-codex.ps1'
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
    Wait-For { @(Find-Elements 'ProxySettings' -ById).Count -gt 0 } 'Application did not open Providers by default'
    Invoke-Control 'SelectCodex' -ById
    $destinations = @(
        @{ Navigation = 'StatisticsNavigation'; Marker = 'StatisticsTabOverview' },
        @{ Navigation = 'PreparationNavigation'; Marker = 'AddPreparation' },
        @{ Navigation = '运行日志'; Marker = 'LogCounter'; ByName = $true },
        @{ Navigation = '软件设置'; Marker = 'SwitchAppearance'; ByName = $true }
    )
    foreach ($destination in $destinations) {
        Invoke-Control $destination.Navigation -ById:(-not $destination.ByName)
        Wait-For { @(Find-Elements $destination.Marker -ById).Count -gt 0 } "Navigation opened the wrong page: $($destination.Navigation)"
        Assert-WindowStable "opening $($destination.Navigation)"
        Invoke-Control 'ProviderNavigation' -ById
        Wait-For { @(Find-Elements 'ProxySettings' -ById).Count -gt 0 } 'Navigation did not return to Providers'
    }
    Invoke-Control 'StatisticsNavigation' -ById
    Wait-For { @(Find-Elements 'StatisticsTabOverview' -ById).Count -gt 0 } 'Statistics did not load'
    # 概况/缓存/在途三个子标签各自一屏，切换后只显示当前标签的内容。
    Invoke-Control 'StatisticsTabCache' -ById
    Wait-For { @(Find-Elements '统计范围').Count -gt 0 } 'Statistics lost the cache details section'
    Invoke-Control 'StatisticsTabActive' -ById
    Wait-For {
        @(Find-Elements '统计范围').Count -eq 0 -and @(Find-Elements '在途请求').Count -gt 0
    } 'Statistics did not switch to the active requests tab'
    Invoke-Control 'StatisticsTabOverview' -ById
    Wait-For {
        @(Find-Elements '在途请求').Count -eq 0 -and @(Find-Elements 'OverviewTotalRequests' -ById).Count -gt 0
    } 'Statistics did not return to the overview tab'
    foreach ($id in @('OverviewChannelPicker', 'CacheChannelPicker', 'EnableAllChannels', 'DisableAllChannels')) {
        if (@(Find-Elements $id -ById).Count -gt 0) { throw "Removed control is still on Statistics: $id" }
    }
    $expectedProviders = if ($WithChannels) {
        @(@{ Selector = 'SelectClaude'; Provider = $longProvider }, @{ Selector = 'SelectCodex'; Provider = 'fixture' })
    } else {
        @(@{ Selector = 'SelectClaude'; Provider = '未选择供应商' }, @{ Selector = 'SelectCodex'; Provider = '未选择供应商' })
    }
    foreach ($client in $expectedProviders) {
        Invoke-Control $client.Selector -ById
        Wait-For {
            $current = @(Find-Elements 'StatisticsCurrentProviderKey' -ById)[0]
            return $null -ne $current -and $current.Current.Name.Contains($client.Provider)
        } 'Client selection did not update Statistics'
        Assert-WindowStable 'switching the current client in Statistics'
    }
    if (-not $WithChannels) {
        Wait-For { @(Find-Elements '未选择供应商').Count -gt 0 } 'Statistics lost the unconfigured-client state'
    }
    Invoke-Control 'ProviderNavigation' -ById
    Wait-For { @(Find-Elements 'ProxySettings' -ById).Count -gt 0 } 'Statistics did not lead back to Providers'
    Wait-For { @(Find-Elements 'ProviderKeepAliveHint' -ById).Count -gt 0 } 'Providers lost the channel summary line'
    Invoke-Control 'PreparationNavigation' -ById
    Wait-For { @(Find-Elements '当前客户端尚未添加准备任务').Count -gt 0 } 'Empty preparation page was not shown'
    Assert-WindowStable 'opening the preparation page'
    $firstTaskId = Add-Preparation '0.5' '低 · low'
    Wait-For { Test-TaskStatus $firstTaskId '已准备*' } '第一项准备未完成'
    Assert-WindowStable '完成独立准备'
    # 以真实后续请求证明成功后仍在保活，最小合法间隔为 0.5 分钟。
    $readyRequests = @(Get-Content -LiteralPath $eventPath | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.path -ne '/v1/models' -and $_.effort -eq 'low' }).Count
    Wait-For {
        $requests = @(Get-Content -LiteralPath $eventPath | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.path -ne '/v1/models' -and $_.effort -eq 'low' })
        return $requests.Count -gt $readyRequests
    } '准备成功后未继续独立保活' 55
    Write-Host '独立准备、获取模型及后续保活通过。'

    Remove-Item -LiteralPath $gate
    $secondTaskId = Add-Preparation '7.5' '高 · high'
    Wait-For { Test-TaskStatus $secondTaskId '准备中*' } '第二项准备未进入等待'
    Invoke-Control "StopPreparation_$secondTaskId" -ById
    Wait-For { Test-TaskStatus $secondTaskId '已停止' } '第二项准备未停止'
    if (-not (Test-TaskStatus $firstTaskId '已准备*')) { throw '停止第二项打断了第一项准备' }
    New-Item -ItemType File -Path $gate | Out-Null
    Invoke-Control "StartPreparation_$secondTaskId" -ById
    Wait-For { (Test-TaskStatus $firstTaskId '已准备*') -and (Test-TaskStatus $secondTaskId '已准备*') } '第二项准备未独立重启'
    Assert-WindowStable '新增并重启第二项准备'

    # 切到 Claude 后 Codex 任务应隐藏但继续运行，新增任务默认沿用所选客户端。
    Invoke-Control 'SelectClaude' -ById
    Wait-For { @(Find-Elements '当前客户端尚未添加准备任务').Count -gt 0 } 'Preparation tasks were not filtered by client'
    Invoke-Control 'AddPreparation' -ById
    Wait-For { @(Find-Elements 'PreparationDrawer' -ById).Count -eq 1 } 'Claude 准备抽屉未打开'
    Assert-DrawerClient 'Claude Code'
    if ((Get-ComboSelection 'PreparationReasoningEffort') -ne '默认（沿用客户端）') { throw '新建 Claude 准备沿用了另一客户端的强度' }
    Select-ComboItem 'PreparationReasoningEffort' '最高 · max'
    Invoke-Control 'PreparationCustomProvider' -ById
    Set-Field 'PreparationProviderUrl' "http://127.0.0.1:$upstreamPort"
    Invoke-Control '显示密钥'
    Set-Field 'PreparationVisibleApiKey' 'sk-prepare-fixture'
    # 仅获取模型，不启动真实 Claude CLI；两次查询都不得自动选中首项。
    foreach ($fetch in 1..2) {
        Set-Field 'PreparationModel' ''
        Fetch-PreparationModels
    }
    Assert-DrawerActionsVisible
    Close-PreparationDrawer
    Invoke-Control 'ProviderNavigation' -ById
    Wait-For { @(Find-Elements 'ProxySettings' -ById).Count -gt 0 } 'Providers did not load'
    Invoke-Control 'ProxySettings' -ById
    Wait-For { @(Find-Elements 'ChannelReasoningEffort' -ById).Count -gt 0 } 'Proxy settings drawer did not open'
    Select-ComboItem 'ChannelReasoningEffort' '最高 · max'
    Invoke-Control 'DrawerSave' -ById
    Wait-For { @(Find-Elements 'ChannelReasoningEffort' -ById).Count -eq 0 } 'Proxy settings drawer did not close'
    Invoke-Control 'SelectCodex' -ById
    Invoke-Control 'ProxySettings' -ById
    Wait-For { @(Find-Elements 'ChannelReasoningEffort' -ById).Count -gt 0 } 'Codex settings did not open'
    Select-ComboItem 'ChannelReasoningEffort' '极限 · ultra'
    Invoke-Control 'DrawerSave' -ById
    Wait-For { @(Find-Elements 'ChannelReasoningEffort' -ById).Count -eq 0 } 'Proxy settings drawer did not close'
    Invoke-Control 'SelectClaude' -ById
    Invoke-Control 'ProxySettings' -ById
    Wait-For { (Get-ComboSelection 'ChannelReasoningEffort') -eq '最高 · max' } 'Claude channel effort was lost'
    Invoke-Control 'DrawerCancel' -ById
    Wait-For { @(Find-Elements 'ChannelReasoningEffort' -ById).Count -eq 0 } 'Proxy settings drawer did not close'
    Invoke-Control 'SelectCodex' -ById
    Invoke-Control 'ProxySettings' -ById
    Wait-For { (Get-ComboSelection 'ChannelReasoningEffort') -eq '极限 · ultra' } 'Codex channel effort was lost'
    Invoke-Control 'DrawerCancel' -ById
    Wait-For { @(Find-Elements 'ChannelReasoningEffort' -ById).Count -eq 0 } 'Proxy settings drawer did not close'
    Invoke-Control 'PreparationNavigation' -ById
    Wait-For { (Test-TaskStatus $firstTaskId '已准备*') -and (Test-TaskStatus $secondTaskId '已准备*') } '切换客户端改变了后台准备状态'
    Wait-For { @(Find-Elements '手动填写').Count -gt 0 } 'Manual preparation group is missing'
    Assert-WindowStable 'returning to client-filtered preparation tasks'
    Invoke-Control "StopPreparation_$firstTaskId" -ById
    Wait-For { Test-TaskStatus $firstTaskId '已停止' } '第一项准备未停止'
    if (-not (Test-TaskStatus $secondTaskId '已准备*')) { throw '停止第一项打断了第二项准备' }
    Invoke-Control "StopPreparation_$secondTaskId" -ById
    Wait-For { Test-TaskStatus $secondTaskId '已停止' } '第二项准备未停止'
    Invoke-Control "PreparationTaskMenu_$firstTaskId" -ById
    Invoke-Control '查看日志'
    Wait-For { @(Find-Elements 'LogSearch' -ById).Count -gt 0 } 'Task log action did not open Logs'
    $taskQuery = @(Find-Elements 'LogSearch' -ById)[0].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($taskQuery -ne '[准备 1 · ') { throw 'Task log action did not select the first preparation' }
    Invoke-Control 'PreparationNavigation' -ById
    foreach ($taskId in @($firstTaskId, $secondTaskId)) {
        Invoke-Control "PreparationTaskMenu_$taskId" -ById
        Invoke-Control '删除'
        Wait-For { @(Find-Elements "PreparationTaskStatus_$taskId" -ById).Count -eq 0 } '已停止的准备任务未删除'
    }
    Wait-For { @(Find-Elements '当前客户端尚未添加准备任务').Count -gt 0 } 'Stopped tasks could not be removed'
    Assert-WindowStable 'stopping and removing preparation tasks'
    Invoke-Control 'AddPreparation' -ById
    Wait-For { @(Find-Elements 'PreparationDrawer' -ById).Count -eq 1 } '准备抽屉未重新打开'
    Assert-DrawerClient 'Codex'
    Close-PreparationDrawer
    Assert-WindowStable '取消准备抽屉'
    Invoke-Control '运行日志'
    Wait-For { @(Find-Elements 'LogSourcePreparation' -ById).Count -gt 0 } 'Log source filters did not load'
    Assert-LogControlsVisible
    $logLines = @(Get-Content -LiteralPath (Join-Path $runtime 'logs\retry-proxy.log'))
    $preparationLines = @($logLines | Where-Object { $_ -match '(INFO|WARNING|ERROR) \[一键准备\]' })
    if ($preparationLines.Count -eq 0) { throw 'Preparation did not produce source-tagged logs' }
    foreach ($line in $preparationLines) {
        if ($line -notmatch '\[一键准备\]\[准备 [12] · Codex\]' -or $line.Contains('[保活-')) { throw "Preparation log lost its identity: $line" }
    }
    Set-Field 'LogSearch' ''
    Invoke-Control 'LogSourcePreparation' -ById
    Wait-For { (Get-LogCount) -eq $preparationLines.Count } 'Preparation source omitted task lifecycle logs'
    if (@(Find-Elements 'LogSelectedClient' -ById).Count -eq 0) { throw 'Current-client filter is missing for preparation logs' }
    Set-Field 'LogSearch' '准备 1'
    $firstTaskCount = @($preparationLines | Where-Object { $_.Contains('[准备 1 · Codex]') }).Count
    Wait-For { (Get-LogCount) -eq $firstTaskCount } 'Task search did not isolate the first preparation'
    Set-Field 'LogSearch' ''
    Wait-For { (Get-LogCount) -eq $preparationLines.Count } 'Clearing task search did not restore the preparation logs'
    Invoke-Control 'LogLevelWarning' -ById
    $warningCount = @($preparationLines | Where-Object { $_.Contains(' WARNING ') }).Count
    Wait-For { (Get-LogCount) -eq $warningCount } 'Severity and source filters did not combine'
    Invoke-Control 'LogLevelAll' -ById
    Invoke-Control 'LogSourceKeepAlive' -ById
    Wait-For { (Get-LogCount) -eq 0 } 'Independent preparation leaked into channel keepalive logs'
    Invoke-Control 'LogSourceAll' -ById
    Invoke-Control 'LogSourcePreparation' -ById
    Invoke-Control 'LogSelectedClient' -ById
    Invoke-Control 'SelectClaude' -ById
    Wait-For { (Get-LogCount) -eq 0 } 'Claude client filter matched Codex preparation logs'
    Invoke-Control 'SelectCodex' -ById
    Wait-For { (Get-LogCount) -eq $preparationLines.Count } 'Codex client filter lost its preparation logs'
    Invoke-Control 'LogSelectedClient' -ById
    Invoke-Control 'LogSourceAll' -ById
    Wait-For { (Get-LogCount) -eq $logLines.Count } 'All sources did not restore every log line'
    Assert-LogControlsVisible
    Assert-WindowStable 'filtering log sources, tasks and severity'
    $events = @(Get-Content -LiteralPath $eventPath | ForEach-Object { $_ | ConvertFrom-Json })
    $modelEvents = @($events | Where-Object path -eq '/v1/models')
    $preparationEvents = @($events | Where-Object path -ne '/v1/models')
    if (@($preparationEvents | Where-Object { -not $_.authOk }).Count -gt 0) { throw 'Preparation forwarded an incorrect credential' }
    # 手动准备默认 Bearer；Codex 两项各查一次，Claude 抽屉独立查询两次。
    if ($modelEvents.Count -ne 4 -or @($modelEvents | Where-Object { -not $_.authOk -or $_.apiKeyOk }).Count -ne 0) { throw 'Model lookup did not use the default Bearer authentication exactly once per fetch' }
    if (@($modelEvents | Where-Object authOk).Count -ne 4) { throw 'Model lookup did not use the selected provider' }
    if (@($preparationEvents | Where-Object effort -eq 'low').Count -eq 0 -or @($preparationEvents | Where-Object effort -eq 'high').Count -eq 0) { throw 'Selected reasoning efforts did not reach the preparation clients' }
    $log = Get-Content -LiteralPath (Join-Path $runtime 'logs\retry-proxy.log') -Raw
    if ($log.Contains('sk-prepare-fixture')) { throw 'Preparation logged its API key' }
    if (Test-Path -LiteralPath (Join-Path $runtime 'User\config.json')) { throw 'Preparation persisted temporary settings' }
    if (Test-Path -LiteralPath (Join-Path $runtime 'User\config.db')) { throw 'Preparation persisted temporary settings' }
    if ([IO.File]::ReadAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json')) -cne '{}' -or
        [IO.File]::ReadAllText((Join-Path $env:CODEX_HOME 'config.toml')) -cne '') { throw '独立准备改动了隔离客户端配置' }
    Write-Host "准备抽屉验收通过：窗口稳定、双任务独立启停、完成后保活及日志筛选。WithChannels=$($WithChannels.IsPresent), CompactWindow=$($CompactWindow.IsPresent)"
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
