> 本文由分析子代理于 2026-09-21 生成，作为《PRD-CSharp-重构.md》附录 C 的逐条对照依据。

# BetterGenshinImpact（BetterGI）UI/UX 框架迁移分析报告

> 目标：把 `D:\better-genshin-impact` 的 WPF UI/UX 框架"照搬"为一个 LLM 反向代理桌面工具的底座，剔除全部游戏内容。以下所有路径均为绝对路径，类名以源码为准。

---

## 一、解决方案与工程结构

解决方案文件：`D:\better-genshin-impact\BetterGenshinImpact.sln`
配置：Debug/Release × Any CPU/x64，全部映射到 x64（无 AnyCPU 实际输出）。含两个解决方案文件夹 `Test`、`Build`。

| 工程 | 路径 | 作用 | 迁移结论 |
|---|---|---|---|
| BetterGenshinImpact | `D:\better-genshin-impact\BetterGenshinImpact\BetterGenshinImpact.csproj` | 主 WPF 程序（AssemblyName `BetterGI`） | **保留并大幅裁剪**（详见后文各目录清单） |
| Fischless.GameCapture | `D:\better-genshin-impact\Fischless.GameCapture\` | 游戏画面捕获（BitBlt、DwmSharedSurface、Windows Graphics Capture、`GameCaptureFactory`、`IGameCapture`） | **删除** |
| Fischless.HotkeyCapture | `D:\better-genshin-impact\Fischless.HotkeyCapture\Fischless.HotkeyCapture.csproj` | 全局热键（`Hotkey.cs`、`HotkeyHolder.cs`、`HotkeyHook.cs`、`KeyPressedEventArgs.cs`、`SystemErrorCodes.cs`；基于 `RegisterHotKey`，依赖 Vanara.PInvoke.User32，TFM `net8.0-windows10.0.22621.0`，UseWindowsForms） | **可选保留**（若代理工具需要全局热键，例如"显示/隐藏主窗口"、"暂停代理"） |
| Fischless.WindowsInput | `D:\better-genshin-impact\Fischless.WindowsInput\` | InputSimulator / SendInput 键鼠模拟，自带 `LICENSE.txt` | **删除** |
| BetterGenshinImpact.Test | `D:\better-genshin-impact\Test\BetterGenshinImpact.Test\` | WPF 手工测试壳（游戏识别调试） | **删除** |
| BetterGenshinImpact.UnitTest | `D:\better-genshin-impact\Test\BetterGenshinImpact.UnitTest\` | xunit 单测；`Assets` 为 git submodule（huiyadanli/BetterGI.UnitTest.Assets） | **删除内容、保留工程骨架**（重新建单测工程更省事；注意 `.gitmodules` 要一并清理） |
| BetterGenshinImpact.I18nSync | `D:\better-genshin-impact\Build\BetterGenshinImpact.I18nSync\`（`Program.cs`、`I18nScanner.cs`、`I18nJsonSynchronizer.cs`） | 控制台工具：扫描 XAML/C# 中 `{i18n:T ...}` 键并同步到 `User/I18n/*.json` | **保留**（配合自定义 i18n 方案使用） |

仓库根目录其它内容：`AGENTS.md`（编码规范，见第三节）、`LICENSE`（GPL-3.0）、`README.md`、`Docs\`、`.github\workflows\`、`.cnb.yml`、`CodeMaid.config`、`Settings.XamlStyler`、`BetterGenshinImpact.sln.DotSettings`、`.agents`、`.cnb`。`Docs\` 与 `README.md` 全部是游戏内容，删除。

---

## 二、BetterGenshinImpact.csproj 详解

文件：`D:\better-genshin-impact\BetterGenshinImpact\BetterGenshinImpact.csproj`

### 2.1 基本属性
- `OutputType` WinExe；`AssemblyName` `BetterGI`；`Version` `0.65.1-alpha.2`
- `TargetFramework` **`net8.0-windows10.0.22621.0`**（WinRT 投影可用，因此能用 `Microsoft.Toolkit.Uwp.Notifications` 和 `Windows.System`）
- `UseWPF` + `UseWindowsForms` 同时开启（WinForms 用于 `NotifyIcon` 兜底、`MessageBox` 兜底、`Screen` 等）
- `Nullable` enable，`LangVersion` 12，`AllowUnsafeBlocks` true，`Platforms` x64，`DebugType` embedded
- `ApplicationIcon` `Resources\Images\logo.ico`
- **未启用 AOT**（WPF 不支持 NativeAOT）；**未启用 ReadyToRun**
- 发布配置见 `D:\better-genshin-impact\BetterGenshinImpact\Properties\PublishProfiles\FolderProfile.pubxml`：Release、x64、`RuntimeIdentifier` win-x64、**`SelfContained=false`（依赖机器已装 .NET 8 Desktop Runtime）**、**`PublishSingleFile=true`**、`PublishReadyToRun=false`、`PublishDir bin\x64\Release\net8.0-windows10.0.22621.0\publish\win-x64\`
- `Properties\launchSettings.json` 存在（调试启动参数）
- 资源包含：`Resources\Images\*.jpg|*.png|*.ico`、`Resources\Images\Anniversary\*`、`Resources\Fonts\*.ttf`；大量 `<None Update="GameTask\**\Assets...">` 复制规则（游戏资产，全部删除）
- `ProjectReference` 指向三个 Fischless 工程（按第一节裁剪）

### 2.2 NuGet 全量清单与标注

**A. UI/UX 框架必需（保留）**

| 包 | 版本 | 用途 |
|---|---|---|
| WPF-UI | 4.3.0 | FluentWindow、NavigationView、TitleBar、Snackbar、CardControl/CardExpander 等全部 Fluent 控件 |
| WPF-UI.DependencyInjection | 4.3.0 | `AddNavigationViewPageProvider()`，让 NavigationView 从 DI 解析页面 |
| WPF-UI.Tray | 4.3.0 | `tray:NotifyIcon` 托盘 |
| WPF-UI.Violeta | 4.3.0.3 | `Toast`、`ExceptionReport`、额外控件字典 `vio:ControlsDictionary` |
| CommunityToolkit.Mvvm | 8.2.2 | `ObservableObject`、`[ObservableProperty]`、`[RelayCommand]`、Messenger |
| Microsoft.Xaml.Behaviors.Wpf | 1.1.122 | `EventTrigger → InvokeCommandAction`、自定义 Behavior |
| Microsoft.Extensions.Hosting / DependencyInjection / Logging | 9.0.4 | Generic Host + DI + ILogger |
| Microsoft.Extensions.Localization | 9.0.4 | `services.AddLocalization()`（实际几乎未用，可去掉） |
| Serilog.Extensions.Logging | 9.0.1 | ILogger → Serilog 桥 |
| Serilog.Sinks.File | 6.0.0 | 按日滚动日志文件 |
| Serilog.Sinks.Console | 6.0.0 | 控制台输出（配合 `ConsoleHelper.AllocateConsole`） |
| Serilog.Sinks.RichTextBoxEx.Wpf | 1.1.0.1 | **运行日志面板**的核心 sink（`IRichTextBox`） |
| Semver | 3.0.0 | 版本比较（`Global.IsNewVersion`） |
| Vanara.PInvoke.User32 | 4.1.3 | 窗口还原、`SendMessage`、DPI |
| Vanara.PInvoke.NtDll | 4.1.3 | `OsVersionHelper`（`RtlGetVersion`） |
| Newtonsoft.Json | 13.0.3 | i18n 字典、AGENTS.md 建议优先使用 |
| System.Drawing.Common | 10.0.7 | 托盘图标/Icon 处理（UseWindowsForms 也需要） |

**B. 可选保留（依功能取舍）**

| 包 | 版本 | 用途 |
|---|---|---|
| Microsoft.Toolkit.Uwp.Notifications | 7.1.3 | Windows 原生 Toast（`Service\Notifier\WindowsUwpNotifier.cs`） |
| AvalonEdit | 6.3.1.120 | `View\Controls\CodeEditor\CodeBox`/`JsonCodeBox`（代理工具编辑 JSON 配置/查看请求体很有用） |
| Markdig | 1.3.2 | `View\Controls\Markdown\MarkdownView`（可用于显示更新说明/帮助/LLM 回复预览） |
| Microsoft.Web.WebView2 | 1.0.2592.51 | `CheckUpdateWindow` 渲染发布说明、`WebpagePanel/WebpageWindow` |
| Emoji.Wpf | 0.3.4 | 文本 Emoji 渲染 |
| gong-wpf-dragdrop | 3.2.1 | 列表拖拽排序（代理路由规则排序可用） |
| Ookii.Dialogs.Wpf | 5.0.1 | 现代文件夹选择对话框 |
| DeviceId / .Windows / .Windows.Wmi | 6.9.0 | 设备指纹（首次运行标记，可换成 GUID） |
| Meziantou.Framework.Win32.CredentialManager | 1.7.4 | Windows 凭据管理器（**非常适合存 API Key**，见 `Helpers\Win32\CredentialManagerHelper.cs`） |
| Microsoft.Extensions.Caching.Memory / LazyCache | 9.0.4 / 2.4.0 | 内存缓存 |
| YamlDotNet | 16.3.0 | 若配置需要 YAML |

**C. 游戏专用（删除）**

OpenCvSharp4.WpfExtensions / .Extensions / .Windows 4.11.0.20250507；Microsoft.ML.OnnxRuntime.DirectML 1.21.0、Microsoft.ML.OnnxRuntime 1.21.0（IncludeAssets none）、Microsoft.ML.OnnxRuntime.Managed 1.21.0；YoloSharp 6.0.3；Microsoft.ClearScript.V8 + .Native.win-x64 7.4.5（JS 脚本引擎）；PresentMonFps 2.0.5；LibreHardwareMonitorLib 0.9.6；Melanchall.DryWetMidi 8.0.3（自动演奏）；HungarianAlgorithm 2.3.5；Clipper2 2.0.0；CsTrees 1.0.5 / CsTrees.MEAI 1.0.2；BetterGI.Assets.Map 1.0.24 / BetterGI.Assets.Model 1.0.34 / BetterGI.Assets.Other 1.0.27；BetterGI.VCRuntime 14.44.35208；LibGit2Sharp 0.31.0（脚本仓库更新）；MailKit / MimeKit 4.16.0（邮件通知器，可选）；Microsoft.Extensions.AI / .OpenAI 10.6.0（游戏内 AI 对话，注意：你的代理工具如需 OpenAI SDK 应自行引入，而非沿用）；NCalc 6.2.0；XamlAnimatedGif 2.3.1；MouseKeyHook 5.7.1（全局键鼠钩子，录制回放用）；System.IO.Hashing 9.0.4；Vanara.PInvoke.CoreAudio / SHCore 4.1.3。

---

## 三、应用启动与 DI

### 3.1 编码规范（`D:\better-genshin-impact\AGENTS.md`）
- ViewModel 继承 `ObservableObject`，使用 `[ObservableProperty]`/`[RelayCommand]`
- View 构造模式（实际代码为 `DataContext = ViewModel = viewModel; InitializeComponent();`），页面公开 `ViewModel` 属性，XAML 绑定用 `{Binding ViewModel.Xxx}`
- 注册：`services.AddView<ExamplePage, ExampleViewModel>();`
- 弹窗优先 `ThemedMessageBox`；JSON 优先 Newtonsoft.Json
- 构建：`dotnet build BetterGenshinImpact.sln -c Debug`

### 3.2 `App.xaml`（`D:\better-genshin-impact\BetterGenshinImpact\App.xaml`，49 行）
- MergedDictionaries：`<ui:ThemesDictionary Theme="Dark" />`、`<ui:ControlsDictionary />`、`<vio:ControlsDictionary />`、`/View/Controls/WpfUi/FaFontIconStyle.xaml`、`/View/Controls/Drawer/DrawerStyles.xaml`、`/View/Controls/Markdown/Resources/MarkdownStyles.xaml`
- 字体：`TextThemeFontFamily` = `/Resources/Fonts/MiSans-Regular.ttf#MiSans`；`DigitalThemeFontFamily` = `deluge-led.ttf#Deluge LED`
- 设计令牌：`ControlCornerRadius`=6、`PagePadding`=48,20,48,28
- 全局转换器（命名空间 `BetterGenshinImpact.View.Converters`）：`BooleanToVisibilityConverter`、`BooleanToVisibilityRevertConverter`、`BooleanToEnableTextConverter`、`InverseBooleanConverter`、`NotNullConverter`、`EnumToKVPConverter`、`CultureInfoNameToKVPConverter`、`StringToColorConverter`、`AdaptiveUniformGridColumnsConverter`；以及两个游戏专用 `DomainNameToCascadingItemConverter`、`AutoBossNameToCascadingItemConverter`（删除）
- 全局 `TextBox`/`ui:TextBox` 样式附加 `behavior:ClipboardInterceptor.EnableSafeClipboard="True"`

### 3.3 `App.xaml.cs`（`D:\better-genshin-impact\BetterGenshinImpact\App.xaml.cs`）

**Host 构建**（静态字段）：
```csharp
private static readonly IHost _host = Host.CreateDefaultBuilder()
    .CheckIntegration()      // RuntimeExtension: 检查 Assets/GameTask 目录 —— 游戏耦合，删除
    .UseElevated()           // 需要管理员则 RestartAsElevated —— 代理工具按需（监听 <1024 端口才需要）
    .UseInstanceIpc()        // InstanceBootstrap.Initialize() 单实例/多实例 IPC
    .ConfigureLogging(b => b.ClearProviders())
    .ConfigureServices((ctx, services) => { ... })
    .Build();
```

**ConfigureServices 顺序**：
1. `var configService = new ConfigService(); services.AddSingleton<IConfigService>(sp => configService); var all = configService.Get();`（配置先于一切）
2. 日志：目录 `Path.Combine(AppContext.BaseDirectory, "log")`，文件 `better-genshin-impact.log`；`var richTextBox = new RichTextBoxImpl(); services.AddSingleton<IRichTextBox>(richTextBox);`
3. Serilog：File sink（模板 `[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [{BgiInstance}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}`、`RollingInterval.Day`、shared、保留 31 个文件 / 21 天）、Console sink、`MinimumLevel.Debug()`、`Override("Microsoft", Warning)`；再包一层 `ConditionalLogEventSink`（`Helpers\ConditionalLogEventSink.cs`）把 Information 级别转发给 RichTextBox sink，谓词 `() => all.MaskWindowConfig is { MaskEnabled: true, ShowLogBox: true }`（迁移时改成"日志面板可见"）；`Log.Logger = ...; services.AddLogging(c => c.AddSerilog());`
4. `services.AddLocalization(); I18nService.Instance.ChangeLanguage(uiLanguage)`（zh-CN→zh-Hans、en-US→en、ja-JP→ja 映射）；`I18N.Culture = new CultureInfo("zh-Hans")`
5. 导航与宿主：`services.AddNavigationViewPageProvider(); services.AddSingleton(InstanceBootstrap.Current); services.AddSingleton<InstanceService>(); services.AddHostedService(sp => sp.GetRequiredService<InstanceService>()); services.AddHostedService<ApplicationHostService>(); services.AddSingleton<INavigationService, NavigationService>(); services.AddSingleton<IUpdateService, UpdateService>(); services.AddSingleton<ISnackbarService, SnackbarService>(); services.AddView<INavigationWindow, MainWindow, MainWindowViewModel>(); services.AddSingleton<NotifyIconViewModel>();`
6. 页面（`AddView<TPage, TViewModel>`）：HomePage/HomePageViewModel、ScriptControlPage/ScriptControlViewModel、TriggerSettingsPage/TriggerSettingsPageViewModel、MacroSettingsPage/MacroSettingsPageViewModel、CommonSettingsPage/CommonSettingsPageViewModel、TaskSettingsPage/TaskSettingsPageViewModel、HotKeyPage/HotKeyPageViewModel、NotificationSettingsPage/NotificationSettingsPageViewModel、KeyMouseRecordPage/KeyMouseRecordPageViewModel、JsListPage/JsListViewModel、MapPathingPage/MapPathingViewModel、OneDragonFlowPage/OneDragonFlowViewModel、MusicPage/MusicPageViewModel、KeyBindingsSettingsPage/KeyBindingsSettingsPageViewModel；另有 `PathingConfigViewModel`、`IBannerImageService→BannerImageService`、`WebImageInputViewModel`（transient）、ChildSession 三件套
7. 其余全是游戏服务（DirectInputMonitor、RawInputMonitor、OverlayMetricsService、TaskTriggerDispatcher、OCR/ONNX 工厂、地图 API、NotificationService(hosted)、NotifierManager、ScriptService、Music* 等）—— 删除

**DI 扩展**（`D:\better-genshin-impact\BetterGenshinImpact\Helpers\Extensions\DependencyInjectionExtensions.cs`）：
```csharp
public static IServiceCollection AddView<TWindow, TWindowImplementation, TViewModel>(this IServiceCollection services)
    where TWindow : class where TWindowImplementation : class, TWindow where TViewModel : class, IViewModel
    => services.AddSingleton<TWindow, TWindowImplementation>().AddSingleton<TViewModel>();
public static IServiceCollection AddView<TPage, TViewModel>(this IServiceCollection services)
    where TPage : FrameworkElement where TViewModel : class, IViewModel
    => services.AddSingleton<TPage>().AddSingleton<TViewModel>();
```
所有 Page/ViewModel 都是 **Singleton**，配合 `NavigationCacheMode="Enabled"`。

**静态访问器**：`App.ServiceProvider`、`App.GetLogger<T>()`、`App.GetService<T>()`、`App.GetService(Type)`（Service Locator，被 `Markup\ServiceLocatorExtension` 使用）。

**生命周期**：
- `OnStartup`：进程优先级 Normal → `WinePlatformAddon.ApplyApplicationConfig()` → `ConsoleHelper.AllocateConsole("BetterGI Console")`（Debug）→ `RegisterEvents()` → `await _host.StartAsync()` → `ServerTimeHelper.Initialize`（游戏）→ `UrlProtocolHelper.RegisterAsync()`（注册自定义 URL 协议，可保留改名）；catch → `HandleException(ex, isTerminating: true)` + WinForms MessageBox 兜底 + `Shutdown()`
- `OnExit`：`TempManager.CleanUp(); await _host.StopAsync(); _host.Dispose(); Log.CloseAndFlush(); ConsoleHelper.FreeConsoleWindow();`
- **异常处理** `RegisterEvents()`：`TaskScheduler.UnobservedTaskException`（忽略 "V8 object has been released"）、`DispatcherUnhandledException`（`e.Handled = true`）、`AppDomain.UnhandledException`（IsTerminating）。`HandleException(Exception e, bool isTerminating = false)`：非致命只记 Warning；致命走 `ShowExceptionDialog` → Violeta `ExceptionReport.Show(e)`（App.xaml.cs:514），用两个 `ManualResetEventSlim` 保证 Dispatcher 上弹窗（3 秒超时），兜底 `System.Windows.Forms.MessageBox`

**宿主服务** `D:\better-genshin-impact\BetterGenshinImpact\Service\ApplicationHostService.cs`：`class ApplicationHostService(IServiceProvider, InstanceService) : IHostedService`；`StartAsync → HandleActivationAsync()`：若无 MainWindow 则解析 `INavigationWindow`、`ShowWindow()`、导航到 `HomePage`（或按 `CommandLineOptions.Instance.Action` 跳转），最后 `instanceService.MarkApplicationReady()`。`Service\PageService.cs` 已整体注释（被 `AddNavigationViewPageProvider` 取代，删除）。

**ViewModel 基类**：`ViewModel\IViewModel.cs`（`public interface IViewModel;` 标记接口）；`ViewModel\ViewModel.cs`（`abstract partial class ViewModel : ObservableObject, INavigationAware, IViewModel`，提供 `OnNavigatedTo/From(Async)`）；`ViewModel\Message\RefreshDataMessage.cs`（Messenger 消息样例）。

**主题**：`ui:ThemesDictionary Theme="Dark"` 静态默认；运行时 `MainWindowViewModel.ApplyTheme(ThemeType)` → `Wpf.Ui.Appearance.ApplicationThemeManager.Apply(ApplicationTheme.Dark/Light)` + `WindowHelper.ApplyThemeToWindow`。

---

## 四、主窗口与导航；页面/ViewModel 逐个标注

### 4.1 `View\MainWindow.xaml`（`D:\better-genshin-impact\BetterGenshinImpact\View\MainWindow.xaml`）
- 根 `ui:FluentWindow`，900×600，`ExtendsContentIntoTitleBar="True"`、`WindowBackdropType="Auto"`、`Title="{i18n:T 更好的原神}"`、`Visibility="{Binding IsVisible, Mode=TwoWay, Converter=BooleanToVisibilityConverter}"`、`WindowState="{Binding WindowState, Mode=TwoWay}"`
- Behaviors：Activated→`ActivatedCommand`、Loaded→`LoadedCommand`、Closing→`ClosingCommand`（PassEventArgsToCommand）
- 布局 Grid 2 行 2 列：
  - 底层 `Image` 背景（`Config.CommonConfig.MainBackgroundOpacity/Stretch`、`MainBackgroundSource`、`IsMainBackgroundVisible`）
  - `ui:NavigationView x:Name="RootNavigation"`（`IsBackButtonVisible=Collapsed`、`IsPaneToggleVisible`、`OpenPaneLength=160`、去掉 Top/Footer 分隔线）+ `<behavior:AnimatedNavigationSelectionIndicatorBehavior />`
  - MenuItems（全部 `NavigationCacheMode="Enabled"`，`ui:SymbolIcon`）：启动→HomePage(Play24)、实时触发→TriggerSettingsPage(Timer24)、独立任务→TaskSettingsPage、一条龙→OneDragonFlowPage、全自动(Bot24) 分组→调度器/JS 脚本/地图追踪/录制回放、辅助操控→MacroSettingsPage、自动演奏→MusicPage、快捷键→HotKeyPage(Flash24)、通知→NotificationSettingsPage(Alert24)；FooterMenuItems：设置→CommonSettingsPage(Settings24)
  - `NavigationView.ContentOverlay`：兑换码通知卡片（Border+DropShadow+accent 条+关闭按钮，**可改造成通用"公告/InfoBar"**）+ `<ui:SnackbarPresenter x:Name="SnackbarPresenter" />`
  - `ui:TitleBar`（Row 0）：`ui:TitleBar.Icon` = `pack://application:,,,/Resources/Images/logo.png`；Header StackPanel 附 `behavior:WindowDragBehavior`，标题 + "桌面分身" 徽章；TrailingContent 三按钮：`OpenFeedCommand`(Gift16，游戏)、`SwitchBackdropCommand`(Blur24，"切换深浅主题或背景样式")、`HideCommand`(CaretDown24，"最小化到托盘")
  - `tray:NotifyIcon`（`FocusOnLeftClick=False`、Icon logo.ico、`LeftDoubleClick="OnNotifyIconLeftDoubleClick"`、`MenuOnRightClick`、TooltipText）；ContextMenu 的 DataContext 通过 `<markup:ServiceLocator Type="{x:Type viewModel:NotifyIconViewModel}" />` 注入；菜单项：桌面分身(删)、检查更新(`CheckUpdateCommand`)、退出(`ExitCommand`)

### 4.2 `View\MainWindow.xaml.cs`
`public partial class MainWindow : FluentWindow, INavigationWindow`；构造 `(MainWindowViewModel, INavigationService, ISnackbarService)`：`DataContext = ViewModel = viewModel; InitializeComponent(); this.InitializeDpiAwareness(); snackbarService.SetSnackbarPresenter(SnackbarPresenter); navigationService.SetNavigationControl(RootNavigation); Application.Current.MainWindow = this;` 另含全局平滑滚动（PreviewMouseWheel + `CompositionTarget.Rendering` 插值：lerp 0.35/0.85、灵敏度 0.27、衰减 0.94）。`OnSourceInitialized → WindowHelper.TryApplySystemBackdrop(this)`；`OnClosed → App.GetService<NotifyIconViewModel>()?.Exit()`；`INavigationWindow` 实现：`GetNavigation() => RootNavigation`、`Navigate(Type)`、`SetPageService(INavigationViewPageProvider) => RootNavigation.SetPageProviderService(p)`、`ShowWindow()`、`CloseWindow()`。**整体保留**，仅删 Feed 按钮、分身徽章、兑换码卡片内容。

### 4.3 `ViewModel\MainWindowViewModel.cs`（682 行）
构造 `(INavigationService, IConfigService, ChildSessionService)`；属性 `IsVisible`、`WindowState`、`CurrentBackdropType`、`IsWin11Later`、`MainBackgroundSource`、`IsMainBackgroundVisible`、`IsRedeemCodeInfoBarOpen`；`Config = _configService.Get()`；订阅 `Config.CommonConfig.PropertyChanged` 重载背景。
- 保留：`OnHide`（IsVisible=false）、`OnSwitchBackdrop`（Win11 22523 以下 DarkNone↔LightNone；否则 DarkMica→DarkAcrylic→LightMica→LightAcrylic 循环）、`ApplyTheme(ThemeType)`、`OnClosing`（`ExitToTray` 时 `e.Cancel=true; OnHide()`）、`OnLoaded` 中的 ApplyTheme / RunForVersion / IsFirstRun / `OnceRun()`（WelcomeDialog）/ `CheckUpdateAsync(new UpdateOption())` / `TempManager.CleanUp`
- 删除：`OnActivated` 剪贴板导入脚本、OCR 预热、Patch1/2、兑换码 Feed、BitBlt 注册表、脚本自动更新。**改造后预计 <200 行。**

### 4.4 `ViewModel\NotifyIconViewModel.cs`（109 行，保留）
`ShowOrHide()`（隐藏或 Activate/Focus/Show + 文件内静态类 `WindowBacktray.Show` 用 `User32.SendMessage(hWnd, WM_SYSCOMMAND, SC_RESTORE)` 还原）、`Exit()`（保存配置后 `Application.Current.Shutdown()`）、`CheckUpdateAsync()`（`UpdateTrigger.Manual`）、`OpenChildSessionWindow()`（删）。

### 4.5 页面与 ViewModel 逐个标注

| 页面（`View\Pages\`） | ViewModel（`ViewModel\Pages\`） | 行数(xaml/cs;vm) | 结论 |
|---|---|---|---|
| HomePage | HomePageViewModel | 607/16; 842 | **改造**：保留 banner 区（`Border`+`ImageBrush`+右键菜单）、`CardExpander` 布局作为"仪表盘/代理状态"页；删除启动游戏逻辑；构造函数中对 `HotKeyPageViewModel` 的依赖去掉 |
| CommonSettingsPage | CommonSettingsPageViewModel | 2535/114; 948 | **改造**：作为设置页样板。`ui:CardControl`/`ui:CardExpander` + 头部 Grid（两行 `ui:TextBlock`：Body 标题 + Tertiary 描述）+ 右侧 ComboBox/ToggleSwitch 的模式全部保留；代码后置的"窗口 Deactivated 时提交焦点 TextBox 绑定"保留；VM 中保留语言切换（`LanguageDict`、`I18nService`）、主题、ExitToTray、开机启动、日志目录打开等命令，删除 OCR/Paddle/地图/脚本仓库等 |
| HotkeyPage | HotKeyPageViewModel | 91/15; 885 | **可选改造**：`ui:TreeListView` 热键表 UI 保留；VM 重写为 3–5 个热键 |
| NotificationSettingsPage | NotificationSettingsPageViewModel | 2695; 844 | **仅作样板**：多通道通知设置的表单模式可参考；实际内容删除 |
| TriggerSettingsPage | TriggerSettingsPageViewModel | 1941; 158 | 删除 |
| TaskSettingsPage | TaskSettingsPageViewModel | 4326; 973 | 删除 |
| OneDragonFlowPage + OneDragon\*Page | OneDragonFlowViewModel + OneDragon\* | 2165/217; 979 | 删除 |
| ScriptControlPage | ScriptControlViewModel | 406; 2487 | 删除（但其"分组列表 + 右侧详情 + 拖拽排序"布局可参考做路由规则页） |
| JsListPage | JsListViewModel | 190; 361 | 删除 |
| MapPathingPage | MapPathingViewModel | 174; 465 | 删除 |
| KeyMouseRecordPage | KeyMouseRecordPageViewModel | 137; 236 | 删除 |
| MacroSettingsPage | MacroSettingsPageViewModel | 579; 75 | 删除 |
| MusicPage | MusicPageViewModel | 655; 1073 | 删除 |
| KeyBindingsSettingsPage | KeyBindingsSettingsPageViewModel | 94/30; 631 | 删除 |
| View\HardwareAccelerationView / PathingConfigView / ScriptGroupConfigView | ViewModel\View\* | — | 删除 |

**窗口（`View\Windows\`）**

| 窗口 | 行数 | 结论 |
|---|---|---|
| ThemedMessageBox.xaml(.cs) | 356 | **保留**：FluentWindow，静态 `Show/ShowAsync/Information/Warning/Error`，`enum MessageBoxIcon { None, Information, Warning, Error, Question, Success }`，经 `WindowHelper` 应用背景 |
| PromptDialog.xaml(.cs) | 90/143 | **保留**：`PromptDialogConfig`（ShowLeftButton/LeftButtonText/LeftButtonClick），构造 `(string question, string title, UIElement uiElement, string? defaultValue, PromptDialogConfig? config)` |
| JsonMonoDialog + ViewModel\Windows\JsonMonoViewModel | 59/30; 71 | **保留**：JSON 查看/编辑弹窗 |
| AboutWindow | 66/62 | **保留改文案**：Base64 解码文本 + 五连击彩蛋 |
| CheckUpdateWindow | 253/271 | **保留**：`[ObservableObject]` FluentWindow，WebView2 显示发布说明 |
| WelcomeDialog | 75/42 | **保留改文案**（首次运行） |
| WebImageInput + WebImageInputViewModel | —;114 | 可选（背景图 URL 输入） |
| FeedWindow、ArtifactOcrDialog、AutoPick*、ChildSessionWindow、CustomHtmlMaskEditorWindow、Editable\ScriptGroupProjectEditor、ImageEditWindow、KeyBindingsWindow、MapLabelSearchWindow、MapPathingDevWindow、MapViewer、MusicSettingsWindow、PictureInPictureWindow、RecognitionTemplateEditorWindow、RepoUpdateDialog、ScriptRepoWindow、SkillCdConfigWindow | — | 删除 |
| View 根目录 MaskWindow(1388/827)、HtmlMaskWindow、CaptureTestWindow、PickerWindow | — | 删除（MaskWindow 中的日志框机制先抽出，见第八节） |

ViewModel 根目录其余：`MaskWindowViewModel`(1189)、`MaskMapPointInfoPopupViewModel`(354)、`MapIconImageCache`(183) 删除；`ViewModel\Windows\FormViewModel`(56) 可留作表单基类参考；其余 Windows VM 随窗口删除。

---

## 五、主题、样式与资源（可复用清单）

- **字体**：`Resources\Fonts\MiSans-Regular.ttf`（正文）、`deluge-led.ttf`（数码管数字，可用于速率/计数展示）、`Fgi-Regular.ttf`（游戏图标字体，删除）。
- **图片**：`Resources\Images\logo.ico/logo.png/banner.jpg/drag.png`（替换）、`Anniversary\*`（删除）。
- **控件样式**（`View\Controls\WpfUi\FaFontIconStyle.xaml`）：`FgiIconFontFamily`、`FaFontIconStyle`、`FaFontIconStyleForOneDragon` —— 若不用自定义图标字体可删；建议全部用 WPF-UI `SymbolIcon`。
- **Drawer 抽屉**（`View\Controls\Drawer\CustomDrawer.cs` + `DrawerStyles.xaml` + `DrawerViewModel.cs`）：`ContentControl` 子类，`IsOpen/DrawerPosition/OpenWidth/AnimationDuration` —— 保留（请求详情侧滑面板非常合适）。
- **Markdown**（`View\Controls\Markdown\`：`MarkdownEngine`、`MarkdownImageLoader`、`MarkdownModels`、`MarkdownView.xaml(.cs)`、`MarkdownWpfRenderer`、`Resources\MarkdownStyles.xaml`）：保留。
- **代码编辑器**（`View\Controls\CodeEditor\CodeBox.cs/CodeBox.xaml/JsonCodeBox.cs`，AvalonEdit）：保留。
- **MultiSelectComboBox.xaml(.cs)**、**TwoStateButton.cs**、**CascadeSelector**（游戏用途，删）、**Style\ListViewEx.xaml**、**Webview\WebpagePanel.cs/WebpageWindow.cs**、**WpfUiWindow.cs**：保留（CascadeSelector 除外）。
- **Draggable\***（Adorner/Thumb 拖拽缩放）、**Overlay\***、**MiniMapPointsCanvas\PointsCanvas**、**ChildSession\RdpActiveXHost**、**Drawable\***：游戏覆盖层，删除。
- **HotKey\HotKeyTextBox.cs**（继承 `Wpf.Ui.Controls.TextBox`，DP `Hotkey`、`HotKeyTypeName`）、**KeyBindings\KeyBindingTextBox**：前者可选保留，后者删除。
- **Behaviors**（`View\Behavior\`）：保留 `AnimatedNavigationSelectionIndicatorBehavior`（`Behavior<NavigationView>` 选中指示器动画）、`ClipboardInterceptor`（安全剪贴板；文件头有 MAA 再许可声明必须保留）、`ComboBoxPopupScrollBehavior`、`ResponsiveTitleBarBehavior`（`Behavior<Panel>`）、`RightClickSelectBehavior`、`SliderSeekBehavior`、`WindowAspectRatioBehavior`、`WindowDragBehavior`、`WindowResizeBehavior`；删除 `DomainCascadingComboBoxBehavior`、`TemplateImageSelectionBehavior`、`TemplateImageZoomBehavior`。
- **Converters**（`View\Converters\`）：保留 App.xaml 中的九个通用转换器 + `MillisecondsToTimeConverter`、`CultureInfoNameToKVPConverter.{en,fr,it,zh-Hans,zh-Hant}.resx`；删除 `Domain*/AutoBoss*`、`OverlayRelativeOrAbsoluteConverter`、`OverlayStyleConverters`。
- **Markup 扩展**（`Markup\`，69 行）：`ConverterExtension`（内联应用 IValueConverter）、`ServiceLocatorExtension`（`ProvideValue => App.GetService(Type)`）—— 保留。
- **i18n**：`Service\I18n\I18nService.cs`（单例，`DefaultLanguage="zh-Hans"`，读 `User/I18n/{lang}.json`，`Revision` 递增触发重绑定）、`Service\I18n\TExtension.cs`（`{i18n:T 中文原文}`，中文是键，缺失返回键本身，设计时直接返回键）；`User\I18n\en.json/it.json/ja.json/ru.json`（内容重写）。保留机制，配合 `Build\BetterGenshinImpact.I18nSync` 与 `Build\Scripts\i18n-sync.ps1` 同步键。

---

## 六、托盘、通知、热键、窗口状态、更新检查

1. **托盘**：`tray:NotifyIcon`（MainWindow.xaml）+ `ViewModel\NotifyIconViewModel.cs`；双击还原 `MainWindow.OnNotifyIconLeftDoubleClick → ShowOrHide()`；关闭窗口时 `MainWindow.OnClosed → NotifyIconViewModel.Exit()`。
2. **应用内通知**：`ISnackbarService`（`SnackbarPresenter` 在 NavigationView.ContentOverlay）；Violeta `Toast.Success/Warning/Error/Information`（用例：`Service\UpdateService.cs`、`Helpers\Win32\MirrorChyanHelper.cs`、`Core\Script\ScriptRepoUpdater.cs`、`Core\Recorder\*`）；异常报告 `ExceptionReport.Show`。
3. **系统通知**：`Service\Notifier\WindowsUwpNotifier.cs`（`new ToastContentBuilder().AddText(...).Show()`）保留；`Service\Notification\NotificationService.cs`（IHostedService，`Instance()`、`RefreshNotifiers`）、`NotificationConfig.cs`、`Notify.cs`、`Service\Notifier\NotifierManager.cs` 与 Bark/Discord/Email/Feishu/Gotify/Meow/OneBot/Qq/ServerChan/Telegram/WebSocket/Webhook/WechatClawbot/WorkWeixin/dingding/xxtui 等：若代理工具需要"额度告警推送"可选保留 NotifierManager + Webhook/Telegram，其余删除。
4. **热键**：`Fischless.HotkeyCapture` 工程 + `Model\HotKey.cs`（`readonly partial record struct HotKey(Key, ModifierKeys, MouseButton)` 带 `ToString/FromString`）+ `Model\HotKeySettingModel.cs`（`HotKey`、`HotKeyTypeEnum`、`FunctionName`、`Children`、`OnKeyPressAction/Down/Up`、`IsHold`）+ `Model\HotKeyTypeEnum.cs` + `Model\KeyboardHook.cs`/`MouseHook.cs`（依赖 MouseKeyHook，可删）+ `Core\Config\HotKeyConfig.cs` + `View\Controls\HotKey\HotKeyTextBox.cs` + `View\Pages\HotkeyPage.xaml`。`HotKeyPageViewModel` 需重写。
5. **窗口状态**：`IsVisible`/`WindowState` 双向绑定；`CommonConfig.ExitToTray`；`NotifyIconViewModel.WindowBacktray`；`Helpers\Ui\WindowHelper.cs`（`TryApplySystemBackdrop`、`ApplyThemeToWindow`（`WindowBackdrop.ApplyBackdrop(None/Mica/Acrylic)`，COMException `0x80263001` 回退）、`CenterOnVisibleOwner`）；`Helpers\DpiAwareness\`（`InitializeDpiAwareness()`）；`Helpers\OsVersionHelper.cs`（`IsWindows11_22523_OrGreater` 等）。窗口尺寸/位置**未持久化**（需自行加）。
6. **单实例 / IPC**：`Service\Instance\InstanceBootstrap.cs`（命名管道 `InstancePipeNames.ForCurrentUser()`，抢到服务端即 Primary，否则连接并在 `ConnectionOpenDisposition.ActivationForwarded` 时退出，实现"二次启动激活已有窗口"）、`InstanceService.cs`（IHostedService，`MarkApplicationReady`）、`InstanceContext.cs`（`enum BetterGiInstanceType { Primary, ChildSession, WebView }`）、`InstanceConnection.cs`、`InstanceIpcProtocol.cs`、`MessageHandlers\`。保留并简化为单实例；`Helpers\RuntimeHelper.CheckSingleInstance` 旧方案（EventWaitHandle）可删。
7. **更新检查**：`Service\UpdateService.cs`（`IUpdateService.CheckUpdateAsync(UpdateOption)`；OSS `notice.json` + MirrorChyan API；`Model\UpdateOption.cs` 的 `UpdateTrigger Auto/Manual`、`UpdateChannel`；弹 `CheckUpdateWindow`，按钮 BackgroundUpdate/OtherUpdate/Update（启动 `BetterGI.update.exe -I` 后 Shutdown）/Ignore（写 `Config.NotShowNewVersionNoticeEndVersion`）/Cancel）；`Core\Config\Global.IsNewVersion` 用 Semver。保留，替换 URL 与更新器文件名。
8. **命令行**：`Helpers\CommandLineOptions.cs`（`--instance`、`--restart-from-pid` 等）保留骨架。

---

## 七、配置持久化

- 接口 `Service\Interface\IConfigService.cs`：`AllConfig Get(); void Save(); AllConfig Read(); void Write(AllConfig)`。
- 实现 `Service\ConfigService.cs`：`ConfigRelativePath = @"User/config.json"`、`BackupFolderName = "backup"`；静态 `JsonSerializerOptions JsonOptions`（CamelCase、WriteIndented、AllowTrailingCommas、跳过注释、`UnsafeRelaxedJsonEscaping`、`AllowNamedFloatingPointLiterals`，含 `OpenCvPointJsonConverter/OpenCvRectJsonConverter` —— 删除）；静态 `Config`；`Get()` 首次读取后设置 `Config.OnAnyChangedAction = Save; Config.InitEvent();`；`Read()` 反序列化失败时备份到 `User/backup/config_{yyyyMMdd_HHmmss_fff}.json.bak` 并 `ThemedMessageBox.ErrorAsync` 提示；`Write()` 使用 `ReaderWriterLockSlim`。
- 模型 `Core\Config\AllConfig.cs`：`[Serializable] partial class AllConfig : ObservableObject`，子配置均为 `ObservableObject`；`[JsonIgnore] Action? OnAnyChangedAction`；`InitEvent()` 给每个子配置挂 `PropertyChanged += OnAnyPropertyChanged`；`OnAnyPropertyChanged` 调 `GameTaskManager.RefreshTriggerConfigs()`（**删除此行**）后 `OnAnyChangedAction?.Invoke()` → **任何属性改动即整体落盘**（无防抖，建议加 200ms 防抖）。
- 保留：`CommonConfig.cs`（`enum ThemeType { DarkNone, DarkMica, DarkAcrylic, LightNone, LightMica, LightAcrylic }`；`ExitToTray`、`CurrentThemeType`（默认 Win11 22523+ 为 DarkMica）、`MainBackground*`、`IsFirstRun`、`RunForVersion`、`OnceHadRunDeviceIdList`）、`OtherConfig.cs` 中 `UiCultureInfoName`、`HotKeyConfig.cs`（可选）、`NotificationConfig`（可选）、`Global.cs`（`Version`、`StartUpPath`、`Absolute()`、`IsNewVersion`、`ManifestJsonOptions`）。
- 删除：其余 20 余个 `Core\Config\*Config.cs`（Genshin/Mask/Macro/Music/OneDragon/Pathing/Record/Script/TaskCompletion 等）。
- 注意 AGENTS.md 说优先 Newtonsoft，但配置本身用 System.Text.Json，两者并存；迁移时统一为一种。

---

## 八、日志与"运行日志"面板

- 主窗口内**没有**日志页；运行日志显示在游戏覆盖层 `View\MaskWindow.xaml` 的 `<RichTextBox x:Name="LogTextBox" IsHitTestVisible="False" VerticalScrollBarVisibility="Hidden">`（包在 `overlay:AdjustableOverlayItem LayoutKey="LogTextBox"` 中，样式绑定 `MaskWindowConfig.LogFontFamily/LogFontSize/LogTextColor/LogPanelBackgroundColor/TextOpacity`）。
- 机制链：Serilog → `ConditionalLogEventSink`（`Helpers\ConditionalLogEventSink.cs`，`Emit` 时按 `Func<bool>` 决定是否转发）→ `Serilog.Sinks.RichTextBoxEx.Wpf` 的 `RichTextBox(IRichTextBox, LogEventLevel.Information, "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")` → `RichTextBoxImpl` 单例（DI 注册为 `IRichTextBox`）→ `MaskWindow.OnLoaded` 中 `_richTextBox.RichTextBox = LogTextBox` 完成挂接。
- `MaskWindow.LogTextBoxTextChanged`：首段 Inlines 超过 200 条时裁剪、总文本 >10000 字符时清空、`ScrollToEnd()`。
- **迁移做法**：新建 `LogPage`（NavigationView 菜单项），把 `RichTextBox` 与上述 `TextChanged` 裁剪逻辑搬入；`ConditionalLogEventSink` 谓词改为 `() => logPageVisible`；把 RichTextBox 改为 `IsHitTestVisible=True` 支持选中复制，增加"打开日志目录"（`CommonSettingsPageViewModel` 已有）与"清空"按钮。日志文件目录保持 `AppContext.BaseDirectory/log`。

---

## 九、Build 目录与打包发布流程

`D:\better-genshin-impact\Build\`：
- `Scripts\setup_build.cmd`：vswhere 定位 MSBuild → `dotnet publish -c Release -p:PublishProfile=FolderProfile` → xcopy 到 `dist\BetterGI` → 删 `*.lib`、`*ffmpeg*.dll` → `7z a ... -t7z -mx=5 -mf=BCJ2` 生成 `BetterGI_v%b%.7z`。**保留改名**。
- `Scripts\package.bat`（旧 net7 路径，删）、`setup_build_for_appveyor.cmd`、`upload_1_build_dist.cmd`、`upload_2_zip_dist.ps1`（上传 OSS，删或改）、`Scripts\i18n-sync.ps1`（保留）。
- `MicaSetup.Tools\7-Zip\7z.exe/7z.dll`（保留）；`micasetup\micasetup.json` + 图标（MicaSetup 安装器：AppName/ExeName/Publisher/RequestExecutionLevel admin/桌面快捷方式/防火墙等，改值保留）；`kachina.config.json`（Kachina 增量更新安装器配置，改值保留）。
- **未使用 Inno Setup / WiX / MSIX。**

`D:\better-genshin-impact\.github\workflows\publish.yml`（"BetterGI Publish"，`workflow_dispatch` 输入 version/kachina-channel/create-release/upload-to-steambird）：validate（semver 正则）→ build_web_map_editor / web_scripts_list（外部 npm 仓库，**删除**）→ 改 csproj Version 并自动提交 → NuGet 缓存 → `dotnet publish BetterGenshinImpact/BetterGenshinImpact.csproj -c Release -p:PublishProfile=FolderProfile -p:Version=...` → 下载 kachina-builder（YuehaiTeam/kachina-installer）→ `kachina-builder.exe pack -c ..\Build\kachina.config.json -o BetterGI/BetterGI.update.exe` → 7z 打包 → 下载最近 3 个 Release 用 `kachina-builder.exe gen ... --diff-vers` 生成差分 → `BetterGI.Install.{ver}.exe` → MicaSetup 打包 → Release。其它 workflow：`mirrorchyan_release_note.yml`、`mirrorchyan_uploading.yml`、`repo-bot.yml`、python 脚本（`cnb_release.py`、`cnb_trigger.py`、`github_download_and_cnb_upload.py`）与 `.cnb.yml`（CNB 镜像 CI）—— 全部删除。保留 publish.yml 的 validate→publish→7z→installer 骨架。

---

## 十、目录级保留/删除/改造清单

| 目录 | 结论 | 说明 |
|---|---|---|
| `Core\Config` | **改造** | 留 AllConfig（精简）、CommonConfig、OtherConfig（精简）、Global、HotKeyConfig(可选)；删其余；去掉 `GameTaskManager` 调用 |
| `Core\BgiVision`、`Core\Monitor`、`Core\Recognition`、`Core\Recorder`、`Core\Script`、`Core\Simulator` | **删除** | 视觉识别、键鼠监听/模拟、录制、JS 脚本引擎 |
| `GameTask\` | **删除** | 379 文件 / 78,511 行全部游戏任务 |
| `Genshin\` | **删除** | Paths/Settings/Settings2 |
| `Helpers\` | **改造** | 保留：`Extensions\DependencyInjectionExtensions`、`ConditionalLogEventSink`、`RuntimeHelper`（删 `CheckIntegration`/`CheckSingleInstance`，`RestartAsElevated` 改 exe 名）、`OsVersionHelper`、`UIDispatcherHelper`、`CommandLineOptions`、`Ui\WindowHelper`（去掉 `TaskContext.Instance()`，改读 `IConfigService`）、`Win32\ConsoleHelper`、`Win32\CredentialManagerHelper`、`DpiAwareness\*`、`Http\HttpClientFactory`、`Crud\JsonCrudHelper`、`Security\MD5Helper`、`Extensions\{Boolean,Enum,Task}Extension`、`Base64Helper`、`CultureHelper`、`DeviceIdHelper`、`DirectoryHelper`、`DpiHelper`、`RegexHelper`、`ResourceHelper`、`StringUtils`、`TempManager`、`UrlProtocolHelper`、`User32Helper`、`PrimaryScreen`、`SpeedTimer`、`SemaphoreSlimParallel`。删除：`Win32\MirrorChyanHelper`（除非沿用 MirrorChyan 分发）、`Http\ProxySpeedTester`、`Extensions\{Bitmap,Click,Mat,Point,RectCut,Rect}Extension`、`ExpandoObjectConverter`、`ScriptObjectConverter`、`ServerTimeHelper`、`SecurityControlHelper`、`AutoBossCascadingItems`、`DomainCascadingItems`、`AssertUtils`、`MathHelper`、`ObjectUtils` |
| `Markup\` | **保留** | ConverterExtension、ServiceLocatorExtension |
| `Model\` | **改造** | 保留：HotKey、HotKeySettingModel、HotKeyTypeEnum（可选）、UpdateOption、Notice、EnumItem、SettingItem、StatusItem、Singleton、FileTreeNode；删除：Condition*、KeyBindingSettingModel、KeyMouseScriptItem、KeyboardHook、MouseHook、MaskButton、MaskMap\、OneDragonTaskItem |
| `Properties\` | **保留** | PublishProfiles\FolderProfile.pubxml、launchSettings.json |
| `Resources\` | **改造** | 换 logo/banner；保留 MiSans、deluge-led；删 Fgi-Regular、Anniversary |
| `Service\` | **改造** | 保留：ApplicationHostService（简化导航分支）、ConfigService、Interface\IConfigService、I18n\*、Instance\*（简化）、UpdateService、Notifier\WindowsUwpNotifier + NotifierManager（可选）；删除：PageService（已注释）、ChildSession\*、Notification\*（或精简）、其余 Notifier、所有游戏服务（Script/Music/Map/OCR/Overlay/Recognition 等） |
| `User\` | **改造** | 保留 `I18n\*.json`（重写内容）；删 AutoFight、AutoGeniusInvokation、avatar_macro_default.json；`config.json` 运行时生成 |
| `View\` | **改造** | 见第四、五节：保留 MainWindow、Behavior（9 个）、Controls（Drawer/Markdown/CodeEditor/MultiSelectComboBox/TwoStateButton/Webview/WpfUiWindow/HotKey/Style）、Converters（通用部分）、Windows（ThemedMessageBox/PromptDialog/JsonMonoDialog/AboutWindow/CheckUpdateWindow/WelcomeDialog）、Pages（HomePage/CommonSettingsPage/HotkeyPage 改造）；删 MaskWindow 等四个根窗口、Drawable、Overlay、Draggable、MiniMapPointsCanvas、ChildSession、其余页面与窗口 |
| `ViewModel\` | **改造** | 保留 ViewModel.cs、IViewModel.cs、Message\、MainWindowViewModel（精简）、NotifyIconViewModel、Pages\{HomePage,CommonSettingsPage,HotKeyPage}ViewModel（重写）、Windows\{JsonMono,WebImageInput,Form}ViewModel；删其余 |
| `Wine\` | **可选** | `WinePlatformAddon.cs`（namespace `BetterGenshinImpact.Platform.Wine`）用于 Wine 兼容；`MouseKeyMonitor.Wine.cs` 删。若不考虑 Linux/Wine 直接删除并移除 App.xaml.cs/MainWindowViewModel 中的 Wine 判断 |
| 根文件 | **改造** | `App.xaml`/`App.xaml.cs`（按第三节裁剪）、`GlobalUsing.cs`、`AssemblyInfo.cs` 保留；`README.md` 重写 |

**必须解耦的隐藏耦合点**（否则编译不过）：`AllConfig.OnAnyPropertyChanged → GameTaskManager`；`WindowHelper.TryApplySystemBackdrop → TaskContext.Instance()`；`RuntimeHelper.CheckIntegration`（要求 Assets/GameTask 目录）与 `CheckSingleInstance → SystemControl.RestoreWindow`；`ConfigService.JsonOptions` 的 OpenCv 转换器；`App.xaml` 两个游戏转换器；`MainWindowViewModel.OnLoaded/OnActivated` 的 OCR/兑换码/脚本逻辑；`HomePage` 构造函数依赖 `HotKeyPageViewModel`；`ConditionalLogEventSink` 谓词读 `MaskWindowConfig`；`CommonSettingsPageViewModel` 引用 `Core.Recognition.OCR.Paddle`、`GameTask.*`、`Core.Script`；`I18nService`/`TExtension` 无游戏耦合。

---

## 十一、代码量统计（`D:\better-genshin-impact\BetterGenshinImpact\`）

| 目录 | .cs 文件 | .cs 行数 | .xaml 文件 | .xaml 行数 | 迁移后预估保留 |
|---|---|---|---|---|---|
| Core | 137 | 24,385 | 0 | 0 | ~600 行（Config 子集） |
| GameTask | 379 | 78,511 | 0 | 0 | 0 |
| Genshin | 14 | 2,076 | 0 | 0 | 0 |
| Helpers | 51 | 3,548 | 0 | 0 | ~1,800 行 |
| Markup | 2 | 69 | 0 | 0 | 69 行 |
| Model | 23 | 1,695 | 0 | 0 | ~400 行 |
| Service | 104 | 17,018 | 0 | 0 | ~2,000 行 |
| View | 117 | 17,626 | 68 | 26,606 | ~3,500 cs / ~2,500 xaml |
| ViewModel | 52 | 16,780 | 0 | 0 | ~1,200 行 |
| Wine | 2 | 264 | 0 | 0 | 0–150 行 |
| 根（App.xaml.cs+AssemblyInfo+GlobalUsing） | 3 | 538 | 1 | 49 | ~350 行 |
| **合计** | **884** | **162,510** | **69** | **26,655** | **约 10,000–12,000 行（≈6%）** |

Fischless.HotkeyCapture 约 5 个文件、几百行；I18nSync 3 个文件。

---

## 十二、许可证

- `D:\better-genshin-impact\LICENSE`：**GNU GPL v3.0**。
- 约束：以本项目代码为基础的衍生作品（即使删掉全部游戏内容，只要保留了 MainWindow、ConfigService、I18n、Behaviors 等源码）**必须以 GPL-3.0 发布**、提供完整源码、保留原版权声明（huiyadanli 及贡献者），不得闭源分发；若你的 LLM 反向代理计划闭源或商用授权，需要**重写而非复制**这些文件（仅参考架构与 NuGet 组合，不受 GPL 约束的只有"想法"）。
- 单文件例外：`View\Behavior\ClipboardInterceptor.cs` 文件头注明源自 MaaAssistantArknights（AGPL-3.0）并再许可为 GPL-3.0 only，该头部必须原样保留。
- 子工程 `Fischless.WindowsInput` 自带独立 `LICENSE.txt`（源自 InputSimulator 系，通常 MIT/MS-PL，未逐字核对）；删除该工程即无影响。`Fischless.HotkeyCapture` 未见单独许可证，随主仓库 GPL-3.0。
- 第三方包：WPF-UI、Violeta、CommunityToolkit 为 MIT；AvalonEdit MIT；WebView2 为微软专有运行时许可（可再分发）；MiSans 字体为小米开源许可（允许免费商用，需保留版权）。

---

### 附：建议的迁移最小骨架（供 PRD 引用）
1. 新建 `net8.0-windows10.0.22621.0` WinExe，引入第二节 A 组包 + 可选 B 组（AvalonEdit、Markdig、CredentialManager、Uwp.Notifications）。
2. 复制：`App.xaml`（裁剪）、`App.xaml.cs`（Host/日志/异常/DI 骨架）、`Helpers\Extensions\DependencyInjectionExtensions.cs`、`Helpers\ConditionalLogEventSink.cs`、`Helpers\OsVersionHelper.cs`、`Helpers\Ui\WindowHelper.cs`、`Helpers\DpiAwareness\*`、`Helpers\RuntimeHelper.cs`、`Helpers\CommandLineOptions.cs`、`Helpers\Win32\ConsoleHelper.cs`、`Markup\*`、`Service\ApplicationHostService.cs`、`Service\ConfigService.cs` + `Interface\IConfigService.cs`、`Service\I18n\*`、`Service\Instance\*`、`Service\UpdateService.cs` + `Model\UpdateOption.cs`、`Core\Config\{AllConfig,CommonConfig,Global}.cs`、`View\MainWindow.xaml(.cs)`、`ViewModel\{ViewModel,IViewModel,MainWindowViewModel,NotifyIconViewModel}.cs`、`View\Behavior\`（9 个）、`View\Converters\`（通用）、`View\Controls\{Drawer,Markdown,CodeEditor,MultiSelectComboBox,TwoStateButton,Webview,WpfUiWindow}`、`View\Windows\{ThemedMessageBox,PromptDialog,JsonMonoDialog,AboutWindow,CheckUpdateWindow,WelcomeDialog}`、`View\Pages\{HomePage,CommonSettingsPage}`（作为模板）、`Resources\Fonts\{MiSans-Regular,deluge-led}.ttf`、`Build\{Scripts\setup_build.cmd,MicaSetup.Tools,micasetup,kachina.config.json}`、`.github\workflows\publish.yml`（裁剪）、`Build\BetterGenshinImpact.I18nSync`。
3. 新增：`LogPage`（第八节）、代理专属页面（Providers/Routes/Keys/Metrics）沿用 `CardControl/CardExpander` 设置样板，Drawer 做请求详情侧栏，AvalonEdit 显示请求/响应 JSON。
4. 全局替换命名空间 `BetterGenshinImpact` → 新名，`BetterGI` → 新 AssemblyName，`{i18n:T 更好的原神}` → 新标题，`User/config.json` 路径可保留。
