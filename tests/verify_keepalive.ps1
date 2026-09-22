param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\RetryProxy.exe'),
    [switch]$Automatic,
    [switch]$Interrupt,
    [switch]$RetryPreparation,
    [switch]$CancelPreparation,
    [switch]$NoScreenshot
)
# C# 版保活验收（移植自 D:\API-Proxy\tests\verify_keepalive_exe.ps1，PowerShell 7 + UIAutomation）。
# 与 Rust 版的差异：保活开关/间隔/一键准备/终止准备都在「运行状态」页，脚本先点导航项切过去；
# 通道切换通过 ComboBox 的 ExpandCollapse + SelectionItem；准备完成/终止用 Snackbar 提示而非带“确定”的弹窗，不再点“确定”；
# 间隔文本框可访问名为“保活间隔分钟”；开关是 ToggleSwitch（TogglePattern，控件类型 Button）。

$ErrorActionPreference = 'Stop'
if (@($Automatic.IsPresent, $Interrupt.IsPresent, $RetryPreparation.IsPresent, $CancelPreparation.IsPresent).Where({ $_ }).Count -gt 1) { throw '各保活验收模式必须分别运行' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$distDir = Split-Path -Parent $ExePath
$oldConfig = $env:RETRY_PROXY_CONFIG_JSON
$runtime = Join-Path ([IO.Path]::GetTempPath()) ('retry-client-validation-' + [guid]::NewGuid().ToString('N'))
$oldCli = $env:RETRY_PROXY_CODEX_CLI
$oldCliEvents = $env:RETRY_PROXY_FAKE_CLI_EVENTS
$oldProxyUrl = $env:RETRY_PROXY_FAKE_PROXY_URL
$oldPath = $env:PATH
$job = $null
$app = $null

function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
}

function Wait-Condition([scriptblock]$Condition, [string]$Failure, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Get-Root {
    $app.Refresh()
    return [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
}

function Find-ByName([string]$Name) {
    $root = Get-Root
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Write-ControlDiagnostics {
    $root = Get-Root
    $controls = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $diagnostics = foreach ($control in $controls) {
        if ($control.Current.ControlType -eq [Windows.Automation.ControlType]::Text) { continue }
        [pscustomobject]@{
            Name = $control.Current.Name
            Type = $control.Current.ControlType.ProgrammaticName
            Value = $control.GetCurrentPropertyValue([Windows.Automation.ValuePattern]::ValueProperty)
            Patterns = @($control.GetSupportedPatterns() | ForEach-Object ProgrammaticName)
        }
    }
    $diagnosticPath = Join-Path $runtime 'window-controls.json'
    $diagnostics | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $diagnosticPath -Encoding utf8
    return $diagnosticPath
}

function Invoke-WindowButton([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        foreach ($button in (Find-ByName $Name)) {
            if (-not $button.Current.IsEnabled) { continue }
            $pattern = $null
            if ($button.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
            if ($button.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
            if ($button.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
            # 导航项：同名的是容器 DataItem / 文本，真正带 SelectionItem 的 TabItem 在其子树里。
            foreach ($child in $button.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
                if ($child.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
                if ($child.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "找不到程序按钮：$Name；控件记录：$(Write-ControlDiagnostics)"
}

function Select-Channel([string]$ItemName) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        foreach ($combo in (Find-ByName '通道选择')) {
            $expand = $null
            if (-not $combo.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { continue }
            $expand.Expand()
            Start-Sleep -Milliseconds 300
            $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $ItemName)
            $item = $combo.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -eq $item) { $item = (Get-Root).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition) }
            if ($null -ne $item) {
                $select = $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
                $select.Select()
                Start-Sleep -Milliseconds 200
                try { $expand.Collapse() } catch {}
                return
            }
            try { $expand.Collapse() } catch {}
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "通道选择里找不到条目：$ItemName；控件记录：$(Write-ControlDiagnostics)"
}

function Invoke-NavigationItem([string]$Name) {
    # WPF-UI 的导航项在 UIA 树里只暴露为无模式的 DataItem：先让它获得焦点，再向主窗口投递回车。
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        foreach ($element in (Find-ByName $Name)) {
            if ($element.Current.ControlType -ne [Windows.Automation.ControlType]::DataItem) { continue }
            $element.SetFocus()
            Start-Sleep -Milliseconds 300
            $enter = [UIntPtr]::new([uint64]13)
            [KeepaliveNative]::PostMessage($app.MainWindowHandle, 0x0100, $enter, [IntPtr]::new([int64]0x001C0001)) | Out-Null
            [KeepaliveNative]::PostMessage($app.MainWindowHandle, 0x0101, $enter, [IntPtr]::new([int64]0xC01C0001)) | Out-Null
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "找不到导航项：$Name；控件记录：$(Write-ControlDiagnostics)"
}

function Get-KeepaliveToggle {
    foreach ($element in (Find-ByName '本通道保活')) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { return $element }
    }
    return $null
}

function Assert-KeepaliveControls([bool]$Enabled, [string]$Minutes) {
    Wait-Condition {
        $toggle = Get-KeepaliveToggle
        if ($null -eq $toggle) { return $false }
        $pattern = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if (($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $Enabled) { return $false }
        foreach ($box in (Find-ByName '保活间隔分钟')) {
            $value = $null
            if ($box.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$value) -and $value.Current.Value -eq $Minutes) { return $true }
        }
        return $false
    } "通道保活设置错误：开关应为 $Enabled，间隔应为 $Minutes 分钟"
}

function Toggle-Keepalive {
    $toggle = Get-KeepaliveToggle
    if ($null -eq $toggle) { throw "找不到本通道保活开关；控件记录：$(Write-ControlDiagnostics)" }
    $pattern = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $pattern.Toggle()
}

function Verify-IndependentChannelControls {
    Assert-KeepaliveControls $Automatic.IsPresent '0.5'
    Toggle-Keepalive
    Assert-KeepaliveControls (-not $Automatic.IsPresent) '0.5'
    Select-Channel "Claude Code 校验通道 · $otherProxyPort · 已停止"
    Assert-KeepaliveControls $false '9'
    Toggle-Keepalive
    Assert-KeepaliveControls $true '9'
    Select-Channel "客户端校验通道 · $proxyPort · 运行中"
    Assert-KeepaliveControls (-not $Automatic.IsPresent) '0.5'
    Toggle-Keepalive
    Assert-KeepaliveControls $Automatic.IsPresent '0.5'
    Select-Channel "Claude Code 校验通道 · $otherProxyPort · 已停止"
    Assert-KeepaliveControls $true '9'
    Toggle-Keepalive
    Assert-KeepaliveControls $false '9'
    Select-Channel "客户端校验通道 · $proxyPort · 运行中"
    Assert-KeepaliveControls $Automatic.IsPresent '0.5'
}

New-Item -ItemType Directory -Path $runtime | Out-Null
$upstreamPort = Get-FreePort
$proxyPort = Get-FreePort
$otherProxyPort = Get-FreePort
while ($otherProxyPort -eq $proxyPort) { $otherProxyPort = Get-FreePort }
$logPath = Join-Path $runtime 'logs\retry-proxy.log'
$eventPath = Join-Path $runtime 'upstream-events.jsonl'
$cliEventPath = Join-Path $runtime 'cli-events.jsonl'
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class KeepaliveNative {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint message, UIntPtr value, IntPtr detail);
}
'@
    Copy-Item -LiteralPath $ExePath -Destination (Join-Path $runtime 'RetryProxy.exe')
    if (Test-Path -LiteralPath (Join-Path $distDir 'User')) {
        Copy-Item -LiteralPath (Join-Path $distDir 'User') -Destination (Join-Path $runtime 'User') -Recurse
    }
    $fakeCliPath = Join-Path $runtime 'codex.ps1'
    if ($Interrupt -or $CancelPreparation) { New-Item -ItemType File -Path (Join-Path $runtime 'hold-second') | Out-Null }
    if ($RetryPreparation) { Set-Content -LiteralPath (Join-Path $runtime 'bootstrap-failures') -Value '2' -Encoding ascii }
    @'
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$ErrorActionPreference = 'Stop'
$initialized = $false
$turnNumber = 0
$toolConfig = @{
    node_repl = @{ command = 'fixture-tool'; env = @{ API_KEY = 'fixture-private' } }
    'fixture.tools' = @{ url = 'http://127.0.0.1/unused'; enabled = $true }
}
$line = [Console]::In.ReadLine()
if ($env:RETRY_PROXY_FAKE_CLI_EVENTS) { Add-Content -LiteralPath $env:RETRY_PROXY_FAKE_CLI_EVENTS -Value 'START' }
while ($null -ne $line) {
    if ($env:RETRY_PROXY_FAKE_CLI_EVENTS) { Add-Content -LiteralPath $env:RETRY_PROXY_FAKE_CLI_EVENTS -Value ("IN " + $line) }
    $message = $line | ConvertFrom-Json
    $id = $message.id
    switch ($message.method) {
        'initialize' { [Console]::WriteLine((@{ id = $id; result = @{} } | ConvertTo-Json -Depth 10 -Compress)) }
        'initialized' { $initialized = -not ($message.PSObject.Properties.Name -contains 'params') }
        'config/read' {
            $failurePath = Join-Path (Split-Path $env:RETRY_PROXY_FAKE_CLI_EVENTS) 'bootstrap-failures'
            if (Test-Path -LiteralPath $failurePath) {
                $remaining = [int](Get-Content -LiteralPath $failurePath -Raw)
                if ($remaining -gt 0) {
                    Set-Content -LiteralPath $failurePath -Value ($remaining - 1) -Encoding ascii
                    [Console]::WriteLine((@{ id = $id; error = @{ code = -32603; message = 'service temporarily unavailable' } } | ConvertTo-Json -Depth 10 -Compress))
                    break
                }
            }
            if (-not $initialized) {
                [Console]::WriteLine((@{ id = $id; error = @{ code = -32600; message = 'initialized notification must not contain params' } } | ConvertTo-Json -Depth 10 -Compress))
            } else {
                [Console]::WriteLine((@{ id = $id; result = @{ config = @{ mcp_servers = $toolConfig } } } | ConvertTo-Json -Depth 10 -Compress))
            }
        }
        'thread/start' {
            $overrides = $message.params.config
            $valid = @($overrides.PSObject.Properties).Count -eq 1 -and @($overrides.PSObject.Properties.Name)[0] -eq 'mcp_servers'
            $valid = $valid -and @($overrides.mcp_servers.PSObject.Properties).Count -eq $toolConfig.Count
            foreach ($name in $toolConfig.Keys) {
                $server = $overrides.mcp_servers.$name
                $valid = $valid -and $server.enabled -eq $false -and @($server.PSObject.Properties).Count -eq 1
            }
            if ($valid) {
                [Console]::WriteLine((@{ id = $id; result = @{ thread = @{ id = 'fake-thread' }; model = 'fake-cli-model' } } | ConvertTo-Json -Depth 10 -Compress))
            } else {
                [Console]::WriteLine((@{ id = $id; error = @{ code = -32600; message = 'failed to load bootstrap configuration: invalid transport in mcp_servers' } } | ConvertTo-Json -Depth 10 -Compress))
            }
        }
        'turn/start' {
            $turnNumber++
            Add-Content -LiteralPath $env:RETRY_PROXY_FAKE_CLI_EVENTS -Value (@{ question = $message.params.input[0].text } | ConvertTo-Json -Compress)
            $marker = $message.params.responsesapiClientMetadata.retry_proxy_keepalive
            if (-not $marker) { throw 'Missing keepalive marker' }
            $metadata = @{ retry_proxy_keepalive = $marker; thread_id = 'fake-thread' } | ConvertTo-Json -Compress
            $body = @{
                model = 'fake-cli-model'; store = $false; stream = $true
                instructions = 'fixture background instructions'; tools = @()
                input = @(@{ role = 'user'; content = @(@{ type = 'input_text'; text = $message.params.input[0].text }) })
                client_metadata = @{ 'x-codex-turn-metadata' = $metadata; fixture_field = 'preserved' }
            } | ConvertTo-Json -Depth 10 -Compress
            $response = Invoke-WebRequest -UseBasicParsing $env:RETRY_PROXY_FAKE_PROXY_URL -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{ 'User-Agent' = 'codex-cli-validation/1'; originator = 'codex_cli_rs'; Authorization = 'Bearer local-validation-token' } -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 8
            if (-not $response.Content.Contains('response.completed')) { throw 'Incomplete local response' }
            [Console]::WriteLine((@{ id = $id; result = @{ turn = @{ id = "fake-turn-$turnNumber"; status = 'inProgress'; items = @() } } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::WriteLine((@{ method = 'item/agentMessage/delta'; params = @{ threadId = 'fake-thread'; delta = 'Java' } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::Out.Flush()
            if ($turnNumber -eq 2 -and (Test-Path -LiteralPath (Join-Path (Split-Path $env:RETRY_PROXY_FAKE_CLI_EVENTS) 'hold-second'))) {
                Add-Content -LiteralPath $env:RETRY_PROXY_FAKE_CLI_EVENTS -Value 'HELD'
                while ($true) { Start-Sleep -Milliseconds 100 }
            }
            [Console]::WriteLine((@{ method = 'thread/tokenUsage/updated'; params = @{ threadId = 'fake-thread'; tokenUsage = @{ last = @{ inputTokens = 40; outputTokens = 12; cachedInputTokens = 0; reasoningOutputTokens = 0 }; total = @{ inputTokens = 40; outputTokens = 12; cachedInputTokens = 0; reasoningOutputTokens = 0 } } } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::WriteLine((@{ method = 'turn/completed'; params = @{ threadId = 'fake-thread'; turn = @{ status = 'completed'; items = @(@{ type = 'agentMessage'; text = 'Java CLI 验证回答' }) } } } | ConvertTo-Json -Depth 10 -Compress))
        }
    }
    [Console]::Out.Flush()
    $line = [Console]::In.ReadLine()
}
'@ | Set-Content -LiteralPath $fakeCliPath -Encoding UTF8
    $env:RETRY_PROXY_CODEX_CLI = $fakeCliPath
    $env:RETRY_PROXY_FAKE_CLI_EVENTS = $cliEventPath
    $env:RETRY_PROXY_FAKE_PROXY_URL = "http://127.0.0.1:$proxyPort/v1/responses?beta=a%20b"
    $env:PATH = "$runtime;$oldPath"
    $job = Start-Job -ArgumentList $upstreamPort, $runtime -ScriptBlock {
        param($Port, $Directory)
        $ErrorActionPreference = 'Stop'
        $listener = [Net.HttpListener]::new()
        $listener.Prefixes.Add("http://127.0.0.1:$Port/")
        $listener.Start()
        New-Item -ItemType File -Path (Join-Path $Directory 'upstream.ready') | Out-Null
        try {
            while ($true) {
                $pending = $listener.GetContextAsync()
                while (-not $pending.IsCompleted) {
                    if (Test-Path -LiteralPath (Join-Path $Directory 'upstream.stop')) { return }
                    Start-Sleep -Milliseconds 50
                }
                $context = $pending.GetAwaiter().GetResult()
                $reader = [IO.StreamReader]::new($context.Request.InputStream, [Text.Encoding]::UTF8)
                $raw = $reader.ReadToEnd()
                $reader.Dispose()
                $body = $raw | ConvertFrom-Json -AsHashtable
                $issues = [Collections.Generic.List[string]]::new()
                $background = $body.model -eq 'fake-cli-model'
                $expectedModel = if ($background) { 'fake-cli-model' } else { 'client-format-model' }
                $expectedInstructions = if ($background) { 'fixture background instructions' } else { 'required client system instructions' }
                $expectedClient = if ($background) { 'codex-cli-validation/1' } else { 'client-validation/1' }
                $expectedOriginator = if ($background) { 'codex_cli_rs' } else { 'cli-client' }
                if ($context.Request.RawUrl -ne '/v1/responses?beta=a%20b') { $issues.Add('endpoint') }
                foreach ($pair in @(@('User-Agent', $expectedClient), @('originator', $expectedOriginator), @('Authorization', 'Bearer local-validation-token'))) {
                    if ($context.Request.Headers[$pair[0]] -ne $pair[1]) { $issues.Add($pair[0]) }
                }
                if ($context.Request.Headers['x-retry-keepalive'] -or $context.Request.Headers['x-retry-preparation-id']) { $issues.Add('internal marker') }
                if ($body.model -ne $expectedModel -or $body.store -ne $false -or $body.stream -ne $true -or $body.instructions -ne $expectedInstructions) { $issues.Add('client fields') }
                if ($body.ContainsKey('max_output_tokens') -or $body.ContainsKey('reasoning')) { $issues.Add('injected fields') }
                if ($background) {
                    if ($body.tools.Count -ne 0) { $issues.Add('background tools') }
                    $metadata = $body.client_metadata['x-codex-turn-metadata'] | ConvertFrom-Json -AsHashtable
                    if ($metadata.ContainsKey('retry_proxy_keepalive')) { $issues.Add('body internal marker') }
                    if ($metadata.thread_id -ne 'fake-thread' -or $body.client_metadata.fixture_field -ne 'preserved') { $issues.Add('preserved metadata') }
                } elseif ($body.tools[0].name -ne 'client_tool') { $issues.Add('tools') }
                $last = $body.input[-1]
                if ($last.content[0].type -ne 'input_text') { $issues.Add('message shape') }
                if ($background -and $raw.Contains('正常客户端验证消息')) { $issues.Add('private conversation') }
                $rejectPath = Join-Path $Directory 'force-error.once'
                $reject = Test-Path -LiteralPath $rejectPath
                if ($reject) { Remove-Item -LiteralPath $rejectPath }
                $status = if ($issues.Count -gt 0 -or $reject) { 400 } else { 200 }
                $record = @{ background = $background; status = $status; issues = $issues.ToArray(); body = $body; client = $context.Request.Headers['User-Agent'] }
                [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'), ($record | ConvertTo-Json -Depth 30 -Compress) + "`n", [Text.UTF8Encoding]::new($false))
                if ($status -eq 400) {
                    $message = if ($reject) { 'client format rejected for verification' } else { $issues -join ', ' }
                    $payload = @{ error = @{ message = $message } } | ConvertTo-Json -Compress
                    $context.Response.StatusCode = 400
                    $context.Response.ContentType = 'application/json'
                } else {
                    $complete = @{ type = 'response.completed'; response = @{ model = 'client-format-model'; status = 'completed'; usage = @{ input_tokens = 40; output_tokens = 12 }; output = @(@{ type = 'message'; content = @(@{ type = 'output_text'; text = '这是完整的 Java 验证回答。' }) }) } } | ConvertTo-Json -Depth 10 -Compress
                    $payload = "data: $complete`n`n"
                    $context.Response.ContentType = 'text/event-stream'
                    Start-Sleep -Milliseconds 160
                }
                $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Stop(); $listener.Close() }
    }
    Wait-Condition { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } '模拟上游未启动'
    $config = @{
        schema_version = 6; upstream_base_url = "http://127.0.0.1:$upstreamPort"; listen_port = $proxyPort
        max_retries = 0; timeout_seconds = 10.0; base_delay_seconds = 0.0; max_delay_seconds = 0.0
        desired_running = $true; selected_route_id = 'client-test'
        providers = @(@{ name = '客户端格式验证'; base_url = "http://127.0.0.1:$upstreamPort" })
        routes = @(
            @{ id = 'client-test'; name = '客户端校验通道'; provider_name = '客户端格式验证'; client_type = 'codex'; listen_port = $proxyPort; max_retries = 0; timeout_seconds = 10.0; base_delay_seconds = 0.0; max_delay_seconds = 0.0; desired_running = $true; keepalive_enabled = $Automatic.IsPresent; keepalive_idle_minutes = 0.5 },
            @{ id = 'claude-test'; name = 'Claude Code 校验通道'; provider_name = '客户端格式验证'; client_type = 'claude'; listen_port = $otherProxyPort; desired_running = $false; keepalive_enabled = $false; keepalive_idle_minutes = 9.0 }
        )
    } | ConvertTo-Json -Depth 8
    $env:RETRY_PROXY_CONFIG_JSON = $config
    $app = Start-Process -FilePath (Join-Path $runtime 'RetryProxy.exe') -WorkingDirectory $runtime -PassThru
    Wait-Condition {
        try { $null = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 1; return $true }
        catch { return $false }
    } '程序监听未启动' 30
    Wait-Condition { $app.Refresh(); $app.MainWindowHandle -ne 0 -and $app.MainWindowTitle -eq 'LLM Retry Proxy' } '程序窗口未创建'
    Invoke-NavigationItem '运行状态'
    Wait-Condition { $null -ne (Get-KeepaliveToggle) } '运行状态页未显示保活控件'
    Verify-IndependentChannelControls
    Write-Host '通道独立设置检查通过。'
    $request = @{ model = 'client-format-model'; stream = $true; store = $false; instructions = 'required client system instructions'; tools = @(@{ type = 'function'; name = 'client_tool'; parameters = @{ type = 'object' } }); input = @(@{ role = 'user'; content = @(@{ type = 'input_text'; text = '正常客户端验证消息' }) }) } | ConvertTo-Json -Depth 10
    $null = Invoke-WebRequest "http://127.0.0.1:$proxyPort/v1/responses?beta=a%20b" -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{ 'User-Agent' = 'client-validation/1'; originator = 'cli-client'; Authorization = 'Bearer local-validation-token' } -Body ([Text.Encoding]::UTF8.GetBytes($request)) -TimeoutSec 15
    Wait-Condition { (Get-Content -LiteralPath $logPath -Raw) -match '输入 40 / 输出 12 token' } '正常客户端请求未完成'
    if ($Automatic) {
        Wait-Condition { (Get-Content -LiteralPath $logPath -Raw) -match '供应商保活.*回答：' } '空闲 30 秒后未执行自动保活' 45
    } else {
        for ($round = 1; $round -le 2; $round++) {
            Invoke-WindowButton '一键准备'
            if (($Interrupt -or $CancelPreparation) -and $round -eq 2) {
                Wait-Condition { (Get-Content -LiteralPath $cliEventPath -Raw) -match 'HELD' } '第二轮保活没有进入等待状态'
            }
            if ($CancelPreparation -and $round -eq 2) {
                Invoke-WindowButton '终止准备'
                Wait-Condition { (Get-Content -LiteralPath $logPath -Raw) -match '准备已终止' } '主动终止准备没有生效'
                Start-Sleep -Seconds 3
                continue
            }
            if ($Interrupt -and $round -eq 2) {
                $null = Invoke-WebRequest "http://127.0.0.1:$proxyPort/v1/responses?beta=a%20b" -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{ 'User-Agent' = 'client-validation/1'; originator = 'cli-client'; Authorization = 'Bearer local-validation-token' } -Body ([Text.Encoding]::UTF8.GetBytes($request)) -TimeoutSec 15
                Wait-Condition { (Get-Content -LiteralPath $logPath -Raw) -match '本轮已中断：已让行真实请求' } '新请求没有正常中断保活'
            }
            Wait-Condition { @((Get-Content -LiteralPath $logPath) | Where-Object { $_ -match '准备完成' }).Count -eq $round } '一键准备未完成'
        }
    }
    $events = @(Get-Content -LiteralPath $eventPath | ForEach-Object { $_ | ConvertFrom-Json })
    $cliEvents = @(Get-Content -LiteralPath $cliEventPath | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json })
    $expectedRealCount = if ($Interrupt) { 2 } else { 1 }
    $expectedCliCount = if ($Automatic) { 1 } elseif ($Interrupt) { 3 } else { 2 }
    $expectedCount = $expectedRealCount + $expectedCliCount
    if ($events.Count -ne $expectedCount -or @($events | Where-Object { $_.issues.Count -gt 0 }).Count -ne 0) { throw "客户端请求格式校验失败：$($events.Count)/$expectedCount，issues=$(($events | ForEach-Object { $_.issues -join '+' }) -join ';')" }
    if ($cliEvents.Count -ne $expectedCliCount) { throw "CLI 后台消息数量错误：$($cliEvents.Count)" }
    $health = Invoke-RestMethod "http://127.0.0.1:$proxyPort/_retry/health" -TimeoutSec 2
    if (@($events | Where-Object background).Count -ne $expectedCliCount) { throw 'CLI 保活没有完整经过本地代理' }
    if ($health.metrics.total_requests -ne $expectedRealCount) { throw '保活被计入真实请求统计' }
    $expectedCompleted = if ($Automatic -or $CancelPreparation) { 1 } else { 2 }
    $expectedInterrupted = if ($Interrupt -or $CancelPreparation) { 1 } else { 0 }
    $expectedFailed = if ($RetryPreparation) { 2 } else { 0 }
    $expectedStatistics = "成功 $expectedCompleted 轮 · 失败 $expectedFailed 轮 · 中断 $expectedInterrupted 轮"
    if ($RetryPreparation -and @((Get-Content -LiteralPath $cliEventPath) | Where-Object { $_ -eq 'START' }).Count -ne 3) { throw '一键准备没有在前两次失败后重新启动客户端' }
    Wait-Condition {
        $root = Get-Root
        $elements = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            $text = $element.Current.Name
            if ($text.Contains($expectedStatistics) -and $text.Contains('最近成功用量 52 token')) { return $true }
        }
        return $false
    } '窗口没有保留正确的累计轮次和最近成功用量'
    $logs = Get-Content -LiteralPath $logPath -Raw
    foreach ($value in @('随机题号', '首字', '耗时', '回答：', '当前会话 52/50000 token')) {
        if (-not $logs.Contains($value)) { throw "日志缺少：$value" }
    }
    $screenshotPath = $null
    if (-not $NoScreenshot) {
        Add-Type -AssemblyName System.Drawing
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ValidationWindow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
}
'@
        $null = [ValidationWindow]::ShowWindow($app.MainWindowHandle, 9)
        $null = [ValidationWindow]::SetForegroundWindow($app.MainWindowHandle)
        Start-Sleep -Milliseconds 500
        $root = Get-Root
        $bounds = $root.Current.BoundingRectangle
        $image = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height)
        $graphics = [Drawing.Graphics]::FromImage($image)
        try {
            $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $image.Size)
            $screenshotPath = Join-Path $runtime 'window.png'
            $image.Save($screenshotPath, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $image.Dispose() }
    }
    [pscustomobject]@{ IndependentChannels = $true; Requests = $events.Count; CliMessages = $cliEvents.Count; Automatic = $Automatic.IsPresent; Interrupted = $Interrupt.IsPresent; Retried = $RetryPreparation.IsPresent; Cancelled = $CancelPreparation.IsPresent; Statistics = $expectedStatistics; RealRequests = $health.metrics.total_requests; LogPath = $logPath; EventsPath = $eventPath; CliEventsPath = $cliEventPath; Screenshot = $screenshotPath }
} catch {
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Encoding UTF8 -Tail 30 | Write-Host }
    throw
} finally {
    if ($null -ne $oldConfig) { $env:RETRY_PROXY_CONFIG_JSON = $oldConfig }
    else { Remove-Item Env:RETRY_PROXY_CONFIG_JSON -ErrorAction SilentlyContinue }
    if ($null -ne $oldCli) { $env:RETRY_PROXY_CODEX_CLI = $oldCli }
    else { Remove-Item Env:RETRY_PROXY_CODEX_CLI -ErrorAction SilentlyContinue }
    $env:PATH = $oldPath
    if ($null -ne $oldCliEvents) { $env:RETRY_PROXY_FAKE_CLI_EVENTS = $oldCliEvents }
    else { Remove-Item Env:RETRY_PROXY_FAKE_CLI_EVENTS -ErrorAction SilentlyContinue }
    if ($null -ne $oldProxyUrl) { $env:RETRY_PROXY_FAKE_PROXY_URL = $oldProxyUrl }
    else { Remove-Item Env:RETRY_PROXY_FAKE_PROXY_URL -ErrorAction SilentlyContinue }
    if ($null -ne $app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($null -ne $job) {
        New-Item -ItemType File -Path (Join-Path $runtime 'upstream.stop') -Force | Out-Null
        $null = Wait-Job $job -Timeout 3
        Stop-Job $job -ErrorAction SilentlyContinue
        Receive-Job $job -ErrorAction Continue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
    }
}
