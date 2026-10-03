#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$OutputDirectory = ''
)
# 当前桌面真实窗口验收；只启动临时副本，只请求本机假上游，保留现场，不写应用日志。
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('.tmp/log-comparison-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$runtime = Join-Path ([IO.Path]::GetTempPath()) ('RetryProxyM4-' + [guid]::NewGuid().ToString('N'))
$variables = @('RETRY_PROXY_CONFIG_JSON','RETRY_PROXY_UI_TEST_ROOT','CLAUDE_CONFIG_DIR','CODEX_HOME','RETRY_PROXY_CLAUDE_CLI','RETRY_PROXY_CODEX_CLI','UPSTREAM_BASE_URL','RETRY_PROXY_PORT')
$variables += @(Get-ChildItem Env: | Where-Object Name -like 'RETRY_*' | ForEach-Object Name)
$variables = @($variables | Sort-Object -Unique)
$saved = @{}; foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$app = $null; $job = $null; $client = $null; $window = [IntPtr]::Zero
$record = [ordered]@{ passed=$false; runtime=$runtime; executable=$ExePath; cases=@(); captures=@(); normalExit=$false; fontColorAutomaticallyVerified=$false; visualReview='人工核对 rows PNG：不一致字段主题警示色、半粗；HTTP400 警示色、HTTP500 错误色。UIA 文本和截图不自动证明字体颜色。' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
if (-not ('RetryProxyTrayVerification' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs') }
if (-not ('LogComparisonWindow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LogComparisonWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
}
function Wait-For([scriptblock]$Condition, [string]$Failure, [int]$Seconds=20) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        if ($app -and $app.HasExited) { throw "测试程序已退出：$Failure" }
        if ($job -and $job.State -in @('Failed','Stopped','Completed')) { throw "假上游已停止：$Failure" }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}
function Find-Control([string]$Value, [switch]$ByName) {
    $element = [Windows.Automation.AutomationElement]::FromHandle($window)
    $property = if ($ByName) { [Windows.Automation.AutomationElement]::NameProperty } else { [Windows.Automation.AutomationElement]::AutomationIdProperty }
    return $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new($property,$Value))
}
function Focus-App {
    $null = [LogComparisonWindow]::SetForegroundWindow($window)
    Wait-For { [LogComparisonWindow]::GetForegroundWindow() -eq $window } '测试窗口未取得前台焦点'
}
function Invoke-Control([string]$Id, [switch]$ByName) {
    Wait-For { $c = Find-Control $Id -ByName:$ByName; $c -and $c.Current.IsEnabled -and !$c.Current.IsOffscreen } "控件不可用：$Id"
    (Find-Control $Id -ByName:$ByName).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Navigate([string]$Name, [string]$Marker) {
    Focus-App
    $ready = @{ Item=$null }
    Wait-For {
        $element = [Windows.Automation.AutomationElement]::FromHandle($window)
        $items = $element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))
        $ready.Item = @($items | Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem -and $_.Current.IsEnabled -and !$_.Current.IsOffscreen } | Select-Object -First 1)[0]
        $null -ne $ready.Item
    } "找不到导航项：$Name"
    $ready.Item.SetFocus()
    $null = [RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]::new(13),[IntPtr]::new(0x001C0001))
    $null = [RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]::new(13),[IntPtr]::new([int64]0xC01C0001))
    Wait-For { $null -ne (Find-Control $Marker) } "未打开页面：$Name"
}
function Set-Query([string]$Query) {
    (Find-Control 'LogSearch').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Query)
    # 绑定明确设置 Delay=200；等防抖与列表重建后再判断 UIA。
    Start-Sleep -Milliseconds 400
}
function Log-Texts {
    $rows = Find-Control 'LogRows'
    if (!$rows) { return }
    # LogLineTextBlock 的 Name 是完整日志，不能依赖拆分的 Run 子元素。
    @($rows.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^\d{4}-\d\d-\d\d .+\[(通道代理|系统)\]' } | Sort-Object -Unique)
}
function Theme {
    $stream = [IO.FileStream]::new((Join-Path $runtime 'User/config.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader = [IO.StreamReader]::new($stream)
    try { return [int](($reader.ReadToEnd() | ConvertFrom-Json).commonConfig.currentThemeType) }
    finally { $reader.Dispose() }
}
function Set-LightTheme {
    Navigate '软件设置' 'SwitchAppearance'
    for ($i=0; $i -lt 6 -and (Theme) -lt 3; $i++) {
        $before = Theme
        Invoke-Control 'SwitchAppearance'
        Wait-For { (Theme) -ne $before } '外观按钮没有改变主题'
    }
    if ((Theme) -notin @(3,4,5)) { throw '外观按钮未切换至浅色主题' }
    Navigate '运行日志' 'LogRows'
}
function Capture([string]$Name, [int]$ExpectedWidth=0, [int]$ExpectedHeight=0) {
    Focus-App; Start-Sleep -Milliseconds 400
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    # 调整尺寸后窗口恢复器可能把无效矩形还原；以截图当刻尺寸验收，防止文件名写窄窗但图片仍是 900px。
    if (($ExpectedWidth -gt 0 -and $bounds.Right-$bounds.Left -ne $ExpectedWidth) -or
        ($ExpectedHeight -gt 0 -and $bounds.Bottom-$bounds.Top -ne $ExpectedHeight)) { throw '截图当刻窗口尺寸与验收目标不符' }
    $image = [Drawing.Bitmap]::new($bounds.Right-$bounds.Left,$bounds.Bottom-$bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($image)
    $rows = (Find-Control 'LogRows').Current.BoundingRectangle
    try {
        $graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$image.Size)
        $image.Save((Join-Path $OutputDirectory ($Name+'.png')),[Drawing.Imaging.ImageFormat]::Png)
        $x = [Math]::Max(0,[int][Math]::Ceiling($rows.Left-$bounds.Left))
        $y = [Math]::Max(0,[int][Math]::Ceiling($rows.Top-$bounds.Top))
        $w = [Math]::Min([int][Math]::Floor($rows.Width),$image.Width-$x)
        $h = [Math]::Min([int][Math]::Floor($rows.Height),$image.Height-$y)
        if ($w -le 0 -or $h -le 0) { throw '日志行区域不可见' }
        $crop = $image.Clone([Drawing.Rectangle]::new($x,$y,$w,$h),$image.PixelFormat)
        try { $crop.Save((Join-Path $OutputDirectory ($Name+'-rows.png')),[Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
        $record.captures += @{ name=$Name; theme=(Theme); width=$image.Width; height=$image.Height; query=(Find-Control 'LogSearch').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value; visibleTexts=@(Log-Texts); rows=@{x=$x;y=$y;width=$w;height=$h} }
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}
function Get-Health([int]$Port) {
    $response = $client.GetAsync("http://127.0.0.1:$Port/_retry/health").GetAwaiter().GetResult()
    try { $response.EnsureSuccessStatusCode() | Out-Null; return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json) }
    finally { $response.Dispose() }
}
# 六项固定假数据；case 通过请求正文送到假上游，上游按 fixture 返回，不改写日志。
$cases = @(
    @{ id='lc01'; client='codex'; path='/v1/responses'; status=200; type='application/json'; model='lc01-gpt'; effort='high'; stream=$false;
       response='{"id":"fixture-lc01","model":"lc01-gpt","reasoning":{"effort":"high"},"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"fixture ok"}]}],"usage":{"input_tokens":10,"output_tokens":2}}';
       fields=@('模型 lc01-gpt','思考 high','输入/输出 10/2 token'); mismatches=0 },
    @{ id='lc02'; client='codex'; path='/v1/responses'; status=200; type='text/event-stream'; model='lc02-sent'; effort='max'; stream=$true;
       response="event: response.created`ndata: {`"type`":`"response.created`",`"response`":{`"id`":`"fixture-lc02`",`"model`":`"lc02-returned`",`"reasoning`":{`"effort`":`"high`"}}}`n`nevent: response.output_text.delta`ndata: {`"type`":`"response.output_text.delta`",`"delta`":`"fixture ok`"}`n`nevent: response.completed`ndata: {`"type`":`"response.completed`",`"response`":{`"status`":`"completed`",`"model`":`"lc02-returned`",`"reasoning`":{`"effort`":`"high`"},`"output`":[],`"usage`":{`"input_tokens`":10,`"output_tokens`":2}}}`n`n";
       fields=@('模型 lc02-sent -> lc02-returned (不一致)','思考 max -> high (不一致)'); mismatches=2 },
    @{ id='lc03'; client='claude'; path='/v1/messages'; status=200; type='text/event-stream'; model='lc03-claude'; effort='max'; stream=$true;
       response="event: message_start`ndata: {`"type`":`"message_start`",`"message`":{`"id`":`"fixture-lc03`",`"type`":`"message`",`"role`":`"assistant`",`"model`":`"lc03-claude`",`"output_config`":{`"effort`":`"high`"},`"content`":[],`"usage`":{`"input_tokens`":10,`"output_tokens`":0}}}`n`nevent: content_block_delta`ndata: {`"type`":`"content_block_delta`",`"index`":0,`"delta`":{`"type`":`"text_delta`",`"text`":`"fixture ok`"}}`n`nevent: message_delta`ndata: {`"type`":`"message_delta`",`"delta`":{`"stop_reason`":`"end_turn`"},`"usage`":{`"output_tokens`":2}}`n`nevent: message_stop`ndata: {`"type`":`"message_stop`"}`n`n";
       fields=@('模型 lc03-claude','思考 max -> high (不一致)'); mismatches=1 },
    @{ id='lc04'; client='claude'; path='/v1/messages'; status=200; type='application/json'; model='lc04-missing'; effort='max'; stream=$false;
       response='{"id":"fixture-lc04","type":"message","role":"assistant","content":[{"type":"text","text":"fixture metadata absent"}],"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":2}}';
       fields=@('模型 lc04-missing -> 未报告','思考 max -> 未报告'); mismatches=0 },
    @{ id='lc05'; client='codex'; path='/v1/responses'; status=400; type='application/json'; model='lc05-http400'; effort='high'; stream=$false;
       response='{"error":{"type":"fixture_bad_request","message":"lc05 fixture HTTP400"}}'; fields=@('HTTP 400'); mismatches=0 },
    @{ id='lc06'; client='claude'; path='/v1/messages'; status=500; type='application/json'; model='lc06-http500'; effort='max'; stream=$false;
       response='{"error":{"type":"fixture_server_error","message":"lc06 fixture HTTP500"}}'; fields=@('HTTP 500'); mismatches=0 }
)
try {
    New-Item -ItemType Directory -Path $runtime,(Join-Path $runtime 'User'),$OutputDirectory | Out-Null
    foreach ($name in $variables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    $env:RETRY_PROXY_UI_TEST_ROOT = $runtime
    $env:CLAUDE_CONFIG_DIR = Join-Path $runtime 'claude'; $env:CODEX_HOME = Join-Path $runtime 'codex'
    $env:RETRY_PROXY_CLAUDE_CLI = Join-Path $runtime 'missing-claude.exe'; $env:RETRY_PROXY_CODEX_CLI = Join-Path $runtime 'missing-codex.exe'
    New-Item -ItemType Directory -Path $env:CLAUDE_CONFIG_DIR,$env:CODEX_HOME | Out-Null
    [IO.File]::WriteAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'config.toml'),'')
    $package = Split-Path -Parent $ExePath
    Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '\.(exe|dll)$|\.(deps|runtimeconfig)\.json$' } | Copy-Item -Destination $runtime
    $translations = Join-Path $package 'User/I18n'
    if (!(Test-Path -LiteralPath $translations)) { $translations = Join-Path $root 'src/RetryProxy.App/User/I18n' }
    Copy-Item -LiteralPath $translations -Destination (Join-Path $runtime 'User/I18n') -Recurse
    $upstreamPort = Get-FreePort
    $ports = @{}
    foreach ($kind in @('codex','claude')) {
        do { $port = Get-FreePort } while ($port -eq $upstreamPort -or $port -in @($ports.Values))
        $ports[$kind] = $port
    }
    $providers = @(); $routes = @()
    foreach ($kind in @('codex','claude')) {
        $providers += @{ id="fixture-$kind"; client_type=$kind; name="fixture-$kind"; base_url="http://127.0.0.1:$upstreamPort"; keys=@(@{id="key-$kind";name='fixture';api_key='sk-log-fixture-only'}) }
        $routes += @{ id="fixture-$kind"; name=$(if ($kind -eq 'codex') {'Codex'} else {'Claude Code'}); client_type=$kind; listen_port=$ports[$kind]; current_provider_id="fixture-$kind"; current_key_id="key-$kind"; local_token=[guid]::NewGuid().ToString('N'); desired_running=$true; keepalive_enabled=$false; max_retries=0; timeout_seconds=10; generation_timeout_seconds=10; total_timeout_seconds=20; base_delay_seconds=0; max_delay_seconds=0 }
    }
    @{ proxy=@{schema_version=7;selected_route_id='fixture-codex';providers=$providers;routes=$routes}; commonConfig=@{clientSetupCompleted=$true;isFirstRun=$false;exitToTray=$false;startMinimized=$false;currentThemeType=0}; otherConfig=@{uiCultureInfoName='zh-Hans'} } |
        ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $runtime 'User/config.json') -Encoding utf8
    $cases | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runtime 'fixtures.json') -Encoding utf8
    $job = Start-Job -ArgumentList $upstreamPort,$runtime -ScriptBlock {
        param($Port,$Directory)
        $ErrorActionPreference = 'Stop'
        $fixtures = Get-Content -LiteralPath (Join-Path $Directory 'fixtures.json') -Raw | ConvertFrom-Json
        $listener = [Net.HttpListener]::new(); $listener.Prefixes.Add("http://127.0.0.1:$Port/"); $listener.Start()
        try {
            [IO.File]::WriteAllText((Join-Path $Directory 'upstream.ready'),'ready')
            while ($true) {
                $pending = $listener.GetContextAsync()
                while (!$pending.IsCompleted) { Start-Sleep -Milliseconds 50 }
                $context = $pending.GetAwaiter().GetResult()
                $reader = [IO.StreamReader]::new($context.Request.InputStream)
                try { $body = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                $fixture = @($fixtures | Where-Object id -eq $body.fixture_case)
                if ($fixture.Count -ne 1) { throw '收到未定义的测试请求' }
                $fixture = $fixture[0]
                $event = @{id=$fixture.id;path=$context.Request.Url.AbsolutePath;model=$body.model;effort=$(if ($fixture.client -eq 'claude') {$body.output_config.effort} else {$body.reasoning.effort});credentialIsFixture=($context.Request.Headers['Authorization'] -eq 'Bearer sk-log-fixture-only' -or $context.Request.Headers['x-api-key'] -eq 'sk-log-fixture-only')}
                [IO.File]::AppendAllText((Join-Path $Directory 'upstream-events.jsonl'),($event | ConvertTo-Json -Compress)+"`n")
                $bytes = [Text.Encoding]::UTF8.GetBytes($fixture.response)
                $context.Response.StatusCode = $fixture.status
                $context.Response.ContentType = $fixture.type
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes,0,$bytes.Length)
                $context.Response.Close()
            }
        } finally { $listener.Close() }
    }
    Wait-For { Test-Path -LiteralPath (Join-Path $runtime 'upstream.ready') } '假上游未启动'
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy=$false; $handler.AllowAutoRedirect=$false
    $client = [Net.Http.HttpClient]::new($handler); $client.Timeout=[TimeSpan]::FromSeconds(25)
    $app = Start-Process -FilePath (Join-Path $runtime (Split-Path -Leaf $ExePath)) -WorkingDirectory $runtime -PassThru
    $null = $app.Handle
    $record.pid = $app.Id; $record.ports=$ports; $record.upstreamPort=$upstreamPort
    Wait-For { $script:window=[RetryProxyTrayVerification]::FindWindow($app.Id,'LLM Retry Proxy',$null); $window -ne [IntPtr]::Zero } '主窗口未出现'
    foreach ($port in $ports.Values) { Wait-For { try { $null=Get-Health $port; return $true } catch { return $false } } '测试通道未启动' }
    Navigate '运行日志' 'LogRows'
    # 切到系统来源会明确把 OnlySelected 置 false，再回全部；该按钮没有 TogglePattern。
    Invoke-Control 'LogSourceSystem'; Invoke-Control 'LogSourceAll'; Invoke-Control 'LogLevelAll'
    Set-Query ''
    Invoke-Control '清空' -ByName
    Wait-For { (Find-Control 'LogCounter').Current.Name -match '^0\s*/\s*0$' } '清空日志未完成'
    foreach ($case in $cases) {
        $body = @{fixture_case=$case.id;model=$case.model;stream=$case.stream}
        if ($case.client -eq 'claude') { $body.messages=@(@{role='user';content='fixture only'}); $body.max_tokens=32; $body.output_config=@{effort=$case.effort} }
        else { $body.input='fixture only'; $body.reasoning=@{effort=$case.effort} }
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"http://127.0.0.1:$($ports[$case.client])$($case.path)")
        $request.Content = [Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 8 -Compress),[Text.Encoding]::UTF8,'application/json')
        $response = $null
        try {
            $response = $client.SendAsync($request).GetAwaiter().GetResult()
            $actual = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $expected = [Text.Encoding]::UTF8.GetBytes($case.response)
            if ([int]$response.StatusCode -ne $case.status -or [Convert]::ToBase64String($actual) -cne [Convert]::ToBase64String($expected)) { throw "$($case.id)：状态码或响应字节透传错误" }
            $record.cases += @{id=$case.id;status=[int]$response.StatusCode;responseByteCount=$actual.Length;responseSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($actual));bytesIdentical=$true;expectedFields=$case.fields}
        } finally { if ($response) { $response.Dispose() }; $request.Dispose() }
    }
    foreach ($port in $ports.Values) {
        Wait-For { (Get-Health $port).metrics.active_requests -eq 0 } '请求尚未结束'
        if ((Get-Health $port).metrics.retry_count -ne 0) { throw '测试发生了不允许的重试' }
    }
    $events = @(Get-Content -LiteralPath (Join-Path $runtime 'upstream-events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    if ($events.Count -ne 6) { throw '假上游未恰好收到六次请求' }
    foreach ($case in $cases) {
        $event = @($events | Where-Object id -eq $case.id)
        if ($event.Count -ne 1 -or !$event[0].credentialIsFixture -or $event[0].model -cne $case.model -or $event[0].effort -cne $case.effort -or $event[0].path -cne $case.path) { throw "$($case.id)：上游请求不符合假数据约定" }
        Set-Query $case.id
        $found = @{text=''}
        Wait-For {
            $lines = @(Log-Texts | Where-Object { $_.Contains($case.id) -and $_.Contains("HTTP $($case.status)") })
            $found.text = @($lines | Where-Object { $line=$_; @($case.fields | Where-Object { !$line.Contains($_) }).Count -eq 0 } | Select-Object -First 1)[0]
            !!$found.text
        } "$($case.id)：UIA LogRows 未出现完整对照字段"
        if ([regex]::Matches($found.text,' \(不一致\)').Count -ne $case.mismatches) { throw "$($case.id)：不一致标记数量错误" }
        ($record.cases | Where-Object id -eq $case.id).uiaText=$found.text
        Capture ('dark-'+$case.id)
    }
    # 搜索仅用于让虚拟化行可见；来源和级别始终全部，当前客户端限制始终关闭。
    foreach ($kind in @('codex','claude')) {
        Invoke-Control $(if ($kind -eq 'codex') {'SelectCodex'} else {'SelectClaude'})
        Set-Query $(if ($kind -eq 'codex') {'[Codex]'} else {'[Claude Code]'})
        Wait-For { @(Log-Texts).Count -gt 0 } '客户端日志未显示'
        Capture ('dark-'+$kind)
    }
    Set-LightTheme
    foreach ($kind in @('codex','claude')) {
        Invoke-Control $(if ($kind -eq 'codex') {'SelectCodex'} else {'SelectClaude'})
        Set-Query $(if ($kind -eq 'codex') {'[Codex]'} else {'[Claude Code]'})
        Wait-For { @(Log-Texts).Count -gt 0 } '浅色客户端日志未显示'
        Capture ('light-'+$kind)
    }
    foreach ($id in @('lc02','lc03','lc05','lc06')) { Set-Query $id; Wait-For { @(Log-Texts).Count -gt 0 } '浅色对照行未显示'; Capture ('light-'+$id) }
    $bounds = [RetryProxyTrayVerification]::Bounds($window)
    # 最小客户区宽度是 720px，外框必须留出边框；沿用既有验收的 760px 外框，避免触发异常尺寸恢复。
    [RetryProxyTrayVerification]::SetBounds($window,$bounds.Left,$bounds.Top,760,600)
    Wait-For { $b=[RetryProxyTrayVerification]::Bounds($window); ($b.Right-$b.Left) -eq 760 -and ($b.Bottom-$b.Top) -eq 600 -and [RetryProxyTrayVerification]::IsUsable($window) } '测试窗口未达到有效的 760x600 实际像素'
    foreach ($id in @('lc02','lc03')) { Set-Query $id; Wait-For { @(Log-Texts).Count -gt 0 } '窄窗对照行未显示'; Capture ('narrow-760x600-'+$id) -ExpectedWidth 760 -ExpectedHeight 600 }
    Set-Query ''
    # 两客户端的实际请求都在同一列表中，证明仅当前客户端筛选已关闭（无需猜按钮颜色）。
    foreach ($id in @('lc01','lc03')) { Set-Query $id; Wait-For { @(Log-Texts | Where-Object { $_.Contains($id) }).Count -gt 0 } '全部客户端筛选未生效' }
    $record.allClientsVisible=$true
    $null = [RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero)
    if (!$app.WaitForExit(25000) -or $app.ExitCode -ne 0) { throw '测试窗口未正常退出' }
    $record.normalExit=$true
    if ([IO.File]::ReadAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json')) -cne '{}' -or [IO.File]::ReadAllText((Join-Path $env:CODEX_HOME 'config.toml')) -cne '') { throw '隔离客户端配置被修改，出现非预期接管' }
    $log = Get-Content -LiteralPath (Join-Path $runtime 'logs/retry-proxy.log') -Raw
    if ($log.Contains('sk-log-fixture-only')) { throw '日志泄露假密钥全文' }
    $record.passed=$true
    Write-Host "PASS：日志字段、标记、缺失语义、六次无重试响应透传；颜色待人工复核。记录：$OutputDirectory；现场：$runtime"
} catch {
    $record.failure=$_.Exception.Message
    if ($app -and !$app.HasExited -and $window -ne [IntPtr]::Zero) { try { Capture 'failure' } catch {} }
    throw
} finally {
    try {
        if ($app -and !$app.HasExited) {
            $null = [RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero)
            if (!$app.WaitForExit(15000)) { $app.Kill(); $null=$app.WaitForExit(5000); $record.forcedCleanup=$true }
        }
        if ($app) { $app.Dispose() }
        if ($job) { Stop-Job $job; Remove-Job $job -Force }
        if ($client) { $client.Dispose() }
    } finally {
        foreach ($name in $variables) {
            if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
        }
        if (Test-Path -LiteralPath $OutputDirectory) { $record | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8 }
        # 刻意保留 runtime、原始代理日志与 PNG/JSON；绝不清理 dist 或用户配置。
    }
}
