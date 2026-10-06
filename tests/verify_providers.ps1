param(
    [string]$ExePath = (Join-Path $PSScriptRoot '../src/RetryProxy.App/bin/x64/Debug/net9.0-windows10.0.22621.0/RetryProxy.exe'),
    [switch]$UseCurrentDesktop,
    [switch]$VerifyTrayPointer
)
if ($VerifyTrayPointer -and -not $UseCurrentDesktop) { throw 'Tray pointer checks require -UseCurrentDesktop.' }
# M3 供应商界面验收。默认私有桌面；所有配置和凭据仅为测试数据，不读写真实客户端配置。
# 显式选择当前桌面时才截图，避免 PrintWindow 在私有桌面返回白图而误认为视觉验收成功。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime_files.ps1')
$root = Split-Path -Parent $PSScriptRoot
$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$runtime = Join-Path ([IO.Path]::GetTempPath()) ('RetryProxyM3-' + [guid]::NewGuid().ToString('N'))
$envNames = @('RETRY_PROXY_CONFIG_JSON','CLAUDE_CONFIG_DIR','CODEX_HOME','RETRY_PROXY_CLAUDE_CLI','RETRY_PROXY_CODEX_CLI')
$saved = @{}
foreach ($name in $envNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$app = $null
$passed = $false
$window = [IntPtr]::Zero
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -Path (Join-Path $root 'tests\window_verification.cs')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class M3Capture {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
 public struct POINT { public int X; public int Y; }
}
'@
function Wait-For([scriptblock]$Condition,[string]$Message) {
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while (-not (& $Condition)) {
  if ($app -and $app.HasExited) { throw "App exited: $Message" }
  if ($clock.Elapsed.TotalSeconds -gt 20) { throw $Message }
  Start-Sleep -Milliseconds 100
 }
}
function Find-Control([string]$Value,[switch]$Name) {
 $uia=[Windows.Automation.AutomationElement]::FromHandle($window)
 $property=if($Name){[Windows.Automation.AutomationElement]::NameProperty}else{[Windows.Automation.AutomationElement]::AutomationIdProperty}
 $condition=[Windows.Automation.PropertyCondition]::new($property,$Value)
 return $uia.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke-Control([string]$Value,[switch]$Name) {
 Wait-For { $null -ne (Find-Control $Value -Name:$Name) } "Missing $Value"
 $element=Find-Control $Value -Name:$Name
 $pattern=$null
 foreach($type in @([Windows.Automation.InvokePattern]::Pattern,[Windows.Automation.TogglePattern]::Pattern,[Windows.Automation.SelectionItemPattern]::Pattern)) {
  if($element.TryGetCurrentPattern($type,[ref]$pattern)) {
   if($type -eq [Windows.Automation.InvokePattern]::Pattern){$pattern.Invoke()}
   elseif($type -eq [Windows.Automation.TogglePattern]::Pattern){$pattern.Toggle()}
   else{$pattern.Select()}
   Start-Sleep -Milliseconds 300
   return
  }
 }
 $element.SetFocus()
 $null=[RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]13,[IntPtr]0x001C0001)
 $null=[RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]13,[IntPtr]0xC01C0001)
 Start-Sleep -Milliseconds 400
}
function Find-Menu([string]$Name) {
 foreach($handle in [RetryProxyTrayVerification]::WindowsForProcess($app.Id)) {
  if(-not [RetryProxyTrayVerification]::IsWindowVisible($handle)){continue}
  $element=[Windows.Automation.AutomationElement]::FromHandle($handle)
  $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name)
  foreach($item in $element.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)) {
   if($item.Current.ControlType -eq [Windows.Automation.ControlType]::MenuItem){return $item}
  }
 }
 return $null
}
function Click-Navigation([string]$Name,[switch]$Keyboard) {
 # 使用实际导航项的鼠标/键盘入口，避免直接换内容绕过关闭保护。
 $uia=[Windows.Automation.AutomationElement]::FromHandle($window)
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name)
 $item=@($uia.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) |
  Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::DataItem -and !$_.Current.IsOffscreen })[0]
 if(!$item){throw "找不到导航项：$Name"}
 if($Keyboard -or !$UseCurrentDesktop) {
  $item.SetFocus()
  $null=[RetryProxyTrayVerification]::PostMessage($window,0x0100,[UIntPtr]13,[IntPtr]0x001C0001)
  $null=[RetryProxyTrayVerification]::PostMessage($window,0x0101,[UIntPtr]13,[IntPtr]0xC01C0001)
 } else {
  $null=[M3Capture]::SetForegroundWindow($window)
  Wait-For { [M3Capture]::GetForegroundWindow() -eq $window } '导航测试窗口未取得前台焦点'
  $bounds=$item.Current.BoundingRectangle
  $null=[M3Capture]::SetCursorPos([int]($bounds.Left+$bounds.Width/2),[int]($bounds.Top+$bounds.Height/2))
  [M3Capture]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
  [M3Capture]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
 }
 Start-Sleep -Milliseconds 500
}
function Cancel-Discard {
 $uia=[Windows.Automation.AutomationElement]::FromHandle($window)
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'取消')
 $button=@($uia.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) |
  Where-Object { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.IsEnabled -and $_.Current.AutomationId -ne 'DrawerCancel' })[0]
 if(!$button){throw '未找到放弃修改确认框的取消按钮'}
 $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { $null -eq (Find-Control '放弃修改' -Name) } '取消确认后对话框仍显示'
}
function Assert-DrawerClosed {
 Wait-For { $null -eq (Find-Control 'DrawerCancel') } '切页后抽屉仍显示'
 if(Find-Control '放弃修改' -Name){throw '未修改的抽屉不应弹出确认'}
}
function Check-HomeNavigation {
 # 真实点击首页卡片，再通过侧栏返回；覆盖默认页、全部入口及缓存后的再次导航。
 Wait-For { $null -ne (Find-Control 'HomeProvidersCard') } '启动时未显示首页'
 if(!(Find-Control 'HomeNavigation')){throw '首页标签缺失'}
 Capture 'm3-home'
 foreach($case in @(
  @('HomeProvidersCard','CurrentProviderKey'),
  @('HomeStatisticsCard','StatisticsTabOverview'),
  @('HomePreparationCard','AddPreparation'),
  @('HomeDiagnosticsCard','DiagnosticSearch'),
  @('HomeLogsCard','LogRows'),
  @('HomeSettingsCard','SwitchAppearance')
 )) {
  Invoke-Control $case[0]
  Wait-For { $null -ne (Find-Control $case[1]) } "首页快捷入口未打开目标页：$($case[0])"
  Click-Navigation '首页'
  Wait-For { $null -ne (Find-Control 'HomeProvidersCard') } '侧栏未返回首页'
 }
 # 小窗口仍须能访问最后一张卡片，不能只验证控件存在。
 $bounds=[RetryProxyTrayVerification]::Bounds($window)
 [RetryProxyTrayVerification]::SetBounds($window,$bounds.Left,$bounds.Top,760,520)
 Start-Sleep -Milliseconds 500
 Capture 'm3-home-compact'
 (Find-Control 'HomeSettingsCard').SetFocus()
 Wait-For { !(Find-Control 'HomeSettingsCard').Current.IsOffscreen } '小窗口无法滚动到首页最后一张卡片'
 Invoke-Control 'HomeSettingsCard'
 Wait-For { $null -ne (Find-Control 'SwitchAppearance') } '小窗口首页入口无法打开设置'
 [RetryProxyTrayVerification]::SetBounds($window,$bounds.Left,$bounds.Top,$bounds.Right-$bounds.Left,$bounds.Bottom-$bounds.Top)
 Click-Navigation '首页'
 Wait-For { $null -ne (Find-Control 'HomeProvidersCard') } '调整窗口后未返回首页'
 Invoke-Control 'HomeProvidersCard'
}
function Check-DrawerNavigation {
 # 所有左侧主菜单和页脚入口都必须在切页前关闭无修改的草稿。
 foreach($target in @('首页','统计','一键准备','请求诊断','运行日志','软件设置','关于')) {
  Invoke-Control 'AddProvider'
  Wait-For { $null -ne (Find-Control 'NameBox') } '新增供应商抽屉未打开'
  Click-Navigation $target
  Wait-For { $null -eq (Find-Control 'AddProvider') } "导航输入未切换页面：$target"
  Assert-DrawerClosed
  if(Find-Control 'AddProvider'){throw "未切换到目标页：$target"}
  Click-Navigation '供应商'
  Wait-For { $null -ne (Find-Control 'AddProvider') } '未返回供应商页'
 }
 Invoke-Control 'AddProvider'
 Set-Field 'NameBox' '切页取消后保留的草稿'
 Click-Navigation '首页'
 Wait-For { $null -ne (Find-Control '放弃修改' -Name) } '切页未提示未保存修改'
 if(!(Find-Control 'AddProvider') -or (Find-Control 'HomeProvidersCard')){throw '确认前已经离开供应商页'}
 Capture 'drawer-navigation-confirm'
 Cancel-Discard
 if((Find-Control 'NameBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '切页取消后保留的草稿'){throw '取消切页丢失输入'}
 if(!(Find-Control 'AddProvider')){throw '取消切页后原页面未保留'}
 Capture 'drawer-navigation-canceled'
 # 保存校验失败后的草稿仍需确认，键盘切页遵守同一保护。
 Invoke-Control 'DrawerSave'
 Click-Navigation '首页' -Keyboard
 Wait-For { $null -ne (Find-Control '放弃修改' -Name) } '保存失败后键盘切页未确认'
 Invoke-Control '放弃修改' -Name
 Assert-DrawerClosed
 Capture 'drawer-navigation-discarded'
 Click-Navigation '供应商'
 Wait-For { $null -ne (Find-Control 'AddProvider') } '确认放弃后未能返回供应商页'
 if(Find-Control '切页取消后保留的草稿' -Name){throw '放弃草稿被保存到供应商列表'}
 # 父草稿有修改、子 Key 没修改时，整体确认；取消不能先退回父层。
 Invoke-Control 'AddProvider'
 Set-Field 'NameBox' '嵌套父草稿'
 Invoke-Control '编辑' -Name
 Wait-For { $null -ne (Find-Control 'SecretBox') } '嵌套 Key 未打开'
 Click-Navigation '运行日志'
 Wait-For { $null -ne (Find-Control '放弃修改' -Name) } '嵌套编辑遗漏父草稿修改'
 Cancel-Discard
 if(!(Find-Control 'SecretBox')){throw '取消切页后子抽屉已被关闭'}
 if((Find-Control 'NameBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '默认'){throw '取消切页后子草稿输入变化'}
 Click-Navigation '请求诊断'
 Invoke-Control '放弃修改' -Name
 Assert-DrawerClosed
 Click-Navigation '供应商'
 Wait-For { $null -ne (Find-Control 'AddProvider') } '嵌套放弃后未返回供应商页'
 if(Find-Control '嵌套父草稿' -Name){throw '嵌套草稿被意外保存'}
 # 代理设置和准备任务同样保护草稿，放弃不得写入配置。
 Invoke-Control 'ProxySettings'
 $original=(Find-Control 'ChannelRetries').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 Set-Field 'ChannelRetries' '19'
 Click-Navigation '一键准备'
 Invoke-Control '放弃修改' -Name
 Assert-DrawerClosed
 Invoke-Control 'AddPreparation'
 Wait-For { $null -ne (Find-Control 'PreparationDrawer') } '准备抽屉未打开'
 Click-Navigation '供应商' -Keyboard
 Assert-DrawerClosed
 Wait-For { $null -ne (Find-Control 'ProxySettings') } '未返回供应商页'
 Invoke-Control 'ProxySettings'
 if((Find-Control 'ChannelRetries').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $original){throw '切页放弃仍改写代理设置'}
 Invoke-Control 'DrawerCancel'
 Click-Navigation '一键准备'
 Invoke-Control 'AddPreparation'
 Set-Field 'PreparationModel' '未保存的准备模型'
 Click-Navigation '供应商'
 Wait-For { $null -ne (Find-Control '放弃修改' -Name) } '准备草稿切页未确认'
 Cancel-Discard
 if((Find-Control 'PreparationModel').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '未保存的准备模型'){throw '准备草稿丢失'}
 Click-Navigation '供应商'
 Invoke-Control '放弃修改' -Name
 Assert-DrawerClosed
 Wait-For { $null -ne (Find-Control 'AddProvider') } '准备放弃后未返回供应商页'
 Write-Host 'PASS: 全部左侧导航关闭、确认前保留原页、取消保留输入、嵌套整体放弃、保存失败、鼠标与键盘、代理与准备草稿保护。'
}
function Check-TrayPointer {
 # 用真实鼠标沿父条目中心横移；UIA 的 Expand/Invoke 会绕过原缺陷，不能代替此检查。
 $tray=[RetryProxyTrayVerification]::FindWindowByTitlePrefix($app.Id,'wpfui_th_')
 $original=[M3Capture+POINT]::new()
 $null=[M3Capture]::GetCursorPos([ref]$original)
 Add-Type -AssemblyName System.Windows.Forms
 $area=[Windows.Forms.Screen]::PrimaryScreen.WorkingArea
 $positions=@(
  @(($area.Right-4),($area.Bottom-4)), @(($area.Left+4),($area.Bottom-4)),
  @(($area.Left+4),($area.Top+4)), @(($area.Right-4),($area.Top+4))
 )
 $results=@()
 try {
  foreach($position in $positions) {
   $null=[M3Capture]::SetCursorPos($position[0],$position[1])
   $null=[RetryProxyTrayVerification]::PostMessage($tray,2048,[UIntPtr]1,[IntPtr]0x0204)
   $null=[RetryProxyTrayVerification]::PostMessage($tray,2048,[UIntPtr]1,[IntPtr]0x0205)
   Wait-For { $null -ne (Find-Menu 'Codex：Codex Fixture · Key E') } 'Pointer: tray menu missing'
   foreach($clientName in @('Codex：Codex Fixture · Key E','Claude Code：Claude Fixture · Default')) {
    $client=Find-Menu $clientName
    $parent=$client.Current.BoundingRectangle
    $x=[int]($parent.Left+$parent.Width/2);$y=[int]($parent.Top+$parent.Height/2)
    $null=[M3Capture]::SetCursorPos($x,$y)
    $keyName=if($clientName.StartsWith('Codex')){'Codex Fixture · Key A'}else{'Claude Fixture · Default'}
    Wait-For { $null -ne (Find-Menu $keyName) } "Pointer: hover did not open $clientName"
    Start-Sleep -Milliseconds 500
    $key=Find-Menu $keyName
    $first=$key.Current.BoundingRectangle
    $last=if($clientName.StartsWith('Codex')){(Find-Menu 'Codex Fixture · Key E').Current.BoundingRectangle}else{$first}
    Write-Host "Pointer geometry: $clientName at $($position -join ','); parent=$parent; first=$first; last=$last"
    if($y -lt $first.Top -or $y -gt $last.Bottom) { throw "Pointer: submenu is detached vertically from $clientName at $($position -join ',')" }
    $targetX=[int]($first.Left+$first.Width/2)
    $step=if($targetX -lt $x){-4}else{4}
    while([Math]::Abs($targetX-$x) -gt 4) {
     $x+=$step;$null=[M3Capture]::SetCursorPos($x,$y);Start-Sleep -Milliseconds 10
    }
    $null=[M3Capture]::SetCursorPos($targetX,$y)
    Start-Sleep -Milliseconds 800
    if($null -eq (Find-Menu $keyName)) { throw "Pointer: submenu disappeared during horizontal movement: $clientName" }
    $null=[M3Capture]::SetCursorPos($targetX,[int]($first.Top+$first.Height/2))
    Start-Sleep -Milliseconds 500
    if($null -eq (Find-Menu $keyName)) { throw "Pointer: submenu disappeared while choosing a Key: $clientName" }
    $results+=@{client=$clientName;corner=$position;parent=$parent.ToString();firstKey=$first.ToString();passed=$true}
    if($position[0] -eq $positions[0][0] -and $position[1] -eq $positions[0][1] -and $clientName.StartsWith('Codex')) {
     $left=[int][Math]::Min($parent.Left,$first.Left)-8
     $top=[int][Math]::Min($parent.Top,$first.Top)-8
     $right=[int][Math]::Max($parent.Right,$first.Right)+8
     $bottom=[int][Math]::Max($parent.Bottom,$last.Bottom)+8
     $bmp=[Drawing.Bitmap]::new($right-$left,$bottom-$top)
     $graphics=[Drawing.Graphics]::FromImage($bmp)
     try {$graphics.CopyFromScreen($left,$top,0,0,$bmp.Size);$bmp.Save((Join-Path $root '.tmp\tray-pointer-bottom-right.png'))}
     finally {$graphics.Dispose();$bmp.Dispose()}
    }
    # 先回到所属父行，再移动到另一个客户端，验证正常的悬停切换仍可用。
    $null=[M3Capture]::SetCursorPos([int]($parent.Left+$parent.Width/2),$y)
   }
   [M3Capture]::keybd_event(0x1B,0,0,[UIntPtr]::Zero)
   [M3Capture]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
   [M3Capture]::keybd_event(0x1B,0,0,[UIntPtr]::Zero)
   [M3Capture]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
   Start-Sleep -Milliseconds 300
  }
  $results|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root '.tmp\tray-pointer-results.json') -Encoding utf8
  Write-Host 'PASS: 8 real-pointer submenu traversals across both clients and all four screen corners.'
 }
 finally {$null=[M3Capture]::SetCursorPos($original.X,$original.Y)}
}
function Check-TraySwitch {
 $tray=[RetryProxyTrayVerification]::FindWindowByTitlePrefix($app.Id,'wpfui_th_')
 if($tray -eq [IntPtr]::Zero){throw 'Tray window missing'}
 $null=[RetryProxyTrayVerification]::PostMessage($tray,2048,[UIntPtr]1,[IntPtr]0x0203)
 Wait-For { -not [RetryProxyTrayVerification]::IsWindowVisible($window) } 'Tray did not hide window'
 $null=[RetryProxyTrayVerification]::PostMessage($tray,2048,[UIntPtr]1,[IntPtr]0x0204)
 $null=[RetryProxyTrayVerification]::PostMessage($tray,2048,[UIntPtr]1,[IntPtr]0x0205)
 Start-Sleep -Milliseconds 500
 Wait-For { $null -ne (Find-Menu 'Codex：Codex Fixture · Key E') } 'Dynamic client submenu missing'
 $client=Find-Menu 'Codex：Codex Fixture · Key E'
 $client.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Wait-For { $null -ne (Find-Menu 'Codex Fixture · Key B') } 'Tray key menu missing'
 $key=Find-Menu 'Codex Fixture · Key B'
 if($VerifyTrayPointer) {
  Start-Sleep -Milliseconds 500
  $bounds=$key.Current.BoundingRectangle
  $null=[M3Capture]::SetCursorPos([int]($bounds.Left+$bounds.Width/2),[int]($bounds.Top+$bounds.Height/2))
  [M3Capture]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
  [M3Capture]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
 } else {$key.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
 Start-Sleep -Milliseconds 400
 $null=[RetryProxyTrayVerification]::PostMessage($tray,2048,[UIntPtr]1,[IntPtr]0x0203)
 Wait-For { [RetryProxyTrayVerification]::IsWindowVisible($window) } 'Tray did not restore window'
 Wait-For { (Find-Control 'CurrentProviderKey').Current.Name -like '*Key B*' } 'Hidden tray switch failed'
 Invoke-Control 'key-A'
}
function Set-Field([string]$Id,[string]$Value) {
 $element=Find-Control $Id
 if(!$element){throw "Missing field $Id"}
 $pattern=$element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
 $pattern.SetValue($Value)
}
function Capture([string]$Name) {
 if(-not $UseCurrentDesktop){return}
 Start-Sleep -Milliseconds 600
 $bounds=[RetryProxyTrayVerification]::Bounds($window)
 $bmp=[Drawing.Bitmap]::new($bounds.Right-$bounds.Left,$bounds.Bottom-$bounds.Top)
 $graphics=[Drawing.Graphics]::FromImage($bmp)
 $null=[M3Capture]::SetForegroundWindow($window)
 Start-Sleep -Milliseconds 500
 if([M3Capture]::GetForegroundWindow() -ne $window){throw 'Test window is not in foreground; screenshot canceled'}
 $graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$bmp.Size)
 $bmp.Save((Join-Path $root ".tmp\$Name.png"))
 $graphics.Dispose();$bmp.Dispose()
}
try {
 New-Item -ItemType Directory -Path (Join-Path $root '.tmp') -Force | Out-Null
 New-Item -ItemType Directory -Path $runtime | Out-Null
 Copy-RetryProxyRuntime -ExePath $ExePath -DestinationDirectory $runtime
 New-Item -ItemType Directory -Path (Join-Path $runtime 'User') | Out-Null
 Copy-Item -LiteralPath (Join-Path $root 'src\RetryProxy.App\User\I18n') -Destination (Join-Path $runtime 'User\I18n') -Recurse
 $env:CLAUDE_CONFIG_DIR=Join-Path $runtime 'claude'
 $env:CODEX_HOME=Join-Path $runtime 'codex'
 $env:RETRY_PROXY_CLAUDE_CLI=Join-Path $runtime 'no-claude.exe'
 $env:RETRY_PROXY_CODEX_CLI=Join-Path $runtime 'no-codex.exe'
 foreach($directory in @($env:CLAUDE_CONFIG_DIR,$env:CODEX_HOME)){New-Item -ItemType Directory -Path $directory|Out-Null}
 foreach($port in @(28080,28081)) {
  $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$port)
  $listener.Start();$listener.Stop()
 }
 $keys=@()
 foreach($letter in @('A','B','C','D','E')){$keys+=@{id="key-$letter";name="Key $letter";api_key="sk-fake-fixture-$letter"}}
 $config=@{
  schema_version=7;selected_route_id='codex-route';providers=@(
   @{id='codex-provider';client_type='codex';name='Codex Fixture';base_url='http://127.0.0.1:28999';models=@{model='test-model'};keys=$keys},
   @{id='claude-provider';client_type='claude';name='Claude Fixture';base_url='http://127.0.0.1:28999';keys=@(@{id='claude-key';name='Default';api_key='sk-claude-fixture'})}
  );routes=@(
   @{id='codex-route';name='Codex';client_type='codex';listen_port=28080;current_provider_id='codex-provider';current_key_id='key-E';local_token='0123456789abcdef0123456789abcdef'},
   @{id='claude-route';name='Claude Code';client_type='claude';listen_port=28081;current_provider_id='claude-provider';current_key_id='claude-key';local_token='abcdef0123456789abcdef0123456789'}
  )
 }
 $env:RETRY_PROXY_CONFIG_JSON=$config|ConvertTo-Json -Depth 12 -Compress
 $app=if($UseCurrentDesktop){Start-Process -FilePath (Join-Path $runtime 'RetryProxy.exe') -WorkingDirectory $runtime -PassThru}
 else{[RetryProxyTrayVerification]::StartPrivateProcess((Join-Path $runtime 'RetryProxy.exe'),$runtime)}
 Wait-For { $script:window=[RetryProxyTrayVerification]::FindWindow($app.Id,'LLM Retry Proxy',$null); $window -ne [IntPtr]::Zero } 'Window not created'
 Check-HomeNavigation
 Wait-For { $null -ne (Find-Control 'CurrentProviderKey') } 'Provider page not loaded'
 if((Find-Control 'CurrentProviderKey').Current.Name -notlike '*Key E*'){throw 'Current key missing'}
 if(!(Find-Control 'key-E')){throw 'Collapsed current Key E missing'}
 foreach($hidden in @('key-A','key-B','key-C','key-D')){if(Find-Control $hidden){throw "Collapsed noncurrent $hidden unexpectedly visible"}}
 Wait-For { (Find-Control 'FoldSummary_codex-provider').Current.Name -like '*还有 4 个 Key*' } 'Collapsed key summary missing'
 Capture 'm3-providers'
 Invoke-Control 'Fold_codex-provider'
 Wait-For { $null -ne (Find-Control 'key-A') } 'Expand did not reveal all keys'
 if(-not (Find-Control 'key-D')){throw 'Expanded Key D missing'}
 Invoke-Control 'key-B'
 Wait-For { (Find-Control 'CurrentProviderKey').Current.Name -like '*Key B*' } 'Switch did not update page'
 Invoke-Control '撤销' -Name
 Wait-For { (Find-Control 'CurrentProviderKey').Current.Name -like '*Key E*' } 'Undo did not restore key'
 if($VerifyTrayPointer){Check-TrayPointer}
 Check-TraySwitch
 Invoke-Control 'SelectClaude'
 Wait-For { (Find-Control 'CurrentProviderKey').Current.Name -like '*Claude Fixture*' } 'Client selection failed'
 Invoke-Control '统计' -Name
 Wait-For { $null -ne (Find-Control 'OverviewTotalRequests') } 'Statistics failed'
 Capture 'm3-statistics'
 Invoke-Control '供应商' -Name
 Wait-For { (Find-Control 'CurrentProviderKey').Current.Name -like '*Claude Fixture*' } 'Shared client selection was lost'
 Invoke-Control 'SelectCodex'
 Check-DrawerNavigation
 Invoke-Control 'ProxySettings'
 Wait-For { $null -ne (Find-Control 'ChannelListenPort') } 'Settings drawer did not open'
 Capture 'm3-proxy-drawer'
 $panel=Find-Control 'BodyScroll'
 if([Math]::Abs($panel.Current.BoundingRectangle.Width-399) -gt 2){throw "Drawer width: $($panel.Current.BoundingRectangle.Width)"}
 $original=(Find-Control 'ChannelRetries').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 Set-Field 'ChannelRetries' '17'
 Invoke-Control 'DrawerCancel'
 Invoke-Control '放弃修改' -Name
 Wait-For { $null -eq (Find-Control 'ChannelListenPort') } 'Discard did not close drawer'
 Invoke-Control 'ProxySettings'
 if((Find-Control 'ChannelRetries').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $original){throw 'Discard changed saved value'}
 Set-Field 'ChannelListenPort' '0'
 Invoke-Control 'DrawerSave'
 if(!(Find-Control 'ChannelListenPort')){throw 'Invalid port was saved'}
 Set-Field 'ChannelListenPort' '28080'
 Set-Field 'ChannelRetries' '17'
 Invoke-Control 'DrawerSave'
 Wait-For { $null -eq (Find-Control 'ChannelListenPort') } 'Valid settings not saved'
 Invoke-Control 'ProxySettings'
 if((Find-Control 'ChannelRetries').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '17'){throw 'Saved value was lost'}
 [RetryProxyTrayVerification]::SetBounds($window,100,100,760,520)
 Start-Sleep -Milliseconds 800
 Capture 'm3-compact-drawer'
 $width=(Find-Control 'BodyScroll').Current.BoundingRectangle.Width
 if($width -ge 640 -or $width -le 400){throw "Compact drawer not full width: $width"}
 Invoke-Control 'DrawerCancel'
 Invoke-Control 'AddProvider'
 Wait-For { $null -ne (Find-Control 'NameBox') } 'Provider drawer missing'
 Capture 'm3-provider-editor'
 Set-Field 'NameBox' 'UI Fixture'
 Set-Field 'UrlBox' 'http://127.0.0.1:28999'
 Invoke-Control '编辑' -Name
 Wait-For { $null -ne (Find-Control 'SecretBox') } 'Nested key drawer missing'
 Invoke-Control 'ShowSecretCheck'
 Set-Field 'VisibleSecretBox' 'sk-ui-fixture-key'
 Invoke-Control 'DrawerSave'
 Wait-For { $null -eq (Find-Control 'SecretBox') } 'Nested key did not return to parent'
 if((Find-Control 'NameBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'UI Fixture'){throw 'Parent draft lost'}
 Invoke-Control 'DrawerSave'
 Wait-For { $null -eq (Find-Control 'NameBox') } 'Provider draft did not save'
 if(!(Find-Control 'UI Fixture' -Name)){throw 'New provider card missing'}
 $passed = $true
 Write-Host 'PASS: home startup, all home shortcuts, compact home, drawer navigation to home, provider page, folding, switch/undo, hidden tray switching, shared client selection, statistics, draft discard, validation, saved settings, compact drawer, nested key and provider creation.'
}
catch {
 if($window -ne [IntPtr]::Zero){
  try { Capture 'm3-failure';[Windows.Automation.AutomationElement]::FromHandle($window).FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|ForEach-Object {"$($_.Current.ControlType.ProgrammaticName) | $($_.Current.AutomationId) | $($_.Current.Name)"}|Set-Content -LiteralPath (Join-Path $root '.tmp\m3-ui-tree.txt') -Encoding utf8 } catch {}
 }
 throw
}
finally {
 if($app -and !$app.HasExited){$app.Kill();$app.WaitForExit(5000)|Out-Null}
 [RetryProxyTrayVerification]::ClosePrivateDesktop()
 foreach($name in $envNames){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
 if($passed){
  $resolved=(Resolve-Path -LiteralPath $runtime).ProviderPath
  if($resolved -ne [IO.Path]::GetFullPath($runtime) -or ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected test cleanup path'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 } else { Write-Host "Test directory retained: $runtime" }
}
