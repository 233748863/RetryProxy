#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$ConfigPath = '',
    [string]$OutputDirectory = ''
)
# SQLite 配置实机验收：真实配置仅复制为隔离种子，不访问真实客户端或供应商。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime_files.ps1')
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ConfigStorageWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$root = Split-Path -Parent $PSScriptRoot
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('.tmp/config-storage-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw '验收输出目录必须为新目录，保留以前的结果' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$variables = @('RETRY_PROXY_CONFIG_JSON','RETRY_PROXY_UI_TEST_ROOT','CLAUDE_CONFIG_DIR','CODEX_HOME','RETRY_PROXY_CLAUDE_CLI','RETRY_PROXY_CODEX_CLI','UPSTREAM_BASE_URL','RETRY_PROXY_PORT')
$variables += @(Get-ChildItem Env: | Where-Object Name -like 'RETRY_*' | ForEach-Object Name)
$variables = @($variables | Sort-Object -Unique)
$saved = @{}; foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$record = [ordered]@{passed=$false;executable=$ExePath;checks=@();runtimes=@();runs=@();captures=@();visualReview='待检查目录卡截图'}
$app = $null; $window = [IntPtr]::Zero; $runtime = ''; $sourceHash = $null
function Check([string]$Text) { $record.checks += $Text; Write-Host "PASS: $Text" }
function Probe([string]$Action,[string]$Database,[string]$Source='') {
    $arguments = @('-X','utf8',(Join-Path $PSScriptRoot 'config_storage_probe.py'),$Action,$Database)
    if ($Source) { $arguments += $Source }
    $output = @(& python @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "配置库断言失败：$($output -join ' ')" }
    return ($output -join "`n")
}
function Read-SharedLog([string]$Path) {
    # 应用保留日志写句柄；读取端必须同时允许写入和轮转，不能使用默认独占共享方式。
    try {
        $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=[IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } catch [IO.IOException] { return '' }
}
function Wait-For([scriptblock]$Condition,[string]$Failure,[int]$Seconds=30) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while (!(& $Condition)) {
        if ($app -and $app.HasExited) { throw "测试程序提前退出：$Failure" }
        if ($clock.Elapsed.TotalSeconds -gt $Seconds) { throw $Failure }
        Start-Sleep -Milliseconds 100
    }
}
function New-Runtime([string]$Name) {
    $directory=Join-Path ([IO.Path]::GetTempPath()) ('RetryProxyM4-config-' + $Name + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory,(Join-Path $directory 'User'),(Join-Path $directory 'claude'),(Join-Path $directory 'codex') | Out-Null
    Copy-RetryProxyRuntime -ExePath $ExePath -DestinationDirectory $directory
    Copy-Item -LiteralPath (Join-Path $root 'src/RetryProxy.App/User/I18n') -Destination (Join-Path $directory 'User/I18n') -Recurse
    [IO.File]::WriteAllText((Join-Path $directory 'claude/settings.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $directory 'codex/config.toml'),'')
    $record.runtimes += $directory
    return $directory
}
function Start-App([string]$Directory,[string]$Injection='') {
    $script:runtime=$Directory; $script:window=[IntPtr]::Zero
    # 此处静态 SetEnvironmentVariable 的 $null 会变为空串，必须真正删除变量。
    foreach ($name in $variables) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
    $env:RETRY_PROXY_UI_TEST_ROOT=$Directory
    $env:CLAUDE_CONFIG_DIR=Join-Path $Directory 'claude'; $env:CODEX_HOME=Join-Path $Directory 'codex'
    $env:RETRY_PROXY_CLAUDE_CLI=Join-Path $Directory 'missing-claude.exe'; $env:RETRY_PROXY_CODEX_CLI=Join-Path $Directory 'missing-codex.exe'
    if ($Injection) { $env:RETRY_PROXY_CONFIG_JSON=$Injection }
    $script:app=Start-Process -FilePath (Join-Path $Directory 'RetryProxy.exe') -WorkingDirectory $Directory -PassThru
    $null=$app.Handle
    $record.runs += @{runtime=$Directory;pid=$app.Id;startedAt=[DateTimeOffset]::Now.ToString('o')}
}
function Wait-Window {
    Wait-For { $script:window=[RetryProxyTrayVerification]::FindWindow($app.Id,'LLM Retry Proxy',$null); $window -ne [IntPtr]::Zero } '主窗口未出现'
    Wait-For { $null -ne (Find-Control 'SelectCodex') } '主页面未加载'
}
function Stop-App([switch]$Force) {
    if ($app -and !$app.HasExited) {
        if ($Force) { $app.Kill(); $null=$app.WaitForExit(10000) }
        else {
            $null=[RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero)
            if (!$app.WaitForExit(25000)) { throw '测试程序未正常退出' }
            if ($app.ExitCode -ne 0) { throw "测试程序退出码异常：$($app.ExitCode)" }
        }
    }
    $script:app=$null; $script:window=[IntPtr]::Zero
}
function Find-Control([string]$Value,[switch]$ByName) {
    $property=if ($ByName) { [Windows.Automation.AutomationElement]::NameProperty } else { [Windows.Automation.AutomationElement]::AutomationIdProperty }
    return [Windows.Automation.AutomationElement]::FromHandle($window).FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new($property,$Value))
}
function Navigate-Settings {
    $null=[ConfigStorageWindow]::SetForegroundWindow($window)
    Wait-For { [ConfigStorageWindow]::GetForegroundWindow() -eq $window } '隔离窗口未取得焦点'
    $item=Find-Control '软件设置' -ByName
    if (!$item) { throw '软件设置导航不存在' }
    $item.SetFocus()
    $null=[RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]::new(13),[IntPtr]0x001C0001)
    $null=[RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]::new(13),[IntPtr]0xC01C0001)
    Wait-For { $null -ne (Find-Control 'LogsAndDataCard') } '日志与数据卡不存在'
}
function Show-DataCard {
    $card=Find-Control 'LogsAndDataCard'; $button=Find-Control 'OpenLogDataDirectory'
    $button.SetFocus()
    # 实机检查确认：内部 ScrollViewer 仅在 RawView 暴露，ControlView 会跳过它。
    $parent=[Windows.Automation.TreeWalker]::RawViewWalker.GetParent($card)
    $scroll=$null
    while ($parent) {
        $candidate=$null
        if ($parent.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$candidate) -and $candidate.Current.VerticallyScrollable) {
            $scroll=$candidate; break
        }
        $parent=[Windows.Automation.TreeWalker]::RawViewWalker.GetParent($parent)
    }
    # 只看到按钮不代表整张卡可见；标题、说明和完整路径都必须在截图内。
    for ($i=0;$i -lt 30;$i++) {
        $bounds=[RetryProxyTrayVerification]::Bounds($window)
        $box=$card.Current.BoundingRectangle
        if (!$button.Current.IsOffscreen -and $box.Top -ge $bounds.Top+52 -and $box.Bottom -le $bounds.Bottom-16) { return }
        if (!$scroll) { throw '找不到设置页滚动容器' }
        $amount=if ($box.Bottom -gt $bounds.Bottom-16) { [Windows.Automation.ScrollAmount]::SmallIncrement } else { [Windows.Automation.ScrollAmount]::SmallDecrement }
        $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount,$amount)
        Start-Sleep -Milliseconds 100
    }
    throw '日志与数据卡没有完整进入可见区域'
}
function Capture([string]$Name) {
    $null=[ConfigStorageWindow]::SetForegroundWindow($window)
    Wait-For { [ConfigStorageWindow]::GetForegroundWindow() -eq $window } '截图时隔离窗口不在前台'
    Start-Sleep -Milliseconds 400
    $bounds=[RetryProxyTrayVerification]::Bounds($window)
    $image=[Drawing.Bitmap]::new($bounds.Right-$bounds.Left,$bounds.Bottom-$bounds.Top)
    $graphics=[Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$image.Size)
        $file=Join-Path $OutputDirectory ($Name+'.png'); $image.Save($file); $record.captures += $file
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Get-FreePort {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}
function New-Seed {
    $providers=@(); $routes=@()
    foreach ($kind in @('claude','codex')) {
        $providers += @{id="$kind-provider";client_type=$kind;name="$kind fixture";base_url='https://example.invalid';keys=@(@{id="$kind-key";name='fixture';api_key='sk-config-fixture-only'})}
        $routes += @{id="$kind-route";client_type=$kind;name=$(if ($kind -eq 'claude') { 'Claude Code' } else { 'Codex' });listen_port=(Get-FreePort);current_provider_id="$kind-provider";current_key_id="$kind-key";local_token='00112233445566778899aabbccddeeff';desired_running=$false;keepalive_enabled=$false}
    }
    return @{proxy=@{schema_version=7;selected_route_id='claude-route';providers=$providers;routes=$routes};commonConfig=@{isFirstRun=$false;clientSetupCompleted=$true;exitToTray=$false;startMinimized=$false;currentThemeType=0};otherConfig=@{uiCultureInfoName='zh-Hans'};preparations=@()}
}
try {
    if ($ConfigPath) {
        $ConfigPath=(Resolve-Path -LiteralPath $ConfigPath).ProviderPath
        $sourceHash=(Get-FileHash -LiteralPath $ConfigPath).Hash
        $text=[IO.File]::ReadAllText($ConfigPath)
        [IO.File]::WriteAllText((Join-Path $OutputDirectory 'original-config.json'),$text)
        $seed=$text | ConvertFrom-Json -AsHashtable -Depth 30
    } else { $seed=New-Seed }
    # 后台保活与准备关闭；普通通道按既有规则自启，但仅监听随机本机端口，不发送上游请求。
    # 接管状态含原客户端和原始备份绝对路径，测试副本必须移除；正式配置原样保留。
    $null=$seed.proxy.Remove('client_takeover')
    foreach ($route in $seed.proxy.routes) { $route.listen_port=Get-FreePort; $route.desired_running=$false; $route.keepalive_enabled=$false }
    foreach ($task in $seed.preparations) { $task.wasRunning=$false; $task.dailyDate=[DateTime]::Today.ToString('yyyy-MM-dd') }
    $seed.commonConfig.exitToTray=$false; $seed.commonConfig.startMinimized=$false
    $seed.commonConfig.isFirstRun=$false; $seed.commonConfig.clientSetupCompleted=$true
    $seed.otherConfig.uiCultureInfoName='zh-Hans'
    $source=Join-Path $OutputDirectory 'import-source.json'
    [IO.File]::WriteAllText($source,($seed | ConvertTo-Json -Depth 30))
    $importRuntime=New-Runtime 'import'; $db=Join-Path $importRuntime 'User/config.db'
    Copy-Item -LiteralPath $source -Destination (Join-Path $importRuntime 'User/config.json')
    # 旧统计与诊断文件作保留哨兵：新实例不得读取、修改或清理它们。
    $legacy=@()
    foreach ($directory in @('daily-statistics/old-route','request-diagnostics')) {
        $folder=Join-Path $importRuntime ('logs/'+$directory); New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $file=Join-Path $folder '2020-01-01.jsonl'; [IO.File]::WriteAllText($file,'legacy-invalid-json-kept-verbatim')
        $legacy += @{path=$file;hash=(Get-FileHash -LiteralPath $file).Hash;ticks=(Get-Item -LiteralPath $file).LastWriteTimeUtc.Ticks}
    }
    Start-App $importRuntime; Wait-Window
    Wait-For { Test-Path -LiteralPath (Join-Path $importRuntime 'User/config.json.migrated.bak') } '旧配置未完成导入和改名'
    $null=Probe 'verify-import' $db $source
    if ($ConfigPath) { $null=Probe 'verify-identity' $db (Join-Path $OutputDirectory 'original-config.json') }
    Check '旧配置四节点完整导入，导入前备份和原文件改名完成'
    Navigate-Settings; Show-DataCard
    $path=(Find-Control 'LogDataDirectoryPath').Current.Name
    if ([IO.Path]::GetFullPath($path) -ne [IO.Path]::GetFullPath((Join-Path $importRuntime 'logs'))) { throw '目录卡指向了错误的运行目录' }
    if (Find-Control '每日统计目录' -ByName) { throw '旧每日统计目录卡仍存在' }
    if (!(Find-Control 'OpenLogDataDirectory').Current.IsEnabled) { throw '目录按钮不可用' }
    if (!(Test-Path -LiteralPath (Join-Path $importRuntime 'logs/data.db'))) { throw '统计和诊断数据库未落在隔离日志目录' }
    Capture 'logs-and-data-initial'
    $initialTheme=(Probe 'read' $db | ConvertFrom-Json).commonConfig.currentThemeType
    foreach ($desired in @(3,0)) {
        for ($i=0;$i -lt 6;$i++) {
            $theme=(Probe 'read' $db | ConvertFrom-Json).commonConfig.currentThemeType
            if ($theme -eq $desired) { break }
            (Find-Control 'SwitchAppearance').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Wait-For { (Probe 'read' $db | ConvertFrom-Json).commonConfig.currentThemeType -ne $theme } '外观按钮未保存到配置库'
        }
        if ((Probe 'read' $db | ConvertFrom-Json).commonConfig.currentThemeType -ne $desired) { throw '无法切换到目标主题' }
        Show-DataCard; Capture "logs-and-data-theme-$desired"
    }
    [RetryProxyTrayVerification]::SetBounds($window,80,80,760,600)
    Show-DataCard; Capture 'logs-and-data-760x600'
    for ($i=0;$i -lt 6;$i++) {
        $theme=(Probe 'read' $db | ConvertFrom-Json).commonConfig.currentThemeType
        if ($theme -eq $initialTheme) { break }
        (Find-Control 'SwitchAppearance').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-For { (Probe 'read' $db | ConvertFrom-Json).commonConfig.currentThemeType -ne $theme } '恢复初始主题失败'
    }
    Check '日志与数据卡目录正确，旧卡已移除，深浅主题和窄窗已截图'
    Stop-App
    Start-App $importRuntime; Wait-Window
    $null=Probe 'verify-import' $db $source
    Check '正常退出和重启继续读库，配置不重复导入'
    Stop-App
    foreach ($file in $legacy) {
        if ((Get-FileHash -LiteralPath $file.path).Hash -ne $file.hash -or (Get-Item -LiteralPath $file.path).LastWriteTimeUtc.Ticks -ne $file.ticks) { throw '旧统计或诊断文件被修改' }
    }
    Check '旧统计与诊断文件内容和时间戳保持不变'
    $injectRuntime=New-Runtime 'inject'
    Start-App $injectRuntime ($seed.proxy | ConvertTo-Json -Depth 30 -Compress); Wait-Window
    Navigate-Settings
    (Find-Control 'SwitchAppearance').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    Stop-App
    foreach ($name in @('config.json','config.db','config.db-wal','config.db-shm')) {
        if (Test-Path -LiteralPath (Join-Path $injectRuntime ('User/'+$name))) { throw "注入模式创建了 $name" }
    }
    Check '配置注入、界面修改和正常退出均未创建配置库或旧配置文件'
    foreach ($fault in @('corrupt','missing-row','invalid-json','readonly')) {
        $faultRuntime=New-Runtime $fault; $faultDb=Join-Path $faultRuntime 'User/config.db'
        if ($fault -eq 'corrupt') { [IO.File]::WriteAllText($faultDb,'not-a-sqlite-database') }
        else {
            Copy-Item -LiteralPath $db -Destination $faultDb
            if ($fault -eq 'readonly') { (Get-Item -LiteralPath $faultDb).IsReadOnly=$true }
            else { $null=Probe $fault $faultDb }
        }
        $before=(Get-FileHash -LiteralPath $faultDb).Hash
        Start-App $faultRuntime
        $log=Join-Path $faultRuntime 'logs/retry-proxy.log'
        # 只读库可成功读取旧配置，保存时才被拒绝；坏库/坏节点则在读取阶段失败并备份。
        $expectedError=if ($fault -eq 'readonly') { '配置保存失败' } else { '配置库读取失败' }
        Wait-For {
            if (!(Test-Path -LiteralPath $log)) { return $false }
            return (Read-SharedLog $log).Contains($expectedError)
        } "$fault 场景未出现配置保护提示"
        if ($fault -ne 'readonly') {
            $backups=@(Get-ChildItem -LiteralPath (Join-Path $faultRuntime 'User/backup') -Filter 'config_*.db.bak' -ErrorAction SilentlyContinue)
            if ($backups.Count -eq 0) { throw "$fault 场景未保存故障备份" }
        }
        Stop-App -Force
        if ((Get-FileHash -LiteralPath $faultDb).Hash -ne $before) { throw "$fault 场景覆盖了原配置库" }
        if ($fault -eq 'readonly') { Check 'readonly：保存被拒绝且有提示，原库未覆盖' }
        else { Check "$fault：配置读取失败有提示、有备份，原库未覆盖" }
    }
    $record.passed=$true
} catch {
    $record.failure=$_.Exception.Message
    if ($window -ne [IntPtr]::Zero -and $app -and !$app.HasExited) { try { Capture 'failure' } catch {} }
    throw
} finally {
    if ($app -and !$app.HasExited) { Stop-App -Force }
    foreach ($name in $variables) {
        if ($null -eq $saved[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    }
    if ($sourceHash) {
        $record.sourceUnchanged=(Get-FileHash -LiteralPath $ConfigPath).Hash -eq $sourceHash
        if (!$record.sourceUnchanged) { $record.passed=$false; $record.sourceWarning='原始配置在验收期间发生变化，未覆盖或恢复该文件' }
    }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    Write-Host "验收现场和结果：$OutputDirectory"
}

if (!$record.passed) { throw '配置存储验收未全部通过，请查看保留的 summary.json' }
