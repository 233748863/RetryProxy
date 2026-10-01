#requires -Version 7.0
param(
 [string]$ExePath = (Join-Path $PSScriptRoot '../src/RetryProxy.App/bin/x64/Debug/net9.0-windows10.0.22621.0/RetryProxy.exe'),
 [switch]$NoScreenshot
)
# M4：真实程序、窗口与退出流程；程序和两个客户端均复制到临时目录，用独立测试互斥。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path ([IO.Path]::GetTempPath()) ('RetryProxyM4-' + [guid]::NewGuid().ToString('N'))
$names = @('RETRY_PROXY_CONFIG_JSON','RETRY_PROXY_UI_TEST_ROOT','CLAUDE_CONFIG_DIR','CODEX_HOME','RETRY_PROXY_CLAUDE_CLI','RETRY_PROXY_CODEX_CLI')
$saved = @{}; foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
$app=$null; $window=[IntPtr]::Zero; $passed=$false
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'window_verification.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class M4Capture {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
function Wait-For([scriptblock]$Condition,[string]$Message) {
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while(-not (& $Condition)) {
  if($app -and $app.HasExited){throw "App exited: $Message"}
  if($clock.Elapsed.TotalSeconds -gt 25){throw $Message}
  Start-Sleep -Milliseconds 100
 }
}
function Find-Control([string]$Value,[switch]$Name) {
 $uia=[Windows.Automation.AutomationElement]::FromHandle($window)
 $property=if($Name){[Windows.Automation.AutomationElement]::NameProperty}else{[Windows.Automation.AutomationElement]::AutomationIdProperty}
 return $uia.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new($property,$Value))
}
function Focus-App {
 $null=[M4Capture]::SetForegroundWindow($window)
 Wait-For { [M4Capture]::GetForegroundWindow() -eq $window } 'Test window did not receive foreground focus'
}
function Invoke-Control([string]$Value,[switch]$Name) {
 Focus-App
 $ready=@{Element=$null}
 Wait-For { $ready.Element=Find-Control $Value -Name:$Name; $null -ne $ready.Element -and $ready.Element.Current.IsEnabled -and !$ready.Element.Current.IsOffscreen } "Missing or unavailable $Value"
 $element=$ready.Element; $pattern=$null
 if($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke()}
 else {
  $element.SetFocus()
  $null=[RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]::new(13),[IntPtr]0x001C0001)
  $null=[RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]::new(13),[IntPtr]0xC01C0001)
 }
 Start-Sleep -Milliseconds 400
}
function Capture([string]$Name) {
 # 不截图也保留焦点与动画等待，避免 -NoScreenshot 改变向导及撤销的操作条件。
 Focus-App;Start-Sleep -Milliseconds 500
 if($NoScreenshot){return}
 if([M4Capture]::GetForegroundWindow() -ne $window){throw 'Screenshot foreground verification failed'}
 $bounds=[RetryProxyTrayVerification]::Bounds($window)
 $bmp=[Drawing.Bitmap]::new($bounds.Right-$bounds.Left,$bounds.Bottom-$bounds.Top)
 $graphics=[Drawing.Graphics]::FromImage($bmp)
 try {
  $graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$bmp.Size)
  $bmp.Save((Join-Path $root ".tmp/$Name.png"))
 } finally {$graphics.Dispose();$bmp.Dispose()}
}
function Start-App {
 $script:app=Start-Process -FilePath (Join-Path $runtime 'RetryProxy.exe') -WorkingDirectory $runtime -PassThru -RedirectStandardOutput (Join-Path $runtime 'stdout.txt') -RedirectStandardError (Join-Path $runtime 'stderr.txt')
 Wait-For { $script:window=[RetryProxyTrayVerification]::FindWindow($app.Id,'LLM Retry Proxy',$null);$window -ne [IntPtr]::Zero } 'Main window missing'
}
function Stop-App {
 $null=[RetryProxyTrayVerification]::PostMessage($window,0x0010,[UIntPtr]::Zero,[IntPtr]::Zero)
 if(-not $app.WaitForExit(25000)){throw 'App did not exit'}
 if($app.ExitCode -ne 0){throw "App exit code $($app.ExitCode)"}
 $script:app=$null
}
function Read-ClientText([string]$Path) {
 # 读取不阻止程序原子替换；只等待短暂共享/锁冲突，权限和格式错误照常使验收失败。
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while($true) {
  $stream=$null;$reader=$null
  try {
   $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
   $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$true)
   return $reader.ReadToEnd()
  } catch {
   $cause=$_.Exception
   while($cause.InnerException){$cause=$cause.InnerException}
   if($cause -isnot [IO.IOException] -or ($cause.HResult -band 0xffff) -notin @(32,33) -or $clock.ElapsedMilliseconds -ge 500){throw}
  } finally {
   if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}
  }
  Start-Sleep -Milliseconds 25
 }
}
function Claude-Config { Read-ClientText (Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json')|ConvertFrom-Json }
function Codex-Text { Read-ClientText (Join-Path $env:CODEX_HOME 'config.toml') }
try {
 New-Item -ItemType Directory -Path $runtime,(Join-Path $runtime 'User'),(Join-Path $root '.tmp') -Force|Out-Null
 Remove-Item Env:RETRY_PROXY_CONFIG_JSON -ErrorAction SilentlyContinue
 $env:RETRY_PROXY_UI_TEST_ROOT=$runtime
 $env:CLAUDE_CONFIG_DIR=Join-Path $runtime 'claude';$env:CODEX_HOME=Join-Path $runtime 'codex'
 $env:RETRY_PROXY_CLAUDE_CLI=Join-Path $runtime 'missing-claude.exe';$env:RETRY_PROXY_CODEX_CLI=Join-Path $runtime 'missing-codex.exe'
 New-Item -ItemType Directory -Path $env:CLAUDE_CONFIG_DIR,$env:CODEX_HOME|Out-Null
 foreach($port in @(28080,28081,28082)){$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$port);$listener.Start();$listener.Stop()}
 $claudeOriginal='{"env":{"ANTHROPIC_BASE_URL":"https://claude.fixture.invalid","ANTHROPIC_AUTH_TOKEN":"sk-claude-original","ANTHROPIC_MODEL":"claude-main-fixture[1M]","ANTHROPIC_DEFAULT_OPUS_MODEL":"role-opus","ANTHROPIC_DEFAULT_SONNET_MODEL":"role-sonnet","ANTHROPIC_DEFAULT_HAIKU_MODEL":"role-haiku","ANTHROPIC_DEFAULT_FABLE_MODEL":"role-fable","UNRELATED":"keep"},"permissions":{"allow":["Read"]},"apiKeyHelper":"never-run-this"}'
 $codexOriginal=@'
# Keep this comment and provider name
model_provider = "fixture-provider"
model = "codex-original"
model_context_window = 200000
model_auto_compact_token_limit = 160000
[model_providers.fixture-provider]
base_url = "https://codex.fixture.invalid/v1"
experimental_bearer_token = "sk-codex-original"
wire_api = "responses"
[unrelated]
keep = "unchanged"
'@
 [IO.File]::WriteAllText((Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json'),$claudeOriginal)
 [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'config.toml'),$codexOriginal)
 $proxy=@{schema_version=7;selected_route_id='codex-route';providers=@(
  @{id='fallback';name='Switch fixture';client_type='codex';base_url='https://switch.fixture.invalid';models=@{model='codex-switched'};keys=@(@{id='switch-key';name='Switch key';api_key='sk-codex-switch'})}
 );routes=@(
  @{id='codex-route';name='Codex';client_type='codex';listen_port=28080;current_provider_id='fallback';current_key_id='switch-key';local_token='0123456789abcdef0123456789abcdef';keepalive_enabled=$false},
  @{id='claude-route';name='Claude Code';client_type='claude';listen_port=28081;current_provider_id='';current_key_id='';local_token='abcdef0123456789abcdef0123456789';keepalive_enabled=$false}
 )}
 @{proxy=$proxy;commonConfig=@{clientSetupCompleted=$false;exitToTray=$false;isFirstRun=$false};otherConfig=@{uiCultureInfoName='zh-Hans'}}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $runtime 'User/config.json') -Encoding utf8
 Copy-Item -LiteralPath (Join-Path $root 'src/RetryProxy.App/User/I18n') -Destination (Join-Path $runtime 'User/I18n') -Recurse
 $ExePath=(Resolve-Path -LiteralPath $ExePath).ProviderPath
 Get-ChildItem -LiteralPath (Split-Path -Parent $ExePath) -File|Copy-Item -Destination $runtime
 Start-App
 Wait-For { $null -ne (Find-Control 'ImportName_claude') } 'Import step missing'
 Capture 'm4-setup-import'
 (Find-Control 'ImportName_claude').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Imported Claude')
 (Find-Control 'ImportName_codex').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Imported Codex')
 Invoke-Control '确认并继续' -Name
 Wait-For { $null -ne (Find-Control 'TakeoverClient_claude') } 'Takeover step missing'
 Capture 'm4-setup-takeover'
 Invoke-Control '确认并继续' -Name
 Wait-For { $null -ne (Find-Control '完成' -Name) } 'Finish step missing'
 Capture 'm4-setup-complete'
 Invoke-Control '完成' -Name
 Wait-For { (Claude-Config).env.ANTHROPIC_AUTH_TOKEN -eq 'abcdef0123456789abcdef0123456789' } 'Claude takeover failed'
 $claude=Claude-Config
 if($claude.env.ANTHROPIC_MODEL -ne 'retry-proxy-main[1M]' -or $claude.env.UNRELATED -ne 'keep' -or $claude.apiKeyHelper){throw 'Claude merge or independent main model failed'}
 $codex=Codex-Text
 if($codex -notmatch 'fixture-provider' -or $codex -notmatch '# Keep this comment' -or $codex -notmatch 'http://127.0.0.1:28080/v1'){throw 'Codex takeover did not preserve structure'}
 Invoke-Control 'SelectCodex'
 Invoke-Control 'switch-key'
 Wait-For { (Codex-Text) -match 'model\s*=\s*"codex-switched"' } 'Switch did not synchronize model'
 if((Codex-Text) -notmatch '0123456789abcdef0123456789abcdef'){throw 'Switch rewrote local credential'}
 Invoke-Control '撤销' -Name
 Wait-For { (Codex-Text) -match 'model\s*=\s*"codex-original"' } 'Undo did not restore model'
 Invoke-Control 'ProxySettings'
 Wait-For { $control=Find-Control 'ChannelListenPort'; $null -ne $control -and $control.Current.IsEnabled -and !$control.Current.IsOffscreen } 'Port settings drawer did not open'
 (Find-Control 'ChannelListenPort').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('28082')
 Invoke-Control 'DrawerSave'
 Invoke-Control '确认修改' -Name
 Wait-For { $null -eq (Find-Control 'ChannelListenPort') } 'Port settings did not finish'
 Wait-For { (Codex-Text) -match 'http://127.0.0.1:28082/v1' } 'Port change did not update client address'
 Invoke-Control 'ProxySettings'
 Invoke-Control 'ToggleProxy'
 Invoke-Control '确认停止' -Name
 Wait-For { (Codex-Text) -match 'sk-codex-original' } 'Manual stop did not restore direct connection'
 Invoke-Control 'ToggleProxy'
 Wait-For { (Codex-Text) -match '0123456789abcdef0123456789abcdef' } 'Manual restart did not take over again'
 Invoke-Control 'DrawerCancel'
 Wait-For { $null -eq (Find-Control 'ChannelListenPort') } 'Proxy settings drawer did not close'
 Invoke-Control '软件设置' -Name
 Wait-For { $null -ne (Find-Control 'ClientSetup') } 'Client settings missing'
 Capture 'm4-client-settings'
 Stop-App
 $claude=Claude-Config;$codex=Codex-Text
 if($claude.env.ANTHROPIC_AUTH_TOKEN -ne 'sk-claude-original' -or $claude.env.ANTHROPIC_MODEL -ne 'claude-main-fixture[1M]'){throw 'Claude exit restore failed'}
 if($codex -notmatch 'sk-codex-original' -or $codex -match '127.0.0.1:28082'){throw 'Codex exit restore failed'}
 $backups=Get-ChildItem -LiteralPath (Join-Path $runtime 'User/backup/client') -File -Recurse
 if(($backups|Where-Object Name -eq 'settings.json').Count -ne 1 -or ($backups|Where-Object Name -eq 'config.toml').Count -ne 1){throw 'Original backup missing'}
 if([IO.File]::ReadAllText(($backups|Where-Object Name -eq 'settings.json').FullName) -cne $claudeOriginal){throw 'Claude original backup changed'}
 if([IO.File]::ReadAllText(($backups|Where-Object Name -eq 'config.toml').FullName) -cne $codexOriginal){throw 'Codex original backup changed'}
 Start-App
 Wait-For { (Claude-Config).env.ANTHROPIC_AUTH_TOKEN -eq 'abcdef0123456789abcdef0123456789' } 'Automatic takeover after restart failed'
 if(Find-Control 'ImportName_claude'){throw 'Wizard repeated after completed'}
 Invoke-Control 'SelectClaude'
 $changed=Claude-Config;$changed.env.ANTHROPIC_BASE_URL='https://external.fixture.invalid'
 $changed|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $env:CLAUDE_CONFIG_DIR 'settings.json') -Encoding utf8
 Invoke-Control '软件设置' -Name
 Invoke-Control '供应商' -Name
 Wait-For { (Find-Control 'ClientState').Current.Name -eq '配置被改动' } 'External change not detected'
 Capture 'm4-external-change'
 Invoke-Control '软件设置' -Name
 Invoke-Control 'CancelTakeover_Codex'
 Invoke-Control '确认取消接管' -Name
 Wait-For { (Codex-Text) -match 'sk-codex-original' } 'Cancel takeover did not restore direct connection'
 Stop-App
 if((Claude-Config).env.ANTHROPIC_BASE_URL -ne 'https://external.fixture.invalid'){throw 'Exit overwrote external change'}
 Start-App
 Wait-For { (Find-Control 'ProxyState').Current.Name -eq '运行中' } 'Third startup did not run'
 if((Codex-Text) -match '0123456789abcdef0123456789abcdef'){throw 'Canceled takeover was re-enabled on restart'}
 Stop-App
 $passed=$true
 Write-Host 'PASS: three-step setup, import/rename, isolated takeover, independent main model, model switch/undo, port change, manual stop/restart, original backups, exit direct restore, restart takeover, persistent cancellation, external change detection and preservation.'
}
catch {
 if($window -ne [IntPtr]::Zero -and $app -and !$app.HasExited){
  try{Capture 'm4-failure'}catch{}
  # 仅导出本脚本的隔离测试窗口，保留失败时的提示，区分业务拒绝与控件未就绪。
  try {
   $uia=[Windows.Automation.AutomationElement]::FromHandle($window)
   $uia.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
    ForEach-Object { if($_.Current.Name){ '{0}: {1}' -f $_.Current.ControlType.ProgrammaticName,$_.Current.Name } } |
    Set-Content -LiteralPath (Join-Path $runtime 'ui-failure.txt') -Encoding utf8
  } catch {}
 }
 foreach($log in @('harness-error.txt','stderr.txt')){$errorFile=Join-Path $runtime $log;if(Test-Path -LiteralPath $errorFile){Get-Content -LiteralPath $errorFile}}
 throw
}
finally {
 if($app -and !$app.HasExited){$app.Kill();$app.WaitForExit(5000)|Out-Null}
 foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
 if($passed){
  $target=Get-Item -LiteralPath $runtime
  if(($target.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $target.FullName -ne [IO.Path]::GetFullPath($runtime)){throw 'Unexpected cleanup path'}
  Remove-Item -LiteralPath $runtime -Recurse -Force
 } else {Write-Host "Test directory retained: $runtime"}
}
