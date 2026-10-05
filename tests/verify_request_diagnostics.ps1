#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$OutputDirectory = ''
)
# 必须由验收者显式执行：当前桌面的隔离实际窗口；不启动或替换 dist，不读取真实配置。
# 正常重启以及仅本脚本 PID 的模拟异常退出，用来验证独立诊断恢复；所有现场始终保留。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime_files.ps1')
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('.tmp/request-diagnostics-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$runtime = Join-Path ([IO.Path]::GetTempPath()) ('RetryProxyM4-' + [guid]::NewGuid().ToString('N'))
$variables = @('RETRY_PROXY_CONFIG_JSON','RETRY_PROXY_UI_TEST_ROOT','CLAUDE_CONFIG_DIR','CODEX_HOME','RETRY_PROXY_CLAUDE_CLI','RETRY_PROXY_CODEX_CLI','UPSTREAM_BASE_URL','RETRY_PROXY_PORT')
$variables += @(Get-ChildItem Env: | Where-Object Name -like 'RETRY_*' | ForEach-Object Name)
$variables = @($variables | Sort-Object -Unique)
$saved = @{}; foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$app=$null; $job=$null; $client=$null; $window=[IntPtr]::Zero; $clipboardBefore=$null; $lastCopied=$null; $clipboardCaptured=$false; $ownsOutput=$false
$record = [ordered]@{passed=$false;runtime=$runtime;executable=$ExePath;checks=@();captures=@();requests=@();runs=@();visualReview='待人工检查深浅主题、760x600 窄窗截图与事件文字；UIA 不证明布局或颜色正确。'}
$privateKey='sk-diagnostic-fixture-only'; $privateBody='diagnostic-private-body-canary'; $privateQuery='diagnostic-private-query-canary'; $privateResponse='diagnostic-private-response-canary'
$canaries=@($privateKey,$privateBody,$privateQuery,$privateResponse)

function Wait-For([scriptblock]$Condition,[string]$Failure,[int]$Seconds=20) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        if ($app -and $app.HasExited) { throw "测试程序已退出：$Failure" }
        if ($job -and $job.State -in @('Failed','Stopped','Completed')) { throw "假上游已停止：$Failure" }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}
function Check([bool]$Condition,[string]$Name) {
    if (!$Condition) { throw $Name }
    $record.checks += $Name
}
function Get-ClipboardTextSafe([int]$Attempts=5) {
    # 剪贴板同步工具（远程控制/剪贴板历史）会短暂独占剪贴板：打开失败时短暂重试。
    # 返回 $null 表示始终打不开；'' 表示剪贴板里没有文本。
    foreach ($attempt in 1..$Attempts) {
        try {
            if ([Windows.Forms.Clipboard]::ContainsText()) { return [Windows.Forms.Clipboard]::GetText() }
            return ''
        } catch { Start-Sleep -Milliseconds 100 }
    }
    return $null
}
function Find-Control([string]$Value,[switch]$ByName) {
    $element=[Windows.Automation.AutomationElement]::FromHandle($window)
    $property=if($ByName){[Windows.Automation.AutomationElement]::NameProperty}else{[Windows.Automation.AutomationElement]::AutomationIdProperty}
    return $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new($property,$Value))
}
function Texts-Under($Element) {
    if (!$Element) { return '' }
    return (@($Element.Current.Name) + @($Element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique)) -join "`n"
}
function Focus-App {
    $null=[RequestDiagnosticWindow]::SetForegroundWindow($window)
    Wait-For { [RequestDiagnosticWindow]::GetForegroundWindow() -eq $window } '测试窗口未取得前台焦点'
}
function Invoke-Control([string]$Id) {
    Wait-For { $c=Find-Control $Id; $c -and $c.Current.IsEnabled -and !$c.Current.IsOffscreen } "控件不可用：$Id"
    (Find-Control $Id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Send-Key([int]$Key) {
    $null=[RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]::new($Key),[IntPtr]::new(1))
    $null=[RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]::new($Key),[IntPtr]::new([int64]0xC0000001))
}
function Navigation-Item([string]$Name) {
    $element=[Windows.Automation.AutomationElement]::FromHandle($window)
    return @($element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name)) |
        Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem -and $_.Current.IsEnabled -and !$_.Current.IsOffscreen } | Select-Object -First 1)[0]
}
function Navigate([string]$Name,[string]$Marker) {
    Focus-App
    Wait-For { $null -ne (Navigation-Item $Name) } "找不到导航项：$Name"
    (Navigation-Item $Name).SetFocus(); Send-Key 13
    Wait-For { $null -ne (Find-Control $Marker) } "未打开页面：$Name"
}
function Selected-OptionName($Combo) {
    $items=@($Combo.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())
    if ($items.Count -eq 1) { return $items[0].Current.Name }
    return ''
}
function Select-Option([string]$Id,[string]$Name) {
    $combo=Find-Control $Id
    if (!$combo) { throw "找不到筛选器：$Id" }
    # 下拉弹窗的包装节点未必支持 SelectionItemPattern；使用用户可操作的键盘选择。
    Focus-App
    $combo.SetFocus()
    Send-Key 36
    Wait-For { (Selected-OptionName $combo) -eq '全部' } "筛选器未回到第一项：$Id"
    for ($index=0; $index -lt 16; $index++) {
        if ((Selected-OptionName $combo) -eq $Name) { return }
        Send-Key 40
        Start-Sleep -Milliseconds 150
    }
    throw "找不到筛选选项：$Id / $Name"
}
function Set-Search([string]$Value) {
    (Find-Control 'DiagnosticSearch').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
function Diagnostic-Rows {
    $list=Find-Control 'DiagnosticList'
    if (!$list) { return }
    foreach ($element in $list.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
        if ($element.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $element.Current.AutomationId -match '^[0-9a-fA-F]{32}$') {
            [pscustomobject]@{id=$element.Current.AutomationId;text=(Texts-Under $element);element=$element}
        }
    }
}
function Row-For([string]$Model) { @(Diagnostic-Rows | Where-Object text -like "*$Model*" | Select-Object -First 1)[0] }
function Assert-Rows([int]$Count,[string]$Contains='') {
    Wait-For {
        $rows=@(Diagnostic-Rows)
        $rows.Count -eq $Count -and (!$Contains -or @($rows | Where-Object text -like "*$Contains*").Count -gt 0)
    } "请求列表不符合预期：$Count 条 / $Contains"
}
function Read-SharedText([string]$Path) {
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
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
function Theme { [int]((Read-ConfigDb 'common' | ConvertFrom-Json).currentThemeType) }
function Set-LightTheme {
    Navigate '软件设置' 'SwitchAppearance'
    for ($i=0; $i -lt 6 -and (Theme) -lt 3; $i++) {
        $before=Theme; Invoke-Control 'SwitchAppearance'
        Wait-For { (Theme) -ne $before } '切换外观未改变主题'
    }
    Check ((Theme) -in @(3,4,5)) '通过现有外观按钮进入浅色主题'
    Navigate '请求诊断' 'DiagnosticList'
}
function Capture([string]$Name,[int]$ExpectedWidth=0,[int]$ExpectedHeight=0) {
    Focus-App; Start-Sleep -Milliseconds 400
    $bounds=[RetryProxyTrayVerification]::Bounds($window)
    $width=$bounds.Right-$bounds.Left; $height=$bounds.Bottom-$bounds.Top
    if (($ExpectedWidth -gt 0 -and $width -ne $ExpectedWidth) -or ($ExpectedHeight -gt 0 -and $height -ne $ExpectedHeight)) { throw '截图当刻真实窗口尺寸不符合预期' }
    $image=[Drawing.Bitmap]::new($width,$height); $graphics=[Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$image.Size)
        $image.Save((Join-Path $OutputDirectory ($Name+'.png')),[Drawing.Imaging.ImageFormat]::Png)
        $record.captures += @{name=$Name;width=$width;height=$height;theme=(Theme);requestIds=@(Diagnostic-Rows | ForEach-Object id)}
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Capture-RowHover([string]$Name,[string]$RequestId) {
    Focus-App
    $row=Find-Control $RequestId
    Check ($row -and !$row.Current.IsOffscreen) '悬停目标请求行可见'
    $box=$row.Current.BoundingRectangle
    $original=[Windows.Forms.Cursor]::Position
    try {
        [Windows.Forms.Cursor]::Position=[Drawing.Point]::new([int]($box.Left+$box.Width/2),[int]($box.Top+$box.Height/2))
        Start-Sleep -Milliseconds 300
        $hit=[Windows.Automation.AutomationElement]::FromPoint([Windows.Point]::new([Windows.Forms.Cursor]::Position.X,[Windows.Forms.Cursor]::Position.Y))
        while ($hit -and $hit.Current.AutomationId -ne $RequestId) { $hit=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($hit) }
        Check ($null -ne $hit) '真实鼠标命中目标请求行'
        Capture $Name
        $record.checks += '悬停截图已记录，圆角轮廓需视觉复核'
    } finally { [Windows.Forms.Cursor]::Position=$original }
}
function Get-FreePort {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0); $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}
function Health([int]$Port) {
    $response=$client.GetAsync("http://127.0.0.1:$Port/_retry/health").GetAwaiter().GetResult()
    try { $response.EnsureSuccessStatusCode() | Out-Null; $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json }
    finally { $response.Dispose() }
}
function Start-App {
    $script:app=Start-Process -FilePath (Join-Path $runtime (Split-Path -Leaf $ExePath)) -WorkingDirectory $runtime -PassThru
    $null=$app.Handle # 固定句柄，后续等待或强停只对应本次启动的实例。
    $record.runs += @{pid=$app.Id;startedAt=$app.StartTime.ToString('o');exit='running'}
    Wait-For { $script:window=[RetryProxyTrayVerification]::FindWindow($app.Id,'LLM Retry Proxy',$null); $window -ne [IntPtr]::Zero } '测试主窗口未出现'
    foreach ($port in $ports.Values) { Wait-For { try { $null=Health $port; return $true } catch { return $false } } '隔离通道未启动' }
    Wait-For { $null -ne (Find-Control 'SelectCodex') } '客户端选择器尚未加载'
}
function Stop-App([switch]$SimulateCrash) {
    if ($SimulateCrash) { $app.Kill() }
    else { $null=[RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero) }
    if (!$app.WaitForExit(25000)) { throw '隔离测试程序未在期限内退出' }
    $record.runs[-1].exit=if($SimulateCrash){'deliberate-crash'}else{'normal'}
    $record.runs[-1].exitCode=$app.ExitCode
    if (!$SimulateCrash -and $app.ExitCode -ne 0) { throw '隔离测试程序正常退出码异常' }
    $app.Dispose(); $script:app=$null; $script:window=[IntPtr]::Zero
}
function Begin-Fixture([string]$Case,[string]$Kind='codex',[switch]$Models) {
    $path=if($Models){'/v1/models'}elseif($Kind -eq 'claude'){'/v1/messages'}else{'/v1/responses'}
    $method=if($Models){[Net.Http.HttpMethod]::Get}else{[Net.Http.HttpMethod]::Post}
    $request=[Net.Http.HttpRequestMessage]::new($method,"http://127.0.0.1:$($ports[$Kind])${path}?fixture=$privateQuery")
    if (!$Models) {
        $body=@{fixture_case=$Case;model="diag-$Case-model";stream=$false}
        if ($Kind -eq 'claude') { $body.messages=@(@{role='user';content=$privateBody}); $body.max_tokens=32 }
        else { $body.input=$privateBody }
        $request.Content=[Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
    }
    [pscustomobject]@{case=$Case;request=$request;task=$client.SendAsync($request)}
}
function Complete-Fixture($Pending,[int]$Status=200,[switch]$Aborted) {
    $response=$null
    try {
        $response=$Pending.task.GetAwaiter().GetResult()
        if ($Aborted) { throw '模拟异常退出的请求竟然正常返回' }
        Check ([int]$response.StatusCode -eq $Status) "$($Pending.case) 返回原预期 HTTP $Status"
        $bytes=$response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $record.requests += @{case=$Pending.case;status=[int]$response.StatusCode;responseBytes=$bytes.Length}
    } catch {
        if (!$Aborted -or $response) { throw }
        $record.requests += @{case=$Pending.case;aborted=$true}
    } finally { if($response){$response.Dispose()}; $Pending.request.Dispose() }
}
function Assert-Safe([string]$Text,[string]$Scope) {
    foreach ($canary in $canaries) { if ($Text.Contains($canary,[StringComparison]::Ordinal)) { throw "$Scope 含禁止记录的测试标记" } }
}
function Copy-And-Check([string]$Button,[string]$RequestId,[switch]$IdOnly) {
    Invoke-Control $Button
    # 不与 WPF 的 OLE 写入/刷新并发打开剪贴板。
    Start-Sleep -Milliseconds 250
    Wait-For {
        $copied=Get-ClipboardTextSafe
        if (!$copied) { return $false }
        if ($IdOnly) { return $copied -ceq $RequestId }
        return $copied.Contains($RequestId) -and $copied.Contains('原重试次数')
    } '诊断复制未得到当前请求编号或安全摘要'
    $text=Get-ClipboardTextSafe -Attempts 15
    if ($null -eq $text) { throw '剪贴板被其他程序占用，无法读取复制结果' }
    $script:lastCopied=$text
    $copyError=Find-Control '复制失败，请稍后重试' -ByName
    Check (!$copyError -or $copyError.Current.IsOffscreen) '复制完成且界面未留下复制失败提示'
    Assert-Safe $text '复制内容'
    if ($IdOnly) { Check ($text -ceq $RequestId) '复制完整请求编号' }
    else { Check (!$text.TrimStart().StartsWith('{') -and $text.Contains('原重试次数')) '复制的是安全文字摘要，未复制原始对象或 JSON' }
}
function Open-Detail([string]$RequestId) {
    Focus-App
    $row=Find-Control $RequestId; $row.SetFocus()
    Invoke-Control $RequestId
    Wait-For { $c=Find-Control 'DiagnosticOverview'; $c -and $c.Current.Name.Contains($RequestId) } '请求详情未打开'
    $save=Find-Control 'DrawerSave'
    Check (!$save -or $save.Current.IsOffscreen) '只读抽屉没有可见保存按钮'
    $close=Find-Control 'DrawerCancel'
    Check ($close -and $close.Current.Name -eq '关闭' -and $close.Current.IsEnabled) '只读关闭按钮文案与可用状态正确'
}
function Close-Detail([string]$RequestId) {
    Focus-App; Send-Key 27
    Wait-For { $null -eq (Find-Control 'DiagnosticOverview') } 'Esc 未关闭只读抽屉'
    Wait-For { [Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq $RequestId } '关闭详情未恢复原请求行焦点'
    Check ($null -ne (Find-Control 'DiagnosticList')) '关闭抽屉恢复请求列表'
    Check ($null -eq (Find-Control '放弃未保存的修改？' -ByName)) '只读抽屉没有未保存确认'
}
function Diagnostic-DbText {
    $db=Join-Path $runtime 'logs/data.db'
    if (!(Test-Path -LiteralPath $db)) { return '' }
    # 请求诊断已迁入 SQLite（WAL）：经 python sqlite3 读取；应用运行中多进程并发由 WAL 保证。
    $reader=@'
import sqlite3, sys
connection = sqlite3.connect(sys.argv[1], timeout=5)
rows = connection.execute("SELECT session_id, request_id, kind, entry_json, COALESCE(request_json, '') FROM diagnostic_events").fetchall()
connection.close()
sys.stdout.write('\n'.join('|'.join('' if v is None else str(v) for v in row) for row in rows))
'@
    $scriptPath=Join-Path ([IO.Path]::GetTempPath()) ('retry-proxy-diag-read-' + [guid]::NewGuid().ToString('N') + '.py')
    try {
        [IO.File]::WriteAllText($scriptPath,$reader,[Text.UTF8Encoding]::new($false))
        $output=@(& python -X utf8 $scriptPath $db 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "读取诊断库失败：$($output -join ' ')" }
        return ($output -join "`n")
    } finally {
        Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    }
}

try {
    $protected=[IO.Path]::GetFullPath((Join-Path $root 'dist'))+[IO.Path]::DirectorySeparatorChar
    $ExePath=(Resolve-Path -LiteralPath $ExePath).ProviderPath
    if ($ExePath.StartsWith($protected,[StringComparison]::OrdinalIgnoreCase) -or ($OutputDirectory+[IO.Path]::DirectorySeparatorChar).StartsWith($protected,[StringComparison]::OrdinalIgnoreCase)) { throw '请使用独立验收包和输出目录，禁止使用 dist' }
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'summary.json')) { throw '输出目录已有验收记录，请使用新目录' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $ownsOutput=$true
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw '请使用 pwsh -STA -File 执行，以验证剪贴板' }
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
    if (-not ('RetryProxyTrayVerification' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs') }
    if (-not ('RequestDiagnosticWindow' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RequestDiagnosticWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
    }
    $clipboardCaptured=$true
    try { $clipboardBefore=[Windows.Forms.Clipboard]::GetDataObject() }
    catch { $clipboardCaptured=$false; if ($record) { $record.clipboardCaptureFailure=$_.Exception.Message } }
    New-Item -ItemType Directory -Path $runtime,(Join-Path $runtime 'User') | Out-Null
    foreach ($name in $variables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    $env:RETRY_PROXY_UI_TEST_ROOT=$runtime
    $env:CLAUDE_CONFIG_DIR=Join-Path $runtime 'claude'; $env:CODEX_HOME=Join-Path $runtime 'codex'
    $env:RETRY_PROXY_CLAUDE_CLI=Join-Path $runtime 'missing-claude.exe'; $env:RETRY_PROXY_CODEX_CLI=Join-Path $runtime 'missing-codex.exe'
    New-Item -ItemType Directory -Path $env:CLAUDE_CONFIG_DIR,$env:CODEX_HOME | Out-Null
    [IO.File]::WriteAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'config.toml'),'')
    $package=Split-Path -Parent $ExePath
    Copy-RetryProxyRuntime -ExePath $ExePath -DestinationDirectory $runtime
    $translations=Join-Path $package 'User/I18n'
    if (!(Test-Path -LiteralPath $translations)) { $translations=Join-Path $root 'src/RetryProxy.App/User/I18n' }
    Copy-Item -LiteralPath $translations -Destination (Join-Path $runtime 'User/I18n') -Recurse
    $upstreamPort=Get-FreePort; $ports=@{}
    foreach ($kind in @('codex','claude')) { do {$port=Get-FreePort} while ($port -eq $upstreamPort -or $port -in @($ports.Values)); $ports[$kind]=$port }
    $providers=@(); $routes=@()
    foreach ($kind in @('codex','claude')) {
        foreach ($suffix in $(if($kind -eq 'codex'){@('a','b')}else{@('a')})) {
            $name=if($kind -eq 'codex'){"诊断 Codex $($suffix.ToUpperInvariant())"}else{'诊断 Claude'}
            $providers+=@{id="diag-$kind-$suffix";client_type=$kind;name=$name;base_url="http://127.0.0.1:$upstreamPort";keys=@(@{id="diag-$kind-$suffix-key";name='测试 Key';api_key=$privateKey})}
        }
        $routes+=@{id="diag-$kind";name=$(if($kind -eq 'codex'){'Codex'}else{'Claude Code'});client_type=$kind;listen_port=$ports[$kind];current_provider_id="diag-$kind-a";current_key_id="diag-$kind-a-key";local_token=[guid]::NewGuid().ToString('N');desired_running=$true;keepalive_enabled=$false;max_retries=2;timeout_seconds=90;generation_timeout_seconds=90;total_timeout_seconds=120;base_delay_seconds=0.5;max_delay_seconds=0.5}
    }
    @{proxy=@{schema_version=7;selected_route_id='diag-codex';providers=$providers;routes=$routes};commonConfig=@{clientSetupCompleted=$true;isFirstRun=$false;exitToTray=$false;startMinimized=$false;currentThemeType=0};otherConfig=@{uiCultureInfoName='zh-Hans'}} |
        ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $runtime 'User/config.json') -Encoding utf8
    $record.ports=$ports; $record.upstreamPort=$upstreamPort
    $job=Start-Job -ArgumentList $upstreamPort,$runtime,$privateKey,$privateResponse -ScriptBlock {
        param($Port,$Directory,$FakeKey,$ResponseMarker)
        $ErrorActionPreference='Stop'; $counts=@{}
        $listener=[Net.HttpListener]::new(); $listener.Prefixes.Add("http://127.0.0.1:$Port/"); $listener.Start()
        try {
            [IO.File]::WriteAllText((Join-Path $Directory 'upstream.ready'),'ready')
            while ($true) {
                $pending=$listener.GetContextAsync()
                while (!$pending.IsCompleted) { Start-Sleep -Milliseconds 50 }
                $context=$pending.GetAwaiter().GetResult()
                try {
                    if ($context.Request.HttpMethod -eq 'GET') { $case='models'; $body=$null }
                    else {
                        $reader=[IO.StreamReader]::new($context.Request.InputStream)
                        try { $body=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                        $case=[string]$body.fixture_case
                    }
                    if ($case -notin @('retry','failure','claude','live','crash','models')) { throw '假上游收到未定义请求' }
                    $counts[$case]=1+[int]$counts[$case]
                    $credential=$context.Request.Headers['Authorization'] -ceq "Bearer $FakeKey" -or $context.Request.Headers['x-api-key'] -ceq $FakeKey
                    $event=@{case=$case;send=$counts[$case];model=$body.model;credentialIsFixture=$credential}
                    [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'),($event | ConvertTo-Json -Compress)+"`n")
                    if ($case -in @('live','crash')) {
                        $clock=[Diagnostics.Stopwatch]::StartNew()
                        while (!(Test-Path -LiteralPath (Join-Path $Directory "release-$case"))) {
                            if ($clock.Elapsed.TotalSeconds -gt 120) { throw '受控等待请求没有被释放' }
                            Start-Sleep -Milliseconds 50
                        }
                    }
                    $status=if($case -eq 'retry' -and $counts[$case] -eq 1){429}elseif($case -eq 'failure'){400}else{200}
                    $payload=if($status -ne 200){@{error=@{type=$(if($status -eq 429){'rate_limit_error'}else{'invalid_request_error'});message=$ResponseMarker}}}
                        elseif($case -eq 'models'){@{data=@(@{id='diag-model-list-only'})}}
                        elseif($case -eq 'claude'){@{id='diag-response';type='message';role='assistant';model=$body.model;content=@(@{type='text';text=$ResponseMarker});stop_reason='end_turn';usage=@{input_tokens=10;output_tokens=2}}}
                        else{@{id='diag-response';model=$body.model;status='completed';output=@(@{type='message';content=@(@{type='output_text';text=$ResponseMarker})});usage=@{input_tokens=10;output_tokens=2}}}
                    $bytes=[Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 12 -Compress))
                    $context.Response.StatusCode=$status; $context.Response.ContentType='application/json'; $context.Response.ContentLength64=$bytes.Length
                    try { $context.Response.OutputStream.Write($bytes,0,$bytes.Length) }
                    catch [Net.HttpListenerException] { if ($case -ne 'crash') { throw } }
                    catch [IO.IOException] { if ($case -ne 'crash') { throw } }
                } finally {
                    try { $context.Response.Close() } catch { if ($case -ne 'crash') { throw } }
                }
            }
        } finally { $listener.Close() }
    }
    Wait-For { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } '假上游未启动'
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.UseProxy=$false; $handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromSeconds(125)
    Start-App
    Invoke-Control 'SelectCodex'
    Complete-Fixture (Begin-Fixture 'retry')
    Invoke-Control 'diag-codex-b-key'
    Wait-For { (Find-Control 'CurrentProviderKey').Current.Name.Contains('诊断 Codex B') } '测试供应商 B 未被选中'
    Complete-Fixture (Begin-Fixture 'failure') 400
    Complete-Fixture (Begin-Fixture 'claude' 'claude')
    Complete-Fixture (Begin-Fixture 'models' -Models)
    foreach ($port in $ports.Values) { Wait-For { (Health $port).metrics.active_requests -eq 0 } '请求尚未收尾' }
    Navigate '请求诊断' 'DiagnosticList'; Assert-Rows 2 'diag-retry-model'
    $retry=Row-For 'diag-retry-model'; $failure=Row-For 'diag-failure-model'
    Check ($retry.text.Contains('成功（重试 1 次）') -and $retry.text.Contains('诊断 Codex A')) '429 后 200：成功、原重试 1 次及实际供应商正确'
    Check ($failure.text.Contains('失败') -and $failure.text.Contains('诊断 Codex B')) '普通失败与实际供应商正确'
    $record.requestIds=@{retry=$retry.id;failure=$failure.id}
    $p=Navigation-Item '一键准备'; $d=Navigation-Item '请求诊断'; $l=Navigation-Item '运行日志'
    Check ($p.Current.BoundingRectangle.Top -lt $d.Current.BoundingRectangle.Top -and $d.Current.BoundingRectangle.Top -lt $l.Current.BoundingRectangle.Top) '独立诊断导航位于一键准备与运行日志之间'
    $date=(Find-Control 'DiagnosticDate').GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $record.dateSelection=@($date | ForEach-Object { @{name=$_.Current.Name;automationId=$_.Current.AutomationId} })
    Check ($date.Count -eq 1 -and $date[0].Current.Name -like "今天*$(Get-Date -Format yyyy-MM-dd)*") '默认查询今天'
    Capture 'dark-codex-list'
    Capture-RowHover 'dark-row-hover' $retry.id

    Invoke-Control 'SelectClaude'; Assert-Rows 1 'diag-claude-model'
    $claude=Row-For 'diag-claude-model'; $record.requestIds.claude=$claude.id
    Check ($claude.text.Contains('成功')) 'Claude 成功按共享客户端显示'
    Navigate '供应商' 'CurrentProviderKey'
    Wait-For { (Find-Control 'CurrentProviderKey').Current.Name.Contains('诊断 Claude') } '跨页面客户端选择未共享'
    Navigate '运行日志' 'LogRows'; Check ($null -ne (Find-Control 'LogSearch')) '原运行日志导航和搜索仍可用'
    Navigate '请求诊断' 'DiagnosticList'; Assert-Rows 1 'diag-claude-model'
    Invoke-Control 'SelectCodex'; Assert-Rows 2

    Select-Option 'DiagnosticProvider' '诊断 Codex B'; Assert-Rows 1 'diag-failure-model'
    Select-Option 'DiagnosticKey' '诊断 Codex B · 测试 Key'; Assert-Rows 1 'diag-failure-model'
    Select-Option 'DiagnosticModel' 'diag-retry-model'; Assert-Rows 0
    Select-Option 'DiagnosticModel' '全部'; Assert-Rows 1 'diag-failure-model'
    Select-Option 'DiagnosticProvider' '全部'; Assert-Rows 2
    Select-Option 'DiagnosticOutcome' '成功'; Assert-Rows 1 'diag-retry-model'
    Set-Search $failure.id; Assert-Rows 0
    Select-Option 'DiagnosticOutcome' '全部'; Assert-Rows 1 'diag-failure-model'
    Set-Search '00000000000000000000000000000000'; Assert-Rows 0
    Set-Search $retry.id; Assert-Rows 1 'diag-retry-model'
    Check $true '结果、供应商、Key、模型及完整请求编号筛选已正反向验证'
    Open-Detail $retry.id
    Wait-For { (Texts-Under (Find-Control 'DiagnosticEvents')).Contains('重试等待结束') } '详情缺少重试等待事件'
    $overview=(Find-Control 'DiagnosticOverview').Current.Name; $events=Texts-Under (Find-Control 'DiagnosticEvents')
    Check ($overview.Contains('实际发送次数：2') -and $overview.Contains('原重试次数：1')) '实际发送数与原重试次数分别显示'
    Check ($events.Contains('实际发送序号：1') -and $events.Contains('实际发送序号：2') -and $events.Contains('等待重试') -and $events.Contains('实际等待')) '详情逐次实际发送与计划、实际等待可见'
    Copy-And-Check 'DiagnosticCopyId' $retry.id -IdOnly
    Copy-And-Check 'DiagnosticCopySummary' $retry.id
    Assert-Safe $overview '详情概况'; Assert-Safe $events '详情事件'
    Capture 'dark-retry-detail'
    Close-Detail $retry.id; Assert-Rows 1 'diag-retry-model'
    Set-Search ''; Assert-Rows 2

    Focus-App; (Find-Control 'DiagnosticSearch').SetFocus()
    $live=Begin-Fixture 'live'
    Wait-For { $r=Row-For 'diag-live-model'; $r -and $r.text.Contains('处理中') } '当前页面没有实时出现处理中请求'
    $liveRow=Row-For 'diag-live-model'; $record.requestIds.live=$liveRow.id
    Check ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'DiagnosticSearch') '新增处理中请求未抢走搜索框焦点'
    Capture 'dark-live-pending'
    [IO.File]::WriteAllText((Join-Path $runtime 'release-live'),'release')
    Complete-Fixture $live
    Wait-For { $r=Row-For 'diag-live-model'; $r -and $r.id -eq $liveRow.id -and $r.text.Contains('成功') } '当前页面没有原位更新请求结果'
    Check ([Windows.Automation.AutomationElement]::FocusedElement.Current.AutomationId -eq 'DiagnosticSearch') '请求完成实时刷新未抢焦点'
    Assert-Rows 3
    Check (@(Diagnostic-Rows | Where-Object text -like '*diag-model-list-only*').Count -eq 0) 'GET 模型查询未进入请求诊断'
    $upstream=@((Read-SharedText (Join-Path $runtime 'upstream-events.jsonl')) -split "`n" | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    Check (@($upstream | Where-Object case -eq 'retry').Count -eq 2 -and @($upstream | Where-Object case -eq 'models').Count -eq 1 -and @($upstream | Where-Object { !$_.credentialIsFixture }).Count -eq 0) '假上游确认重试两次发送、一次模型查询与测试凭据'

    Set-LightTheme; Assert-Rows 3; Capture 'light-codex-list'
    Capture-RowHover 'light-row-hover' $retry.id
    Set-Search $retry.id; Assert-Rows 1; Open-Detail $retry.id; Capture 'light-retry-detail'; Close-Detail $retry.id
    $bounds=[RetryProxyTrayVerification]::Bounds($window)
    [RetryProxyTrayVerification]::SetBounds($window,$bounds.Left,$bounds.Top,760,600)
    Wait-For { $b=[RetryProxyTrayVerification]::Bounds($window); $b.Right-$b.Left -eq 760 -and $b.Bottom-$b.Top -eq 600 -and [RetryProxyTrayVerification]::IsUsable($window) } '窗口未达到有效760x600真实像素'
    Capture 'narrow-760x600-list' 760 600
    $listBounds=(Find-Control 'DiagnosticList').Current.BoundingRectangle
    Open-Detail $retry.id
    Wait-For {
        # Border 没有自动化节点；比较实际可见的详情正文与原列表两侧边界。
        $detail=Find-Control 'DiagnosticOverview'
        if (!$detail) { return $false }
        $bounds=$detail.Current.BoundingRectangle
        $record.narrowDrawer=@{detailWidth=$bounds.Width;detailLeft=$bounds.Left;detailRight=$bounds.Right;listWidth=$listBounds.Width;listLeft=$listBounds.Left;listRight=$listBounds.Right}
        $bounds.Width -gt 400 -and $bounds.Width -lt 640 -and
            [Math]::Abs($bounds.Left-$listBounds.Left) -le 24 -and [Math]::Abs($bounds.Right-$listBounds.Right) -le 24
    } '窄窗只读详情未在动画结束后铺满内容区'
    Check $true '窄窗只读抽屉占满内容区'
    Capture 'narrow-760x600-detail' 760 600
    Close-Detail $retry.id
    Set-Search ''; Assert-Rows 3
    Stop-App
    Start-App; Navigate '请求诊断' 'DiagnosticList'; Invoke-Control 'SelectCodex'; Assert-Rows 3
    Check (@(Diagnostic-Rows | Where-Object id -eq $retry.id).Count -eq 1 -and @((Diagnostic-Rows) | Where-Object text -like '*处理中*').Count -eq 0) '正常重启后历史请求保留且没有旧运行处理中标记'
    Capture 'restarted-history'

    # 在诊断开始事件已经落盘后，只强停自己创建并固定句柄的进程，制造缺收尾的旧会话。
    $crash=Begin-Fixture 'crash'
    Wait-For { $r=Row-For 'diag-crash-model'; $r -and $r.text.Contains('处理中') } '异常恢复场景未进入处理中'
    $crashRow=Row-For 'diag-crash-model'; $record.requestIds.crash=$crashRow.id
    Wait-For { (Diagnostic-DbText).Contains($crashRow.id) } '旧运行开始事件尚未落库'
    Capture 'before-simulated-crash'
    Stop-App -SimulateCrash
    Complete-Fixture $crash -Aborted
    [IO.File]::WriteAllText((Join-Path $runtime 'release-crash'),'release')
    Start-App; Navigate '请求诊断' 'DiagnosticList'; Invoke-Control 'SelectCodex'; Assert-Rows 4
    $restored=Row-For 'diag-crash-model'
    Wait-For { $r=Row-For 'diag-crash-model'; $r -and $r.text.Contains('结束状态未记录') } '缺收尾的旧会话没有显示结束状态未记录'
    $restored=Row-For 'diag-crash-model'
    Check ($restored.id -eq $crashRow.id -and !$restored.text.Contains('失败') -and !$restored.text.Contains('处理中')) '异常重启保留完整ID，缺收尾未误标失败或继续处理中'
    Open-Detail $crashRow.id
    $overview=(Find-Control 'DiagnosticOverview').Current.Name
    Check ($overview.Contains('结束状态未记录') -and $overview.Contains('总耗时：未记录')) '缺收尾详情没有编造总耗时'
    Capture 'restarted-incomplete-detail'
    Close-Detail $crashRow.id
    Navigate '运行日志' 'LogRows'
    Stop-App
    Assert-Safe (Diagnostic-DbText) '独立诊断数据库'
    Check ([IO.File]::ReadAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json')) -ceq '{}' -and [IO.File]::ReadAllText((Join-Path $env:CODEX_HOME 'config.toml')) -ceq '') '隔离客户端配置未被接管或修改'
    $record.passed=$true
    Write-Host "PASS：请求诊断交互、筛选、只读详情、复制、实时更新、正常及异常重启；截图待人工复核。记录：$OutputDirectory；现场：$runtime"
} catch {
    $record.failure=$_.Exception.Message
    if ($app -and !$app.HasExited -and $window -ne [IntPtr]::Zero) {
        try { Capture 'failure' } catch { $record.failureCapture=$_.Exception.Message }
        try { Texts-Under ([Windows.Automation.AutomationElement]::FromHandle($window)) | Set-Content -LiteralPath (Join-Path $OutputDirectory 'failure-ui.txt') -Encoding utf8 } catch {}
    }
    throw
} finally {
    try {
        if ($app -and !$app.HasExited) {
            $null=[RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero)
            if (!$app.WaitForExit(15000)) { $app.Kill(); $null=$app.WaitForExit(5000); $record.forcedCleanup=$true }
        }
        if ($app) { $app.Dispose() }
        if ($job) { Stop-Job $job; Remove-Job $job -Force }
        if ($client) { $client.Dispose() }
        # 不覆盖验收期间其他程序新写的剪贴板，不把原剪贴板写入任何文件；剪贴板被占用不视为验收失败。
        try {
            $current=Get-ClipboardTextSafe -Attempts 15
            if ($clipboardCaptured -and $lastCopied -and $current -ceq $lastCopied) {
                if ($clipboardBefore) { [Windows.Forms.Clipboard]::SetDataObject($clipboardBefore,$true) } else { [Windows.Forms.Clipboard]::Clear() }
            }
        } catch { if ($record) { $record.clipboardRestoreFailure=$_.Exception.Message } }
    } catch { $record.cleanupFailure=$_.Exception.Message; $record.passed=$false; throw }
    finally {
        foreach ($name in $variables) {
            if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
        }
        if ($ownsOutput -and (Test-Path -LiteralPath $OutputDirectory)) { $record | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8 }
        # runtime、请求诊断数据库、原始运行日志、截图和summary始终保留；不执行目录清理。
    }
}
