# LLM Retry Proxy C# 重构 PRD

版本：v1.0 草案　日期：2026-09-21
来源：`D:\API-Proxy`（Rust 2.0.0 现行实现）+ `D:\better-genshin-impact`（BetterGI 0.65.1，WPF UI 框架底子）

---

## 1. 背景与目标

现有 LLM Retry Proxy 用 Rust + egui 实现，功能已稳定（约 2 万行，含 4 千行集成测试），但界面是即时模式 GUI，扩展与视觉一致性受限。目标是：

1. 以 BetterGI 的 WPF 工程为 UI/UX 底座（WPF-UI Fluent 风格、NavigationView、托盘、主题、i18n、配置、日志、更新检查、单实例），剔除全部游戏相关内容。
2. 把 Rust 后端（代理、重试、流式等待生成、缓存标识补全、响应统计、每日统计、保活、CLI 驱动、系统代理）**逐规则等价移植**到 C#，行为、日志文案、配置格式、统计文件格式与现版本兼容。
3. 产出一个新项目目录，独立于 `D:\API-Proxy`，可单独构建、发布、验收。

不是目标：重新设计功能、改变重试语义、改配置存储位置、给界面新增 Rust 版没有的功能（除 BetterGI 框架自带的通用能力）。

---

## 2. 范围

### 2.1 包含
- 新建 C# 解决方案（App + Core + Tests 三个工程）。
- BetterGI 框架层裁剪迁移（清单见附录 A）。
- Rust 后端全部模块移植（映射见附录 B）。
- Rust 版所有用户可见文案、对话框、校验提示原样沿用。
- 集成测试移植为 xUnit，PowerShell 验收脚本适配新 EXE。
- 构建/发布脚本与安装包（沿用 BetterGI 的 `setup_build.cmd` + MicaSetup 或直接发布单文件）。

### 2.2 不包含
- 游戏捕获、OCR、ONNX、脚本引擎、录制回放、地图、音乐等全部 BetterGI 业务。
- 多语言翻译内容本身（保留 i18n 机制，首期只有简体中文）。
- Wine/Linux 兼容。

---

## 3. 关键决策与假设

| # | 事项 | 决策 / 假设 | 说明 |
|---|---|---|---|
| D1 | 许可证 | **已确认（2026-09-22）**：新项目采用 GPL-3.0。照搬 BetterGI 源码，保留原版权声明；`View\Behavior\ClipboardInterceptor.cs` 文件头的 MAA 再许可声明必须保留。仓库根目录放 `LICENSE`（GPL-3.0）。 | 用户已接受。 |
| D2 | 项目目录与命名 | **已确认**：目录 `D:\RetryProxy`，解决方案 `RetryProxy.sln`，工程 `RetryProxy.App`（WPF）、`RetryProxy.Core`（后端类库）、`RetryProxy.Tests`（xUnit）。AssemblyName `RetryProxy`，窗口标题 `LLM Retry Proxy`。 | |
| D3 | 目标框架 | `net9.0-windows10.0.22621.0`（本机已装 SDK 9.0.305 / 10.0.201；WPF-UI 4.3 支持 net8/net9）。 | BetterGI 原为 net8；升到 net9 只改 TFM。 |
| D4 | 发布形态 | **已确认**：框架依赖（`SelfContained=false`）+ `PublishSingleFile=true` + win-x64，产物 `dist\RetryProxy.exe`（约 10 MB）。运行需要 .NET 9 Desktop Runtime 与 ASP.NET Core Runtime（Kestrel 依赖 `Microsoft.AspNetCore.App`）。 | 首次启动缺运行时时，.NET 宿主会弹出下载提示；README 写明安装 "ASP.NET Core Runtime 9 (Hosting Bundle)" 或分别安装两个运行时。 |
| D5 | 配置存储 | **已确认：完全沿用 BetterGI 方式**，`{exe 目录}\User\config.json`，System.Text.Json 缩进输出，`ConfigService` 读失败时把坏文件备份到 `User\backup\` 后用默认值重建；`AllConfig` 任一属性变更即整体落盘（200 ms 防抖）。代理配置（`ProxyConfig`：providers/routes/selected_route_id/schema_version）作为 `AllConfig.Proxy` 子对象存放，界面偏好（主题、语言、托盘行为、窗口尺寸）作为 `AllConfig.Common` 等同级子对象。 | 与 Rust 版**不再共用**配置。首次启动若 `config.json` 不存在而注册表 `HKCU\Software\LLM Retry Proxy\ConfigJson` 存在，则读取并迁移一次（走同一套 schema 6 迁移逻辑），写入 config.json 后不再回写注册表；日志记录 `已从注册表导入旧配置`。`RETRY_PROXY_CONFIG_JSON` 环境变量注入仍保留供测试使用，此时保存为空操作。 |
| D6 | HTTP 服务端 | 每条通道一个独立 Kestrel `WebApplication`，绑定 `127.0.0.1:port`；停用即硬停（不做优雅关闭），与 Rust 语义一致。 | 用 `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`。不用 HttpListener（http.sys 无法精细控制流式与 499）。 |
| D7 | HTTP 客户端 | 每通道一个 `HttpClient`（`SocketsHttpHandler`：禁重定向、ConnectTimeout=单次超时、自定义 `IWebProxy` 复刻 `system_proxy.rs` 规则、TLS 走 SChannel）。连续无数据超时用逐段读取 + `CancellationTokenSource` 实现。 | .NET 无原生 read-timeout。 |
| D8 | 页面结构 | **已确认（2026-09-22，用户明确"首页要和参考项目一致"；同日拍板落点）**：NavigationView 六页：**首页**（照搬 BetterGI 首页：横幅 + 一张「代理服务，启动！」折叠卡，卡内含服务商/通道的新增/编辑/删除按钮，见 5.2）、**运行状态**（新增：通道计数与全部启停、状态胶囊、保活状态、统计瓦片、缓存摘要、请求明细、策略行）、**运行日志**（新增：日志面板独占一页）、**缓存明细**（原独立窗口改为页面，只从左侧导航进入）、**设置**（语言 + 「开机自动启动」折叠组）、**关于**。 | 首页视觉与 BetterGI 一致优先于沿用 Rust 工作流。M0 已按此实现首页/设置页并截图验证；运行状态、运行日志两页在 M5 实现。 |
| D9 | 日志面板 | 不用 Serilog RichTextBox sink；自实现 `LogPage` 控件：虚拟化 `ListView` + 按行着色（等价 Rust `log_line_job`）。文件日志沿用 Rust 格式（自实现轮转 5 MiB × 3），**不用 Serilog 按日滚动**，否则 legacy 恢复解析失效。 | 旧日志恢复逻辑依赖 `retry-proxy.log(.1/.2/.3)` 与 `WARNING` 级别字样。 |
| D10 | JSON 库 | 统一 `System.Text.Json`；prompt_cache_key 插入采用字节级操作，不反序列化重写。 | AGENTS.md 偏好 Newtonsoft，但本项目需要保留原始字节，STJ 足够。 |
| D11 | 图标 | 用 Rust `icon.rs` 的配色重新绘制静态 `logo.ico`/`logo.png`（深靛底、青绿链路、橙色箭头）。 | 不复用 BetterGI 的原神图标。 |

---

## 4. 技术架构

### 4.1 解决方案结构

```
D:\RetryProxy\
  RetryProxy.sln
  Directory.Build.props            统一 TFM、Nullable、LangVersion、版本号
  src\
    RetryProxy.Core\               纯后端，无 WPF 依赖，可单测
      Config\                      ProxyConfig / ProviderEndpoint / ProxyRoute / ClientType / 迁移 / 注册表一次性导入
      Proxy\                       RetryProxy(请求管线) / GenerationGate / StreamLifecycle / RetryPolicy / HeaderRules
      Stats\                       ResponseStats / EventDecoder / TokenUsage / FailureSummary / Sanitizer
      Cache\                       PromptCache / CacheKeyState / RejectionProbe
      Metrics\                     ProxyMetrics / DailyJournal / LegacyLogRestore / Snapshot 类型
      KeepAlive\                   KeepAliveWatchdog / KeepAliveProbe / Conversation / InternalSessions
      Cli\                         CliCommand / CodexSession / ClaudeSession / CliCredential / SafeCliError / ProcessJob
      Logging\                     RouteLogger / RotatingFileLogger / LogQueue
      Service\                     ProxyService(状态机) / SystemProxyResolver / HealthEndpoint
      JavaQuestions.cs             250 题
    RetryProxy.App\                WPF，BetterGI 框架层裁剪版
      App.xaml(.cs)                Host / DI / 异常 / 日志 / 主题
      Core\Config\                 AllConfig(UI 偏好) / Global
      Helpers\ Markup\ Service\    BetterGI 通用件
      View\  ViewModel\            MainWindow + 五页 + 对话框
      Resources\
    RetryProxy.Tests\              xUnit：移植 rust/tests 四个文件 + config/proxy 单测
  tests\                           verify_rust_exe.ps1 / verify_keepalive_exe.ps1 / window_verification.cs（适配）
  build\                           setup_build.cmd / micasetup / kachina / 7z
  dist\RetryProxy.exe
```

### 4.2 技术栈映射

| Rust | C# |
|---|---|
| axum + tokio | Kestrel（Minimal API）+ async/await |
| reqwest（native-tls, stream） | HttpClient + SocketsHttpHandler，`HttpCompletionOption.ResponseHeadersRead` |
| `CancellationToken`（tokio-util） | `System.Threading.CancellationTokenSource` |
| `tokio::select!(biased)` | `Task.WhenAny` + 优先级判定辅助方法（须保证同一优先级语义：通道取消 > 内部取消 > 总等待 > 正常路径） |
| serde / serde_json | System.Text.Json（`JsonSerializerOptions` 保留字段顺序；`JsonNode` 处理动态结构） |
| sha2 | `System.Security.Cryptography.SHA256` |
| winreg | `Microsoft.Win32.Registry` |
| tray-icon | WPF-UI.Tray `NotifyIcon` |
| egui | WPF + WPF-UI 4.3 + CommunityToolkit.Mvvm 8.2 + Microsoft.Xaml.Behaviors |
| `std::sync::mpsc(10000)` 日志队列 | `System.Threading.Channels.Channel<string>`（有界 10000，`DropOldest` 改为 Drop 写入方以保持 Rust 语义：满则丢弃） |
| `UiNotifier` 回调 | `IUiNotifier` → `Dispatcher.BeginInvoke` 合并 40 ms 刷新 |
| Windows Job Object（windows-sys） | P/Invoke `CreateJobObjectW/SetInformationJobObject/AssignProcessToJobObject/TerminateJobObject` |
| `Uuid::new_v4().simple()` | `Guid.NewGuid().ToString("N")` |
| `rand` | `Random.Shared`（测试可注入 `Func<double>`） |
| `chrono` 本地时间 | `DateTime.Now` / `DateTimeOffset` |
| `httpdate` | `DateTimeOffset.TryParseExact` RFC 1123 |

### 4.3 线程模型
- 每通道：一个 Kestrel 主机（自带线程池）、一个保活轮询 `Task`（5 秒或准备唤醒）、共享 `ProxyMetrics`/`KeepAliveWatchdog`（锁保护）。
- UI 线程只读快照（`MetricsSnapshot`、`KeepAliveSnapshot`、`ServiceState`），由 `IUiNotifier` 触发刷新；不轮询定时器，仅保活倒计时/准备中每秒刷新一次。
- 退出：对每个服务 `StopAsync(15 s)`，保存配置，销毁托盘。

---

## 5. 前端需求（UI/UX）

### 5.1 从 BetterGI 保留的框架能力
- `FluentWindow` + `ExtendsContentIntoTitleBar` + `WindowBackdropType=Auto`，Mica/Acrylic 切换（`ThemeType` 六态，Win11 22523 以下降级）。
- `NavigationView`（左侧 160 px 面板，`NavigationCacheMode=Enabled`，`AnimatedNavigationSelectionIndicatorBehavior`）。
- `TitleBar` 尾部按钮：切换主题、最小化到托盘。
- `SnackbarPresenter`（`ISnackbarService`）用于非阻塞提示；`ThemedMessageBox` 用于阻塞提示与确认。
- `tray:NotifyIcon`：双击还原；右键菜单 `显示窗口 / 全部启用 / 全部停用 / 退出`；关闭窗口行为由设置 `ExitToTray` 决定（默认 **退出**，与 Rust 版一致）。
- 单实例（命名管道 `InstanceBootstrap`），二次启动激活已有窗口。
- `ConfigService` 模式（任何属性变更即保存，加 200 ms 防抖）。
- 全局异常处理（`ExceptionReport`）、`WelcomeDialog`（首次运行）、`AboutWindow`、`CheckUpdateWindow`（更新源待定，首期可禁用）。
- i18n `{i18n:T 中文原文}` 机制；`I18nSync` 工具。
- `Drawer` 抽屉、`JsonCodeBox`（AvalonEdit）备用于请求详情/配置查看。
- 字体 MiSans（正文）、deluge-led（统计数字，可选）。

### 5.2 页面与控件（与 Rust 版逐项对应）

#### 首页（HomePage）
**已确认（2026-09-22）**：布局照搬 BetterGI `HomePage.xaml`，不再复刻 Rust 主窗口。M0 已实现（`src\RetryProxy.App\View\Pages\HomePage.xaml`，截图 `build\m0-home*.png`）：

1. **横幅**：高 200、圆角 8 的 `ImageBrush` 图片（暂借 BetterGI `banner.jpg`，用户日后自换）+ 左下角标题 `LLM Retry Proxy`、副标题 `本地 LLM 反向代理，自动重试，免费且开源`、链接 `点击查看文档与教程`。
2. **「代理服务，启动！」`CardExpander`**（对应 BetterGI 的「启动！」卡）
   - 头部：标题 + 说明 `服务启动后本地端口才会开始转发请求，点击展开启动相关配置。` + 右侧 `TwoStateButton`（`启动`/`停止`，绑定当前通道运行状态）。
   - 展开后五行（每行 Body 标题 + Tertiary 说明 + 右侧控件，行间 `Separator`）：`服务商` ComboBox、`通道` ComboBox、`本地监听端口` TextBox、`本通道保活` ToggleSwitch + `一键准备` 按钮、`本地监听地址` + `复制` 按钮。
   - **已确认（2026-09-22）**：`服务商` 行与 `通道` 行的 ComboBox 右侧各加三个图标小按钮 `新增` / `编辑` / `删除`，点击弹 WPF-UI `ContentDialog`，内容与 5.3 的对话框一致；通道运行中 `编辑`/`删除` 禁用（提示 `请先停用通道`）。ComboBox 项文案沿用 Rust：服务商 `{name} · {url} · {n} 通道`，通道 `{name} · {port} · {状态}`。空态提示两条沿用。
   - 文案全部走 `{i18n:T 中文}`，英文在 `User\I18n\en.json`；句号也作为 i18n 键（`"。": "."`）。
3. 页面为 `ScrollViewer` 内 `StackPanel`，`PagePadding` 与 BetterGI 相同。

**已确认（2026-09-22，用户在 a/b/c 中选 c）**：Rust 版首页的其余区域**新增两个导航页**承载——「运行状态」与「运行日志」，均为 `ScrollViewer` 内 `StackPanel` + `CardControl/CardExpander` 样板。原始规格如下，按落点分配：

#### 运行状态页（StatusPage，M5 新增）
1. **顶栏区**（页面首张卡）：`{running} / {total} 个通道在运行`、`全部启用`、`全部停用`、版本号（M0 已把 `全部启用`/`全部停用` 放进托盘菜单，页面上再放一份）。
2. **当前通道卡**：通道 ComboBox（与首页同步选中项）、状态胶囊（已停止/启动中/运行中/停止中/异常，颜色同 Rust）、`启用通道`/`停用通道`。
3. **保活与统计卡片**
   - 保活行：`本通道保活` 开关 + 空闲分钟（0.5–1440）+ `会话上限` NumberBox（步进 1000，后缀 token）+ 拆分按钮 `一键准备 ▼` / `终止准备` + 三行状态文本（`keepalive_hint`）。
   - `本地监听` 可点击复制（三态标题）。
   - 日期说明 `今日 {date} · 重启保留 [· 历史未完成 n]`，异常时 `统计日志异常`，悬停帮助全文。
   - 五个统计瓦片：今日请求 / 成功 / 重试 / 失败 / 用户处理中。
   - 缓存摘要三列（今日缓存命中 + 进度条 + 已记录写入；最近一次成功；完全未命中 + `明细` 按钮跳转缓存明细页）。
   - 请求明细列表：`request_id · 第 n 次 · 阶段 · METHOD path`，最多 66 px 高度可滚动。
   - 策略行（最大重试 / 单次·总等待 / 退避间隔），页面内不再按窗口高度隐藏。

#### 运行日志页（LogPage，M5 新增）
- 整页为日志面板，占满导航内容区高度。
- 头部：`运行日志 {shown}/{total}`、级别 chip（全部/信息/警告/错误）、`仅 {通道}` chip、`目录`、`清空`、`自动滚动` 开关、搜索框。
- 行渲染：`HH:MM:SS`（淡）+ 级别徽章 `INFO/WARN/ERR`（绿/橙/红）+ `[通道][请求ID]` 强调色 + 正文按级别色；正文首个 `HTTP ddd` 单独着色（2xx/3xx 跟随级别色，4xx 橙，5xx 红）。
- 内存缓冲 2000 行，超过一次丢 500；自动滚动：手动上滚暂停，5 秒无操作恢复；关闭后保持手动。
- 虚拟化列表（`VirtualizingStackPanel`，Recycling）。页面未显示时仍持续消费 `ProxyLogger.UiLines` 队列进缓冲，切回即见。

#### 缓存明细页（CachePage）
- **已确认**：只从左侧导航进入，不再提供「弹出独立窗口」按钮（BetterGI 无此模式）。M0 为占位卡片。
- 内容与 Rust `CacheView` 一致：标题行命中率、最近 20 次走势条（有命中/完全未命中/未计入）、筛选 chip、五列表格（完成时间/请求、模型、命中率、读取/总输入、写入），`统计范围` 折叠说明。主窗口隐藏到托盘时数据继续更新。

#### 设置页（SettingsPage）
- **已确认（M0 已实现）**：标题 `软件设置`；`软件UI语言` `CardControl`（ComboBox，选项由 `User\I18n\*.json` 枚举，切换即时生效并写入 `uiCultureInfoName`）；`开机自动启动` `CardExpander`（头部开关 = 注册表 Run 项；展开两行：`启动时最小化到托盘`、`关闭窗口时最小化到托盘`，均为 `ToggleSwitch`）。
- 主题/背景切换放在标题栏按钮（沿用 BetterGI `MainWindow`），不在设置页。
- 后续补：日志目录、每日统计目录入口。
- 沿用 BetterGI `CardControl/CardExpander` 样板。

#### 关于页
版本、许可证（GPL-3.0）、依赖致谢、检查更新（首期可隐藏）。

### 5.3 对话框（`ContentDialog` 或 `FluentWindow`，文案沿用 Rust）
- 提示（`提示` / `确定`）。
- 确认删除通道 / 服务商（`确认删除` 红色 / `取消`）。
- 新增/编辑服务商：名称、上游基础地址；校验文案同 Rust。
- 新增/编辑通道：名称、客户端（必选 Codex / Claude Code）、本地端口、最大重试次数、单次超时、总等待上限、等待生成上限、退避最小/最大间隔；悬停帮助文案同 Rust；运行中禁止编辑（`请先停用通道`）。
- 一键准备选项窗：两个单选（`当前通道，沿用本机默认配置` / `单独准备另一个供应商，开新通道`）、目标服务商 ComboBox（含 `新增服务商…`）、服务商名称/地址、API Key 密码框 + `显示`、计划文案、`开始准备`/`开新通道准备`/`取消`。
- 准备完成 / 未完成 / 已终止通知：Snackbar + 任务栏闪烁（`FlashWindowEx`），多个通道各一条不覆盖。
- 通道异常通知：`通道“{name}”异常：{error}`。

### 5.4 窗口行为
- 默认 1000×700，最小 780×540；记住本次运行内最近有效位置。
- 窗口位置异常自动恢复（复刻 `window_recovery.rs`：250 ms 确认、5 秒重试、标题栏 32 dp 与显示器工作区交集 ≥ 96×16 dp、跨显示器回落、`SWP_NOACTIVATE`，日志文案同 Rust）。
- 最小化到托盘后 `Visibility=Hidden`；后台通道与请求继续。
- 界面刷新事件驱动，无常驻定时器（保活倒计时每秒一次）。

---

## 6. 后端功能需求

以下每一条都以 Rust 现行实现为准，数值与文案见 §6 各小节及《后端功能与数据规格》《保活子系统规格》两份分析（已在本次会话产出，随 PRD 一并归档于 `docs\`）。

### 6.1 配置（Core/Config）
- 类型：`ClientType{Codex,Claude}`（序列化 `codex/claude`）、`ProviderEndpoint{name,base_url}`、`ProxyRoute`（15 字段，默认值：port 18080、max_retries 6、timeout 300、generation 300、total 600、base_delay 0.5、max_delay 4.0、keepalive_idle 3.0、context_limit 50000）、`ProxyConfig`（顶层镜像字段 + providers + routes + selected_route_id + schema_version 6 + runtime_overrides）。
- 校验规则全套（端口 1–65535、超时 0<x≤86400、退避 0≤base≤3600、max≥base、名称/地址/ID/端口重复、服务商引用存在、URL 无用户信息与 query）。错误文案逐字沿用。
- 迁移：`schema_version<6` 推断 client_type（名称含 claude 或端口 18081）；provider 级保活字段继承到 route；单地址旧格式生成 `legacy-default / 默认通道`；`total_timeout` 缺省取 `max(600, timeout)`。
- 存储：`User\config.json` 的 `Proxy` 子对象，System.Text.Json 缩进输出，键顺序固定；首次启动无 config.json 时从注册表 `ConfigJson` 一次性导入（D5）；`RETRY_PROXY_CONFIG_JSON` 环境变量注入测试配置且保存为空操作。
- 内置默认配置（anyrouter.top / sotamodel.net，两条通道 18080 Codex、18081 Claude Code）。
- 环境变量覆盖 8 项，仅作用当前选中通道，不写回。

### 6.2 服务生命周期（Core/Service）
- 状态机 `Stopped/Starting/Running/Stopping/Error`；`request_start` 仅在 Stopped/Error 时生效；绑定失败置 Error `无法监听 http://127.0.0.1:{port}：{err}`。
- 停止为硬停：取消所有处理中请求，日志 `代理服务已停止，丢弃 {N} 个处理中的请求`。
- 启动日志 `代理服务已启动：{url}（上游请求跟随系统代理）`。
- 每通道恢复当日统计并写 `已恢复 {date} 当日统计：…`。
- UI 重建服务对象时复用 `ProxyMetrics` 实例。

### 6.3 代理核心（Core/Proxy）
- 路由：`GET /_retry/health` → 健康 JSON；其余全部转发；请求体上限 100 MiB。
- 请求头：删 hop-by-hop 8 项、`Connection` 列出的 token、`host`、`content-length`、`x-retry-keepalive`、`x-retry-preparation-id`；`x-codex-turn-metadata` 中剔除 `retry_proxy_keepalive`；其余含鉴权头原样透传；流式或有 model 时加 `Accept-Encoding: identity`。
- 正文：`strip_internal_request_metadata` 剔除内部标记；`prompt_cache_key` 补全（§6.5）。
- 错误映射：取消 499 空体；总等待到期 504 `proxy_timeout`；正文错误 400 `invalid_request`；重试耗尽无完整响应 502 `upstream_unavailable`。
- 重试判定：网络错误、408、425、429、5xx；`Retry-After` 仅对 429/503 HTTP 重试生效（数字或 HTTP 日期，+0～500 ms）。
- 退避公式：基准逐次翻倍封顶 max_delay；在 `[max(base-0.5,0), min(base+0.5,max)]` 均匀随机；base=0 立即重试。
- 错误正文暂存 1 MiB；超限改完整透传并关闭本次重试；最后一次错误响应完整返回。
- 等待生成：仅 2xx + `text/event-stream` + API 路径；`GenerationGate` 事件判定表（Claude/OpenAI 两套，含 ping/keepalive/heartbeat、response.created 等待类、空 delta 等）；前缀暂存 1 MiB；生成前 EOF/网络错误/到期可重试；未知事件保守放行。
- 转发阶段：终止事件（`response.completed`/`message_stop`/`[DONE]`）到达即收尾，不等上游断开；缺终止事件以传输中断结束；总等待到期以中断结束不伪造完成。
- `metrics.success` 仅 HTTP 200 且流 outcome Complete；其他 2xx 计失败。
- 交付语义：容量 1 的通道把上游块推给下游，读空后才结清处理中计数。
- 完成/失败日志格式（`timing_text`、`attempt_prefix`、`network_error_label`、底层 `io::Error` 中文表 14 项）逐字沿用。

### 6.4 响应统计（Core/Stats）
- `BodyFormat` 判定（Content-Encoding / Content-Type / 首字节探测）。
- SSE 解码器（`\r`、`\n`、`\r\n`、BOM、`data:`/`event:`/`id:`/`retry:`/注释，8 MiB 事件上限）。
- 模型名、usage（input/output/cache_read/cache_creation/reasoning 各 JSON 指针候选）、Claude `message_delta` 的 input 覆盖规则、首字时间（首个生成内容事件）。
- 失败字段提取（上游错误码/类型/参数/未完成原因）、安全标识过滤（≤128 字节、字符白名单、拒绝 `sk-`/`sess-`/`eyj` 前缀）。
- 错误码 → 中文说明表 10 项（上游请求超限 / 可用额度不足 / 服务繁忙 / 超过长度限制 / 身份验证失败 / 内部错误 / 达到输出长度限制 / 内容审核拦截）。
- `CacheRequest` 生成与 `context_tokens(claude)` 计算。

### 6.5 缓存标识补全（Core/Cache）
- 仅 POST `/responses`、`/v1/responses`、`/chat/completions`、`/v1/chat/completions`（允许尾斜杠）；GPT 模型（`gpt-` 前缀）；排除保活、>4 MiB、压缩、非 JSON、签名/校验头。
- 客户端已有 `prompt_cache_key`（含 null/空）→ `client`。
- 会话来源优先级：`thread_id`/`session_id` 请求头 → `x-codex-turn-metadata` 头 → 正文 `client_metadata` → `conversation`；无 → `missing_session`。
- scope = SHA-256(namespace, endpoint, model, 五个鉴权头名/数量/值，长度前缀编码)；key = `rp1_` + SHA-256(前缀, scope, session)[:60]。
- 字节级插入 `,"prompt_cache_key":"…"` 于末尾 `}` 前。
- 400/422 拒绝识别三种形态；兼容重发一次不计重试；scope 进 128 容量 LRU；错误检查 64 KiB 上限。

### 6.6 日志（Core/Logging）
- 文件 `logs\retry-proxy.log`，行格式 `YYYY-MM-DD HH:MM:SS LEVEL message`，LEVEL 为 `INFO/WARNING/ERROR`；5 MiB 轮转 × 3。
- 通道前缀规则：消息以 `[` 开头 → `[通道]` 紧贴；否则 `[通道] `。
- UI 队列 10000 行有界，满则丢弃。

### 6.7 每日统计（Core/Metrics）
- 目录 `logs\daily-statistics\{SHA256(route_id) hex}\{YYYY-MM-DD}.jsonl`，头行 + 请求行 schema v1 逐字段一致。
- 幂等更新（retry 仅 attempt 递增计数；outcome 只设一次）、dirty 重试、残行截断、损坏行计数与警告文案。
- 午夜切换：跨日携带处理中请求，新一天计入 total。
- 首日无任何 jsonl 时从 `retry-proxy.log(.1-.3)` 恢复（解析规则见规格 §7.5）。
- `/_retry/health` JSON 结构（`status`、`metrics.*`、`cache`、`gpt_cache`、两个命中率）完全一致。

### 6.8 系统代理（Core/Service/SystemProxyResolver）
- 顺序：`NO_PROXY` 绕过 → 环回地址始终直连 → `{scheme}_proxy`/`ALL_PROXY` 环境变量 → 注册表 `Internet Settings`（`ProxyEnable`、`ProxyOverride`、`ProxyServer` 含 `=` 的按 scheme 取）→ 直连。
- 日志 `链路：系统代理|直连` 据此。

### 6.9 保活（Core/KeepAlive）
- 每通道独立 `KeepAliveWatchdog`：enabled、idle（≥1 s）、context_limit、last_activity、active_requests、flavor、session、totals、last_success、flight、preparation、credential、attempts、retry_at、last_error、results 队列、running_services。
- `begin_due_probe` 判定条件与顺序完全一致（无运行服务 / 有真实请求 / 已有 flight / 准备重试未到 / 未启用或未到空闲）。
- 真实请求到达取消未确认 flight 并清会话；已确认完成不清。
- 会话阈值：`context_tokens > limit`（严格大于）或缺用量 → 清理，文案两条。
- 准备重试 1500–2500 ms 随机；`cancel_preparation` 保留 credential；结果队列供 UI 弹通知。
- 5 秒轮询 + 准备唤醒；单轮超时 `min(timeout, total_timeout)`。
- 日志文案（`供应商保活 [会话 xxxxxxxx] 第 N 轮，随机题号 i/250，…`、完整回复/响应未完成/本轮已中断三类）逐字沿用。
- 内部请求识别：`x-retry-keepalive` 头、`x-codex-turn-metadata` 头/正文中的 `retry_proxy_keepalive`，全局会话表登记；内部请求用独立空 metrics、`保活-` 前缀 id，不计统计。

### 6.10 CLI 驱动（Core/Cli）
- 定位：`RETRY_PROXY_CLAUDE_CLI` / `RETRY_PROXY_CODEX_CLI` 绝对路径优先；否则 PATH 上 `.exe/.cmd/.bat/.ps1`，`.ps1` 用 `powershell -NoProfile -ExecutionPolicy Bypass -File` 包装。
- 启动：临时目录 cwd、三路管道、`CREATE_NO_WINDOW`、Job Object `KILL_ON_JOB_CLOSE`；释放时 `TerminateJobObject` + 等待 5 s + Kill。
- Codex：`codex app-server --listen stdio://` + 9 项 `-c` 禁用工具；指定 Key 时 `model_provider=retry_proxy_prepare` 五项 + 环境变量 `RETRY_PROXY_PREPARE_KEY`；握手 `initialize` → `initialized` → `config/read` 生成 `mcp_servers.*.enabled=false` → `thread/start`（ephemeral、never、read-only、developerInstructions）；`turn/start` 带 `responsesapiClientMetadata.retry_proxy_keepalive`；事件解析（delta/首字、item/completed 答案、tokenUsage、turn/completed、工具请求拒绝 `-32601`）。
- Claude Code：`claude --print --input-format stream-json --output-format stream-json --verbose --include-partial-messages --no-session-persistence --tools "" --strict-mcp-config --mcp-config {"mcpServers":{}} --disable-slash-commands --settings {…} --append-system-prompt …`；`ANTHROPIC_CUSTOM_HEADERS` 追加 `x-retry-keepalive`；指定 Key 时 settings.env + 进程环境双重覆盖，移除 `CLAUDECODE`、`ANTHROPIC_API_KEY`；事件解析（session_id、control_request 拒绝、message_start/delta、result 判定）。
- 单条输出 2 MiB 保护；答案 512 KiB；伪响应喂 `ResponseStats` 复用 usage 解析。
- `SafeCliError`：JSON-RPC 码表 5 项、协议细节分类（缺少字段/不支持字段/类型/取值 + 15 个字段白名单）、关键字分类 7 项、诊断编号 16 位十六进制、兜底文案。Key 与原始错误不落日志。
- `CliCredential` 校验三条文案；`ToString` 脱敏。

### 6.11 一键准备的通道编排（App 层）
- 默认模式：`request_preparation()`；未选/未运行两条提示；提交日志两条。
- 单独准备：`separate_channel_plan`（同服务商同类型通道复用，否则新建 `{provider} · {client}`、重名加序号、端口取配置未用且可绑定的最小值、复制原通道参数、`desired_running=true`）；保存、切页、启动、Running 后 `request_preparation_with(credential)`；Error/Stopped 的四条取消文案；日志两条。

---

## 7. 非功能需求

| 项 | 要求 |
|---|---|
| 性能 | 空闲主线程 CPU < 2%；日志 2000 行渲染不卡顿；流式转发零拷贝（`PipeReader`/`Stream.CopyToAsync` 分块 ≥ 16 KiB）。 |
| 内存 | 常驻 < 150 MB（WPF 基线）；错误/前缀暂存严格 1 MiB 上限。 |
| 安全 | API Key 只驻留内存；日志/统计/配置不含 Key、请求正文、会话编号；标识过滤规则同 Rust。 |
| 兼容 | jsonl v1、日志格式、`/_retry/health` 与 Rust 2.0.0 完全互换；配置 schema 6 语义一致但存储位置不同（config.json），仅支持从注册表单向导入。 |
| 可测 | Core 不依赖 WPF；随机数、时钟、CLI 命令可注入。 |
| 稳定 | 未处理异常走 `ExceptionReport`；通道异常不影响其他通道。 |

---

## 8. 验收标准

1. **单元/集成测试**：`rust/tests` 四个文件共约 70 个用例逐一移植为 xUnit（名称保持英文原名便于对照），全部通过；`config.rs`、`prompt_cache.rs`、`response_stats.rs`、`keepalive.rs`、`ui.rs` 中的单元测试按模块移植。
2. **`verify_rust_exe.ps1`** 十项检查点全部通过（改 EXE 名与窗口类名；托盘验证改为 WPF-UI NotifyIcon 的窗口类）。
3. **`verify_keepalive_exe.ps1`** 四种模式（默认 / Automatic / Interrupt / RetryPreparation / CancelPreparation）通过（UIAutomation 定位元素改为 WPF AutomationId）。
4. **配置导入**：Rust 版写入的注册表配置，在 C# 版首次启动（无 config.json）时被完整导入，服务商、通道、选中通道一致；之后 C# 版只读写 config.json。
5. **统计互换**：Rust 版产生的当日 jsonl 由 C# 版恢复后 `/_retry/health` 输出一致。
6. **文案对照**：附录 C 列出的全部用户可见字符串在 C# 版中逐字存在（脚本 grep 校验）。
7. **界面**：五页可导航、深浅主题切换、托盘隐藏/还原、缓存独立窗口居中与跨显示器、窗口异常位置自动恢复。

---

## 9. 工作分解与里程碑

| 阶段 | 内容 | 产出 | 预估 |
|---|---|---|---|
| M0 骨架（1–2 天） | 建目录、复制 BetterGI 框架层并裁剪到能编译运行空壳（MainWindow + 五个空页 + 托盘 + 主题 + 设置持久化）；改命名空间、图标、标题 | 可运行空壳 | 附录 A 清单 |
| M1 Core 配置与日志（1 天） | Config + 迁移 + config.json 存储 + 注册表一次性导入；RotatingFileLogger；单测 | 通过 config 单测 | **已完成（2026-09-22）**：`Core\Config` 12 个文件 + `Core\Logging` 2 个文件；30 个 xUnit 用例通过（移植 Rust 20 个 + 加载顺序/键序/文案 10 个）；实机验证注册表导入一次后只读写 config.json。`AllConfig.Proxy` 为可空的 `ProxyConfig`，用自定义 JsonConverter 写 snake_case 固定键序；改动后需显式 `Save()`。 |
| M2 Core 代理（3–4 天） | Kestrel 主机、请求管线、重试、等待生成、暂存上限、交付语义、ResponseStats、SystemProxy、health | `request_lifecycle` + `request_logging` 用例通过 | **已完成（2026-09-22）**：`Core\Proxy`（RetryProxy 管线、ProxyHost、HeaderRules、StreamLifecycle、RequestFinishGuard、ChunkSources、UpstreamException）、`Core\Service`（ProxyService 状态机、SystemProxyResolver）、`Core\Stats`、`Core\Metrics`（内存态，留 IDailyStorage 接口给 M3）、`Core\Cache`（PromptCache 全量）、`Core\KeepAlive`（模板/登记/计数，探测留 M4）。142 个 xUnit 用例通过：request_lifecycle 31、request_logging 10、proxy_integration 5+1、proxy.rs 单测 15、response_stats 33、metrics 7、prompt_cache 单测 7。语义映射：tokio biased select → 单个 linked CTS + 按「通道取消 > 内部取消 > 总等待 > 客户端断开」判定；正文流错误 → `context.Abort()`；硬停 → 先取消再 `StopAsync(100ms)`；Kestrel `MaxResponseBufferSize=0` 保证切断前字节已交付。未移植：proxy.rs 3 个交付 guard 单测（由集成用例覆盖）、依赖 CLI 的 `preparation_logs_cli_launch_failure`（M4）。 |
| M3 Core 缓存与统计（2 天） | PromptCache、兼容重发、ProxyMetrics、DailyJournal、Legacy 恢复 | `prompt_cache` 用例通过；jsonl 互换 | **已完成（2026-09-22）**：PromptCache、兼容重发、ProxyMetrics 与 prompt_cache 单测随 M2 落地；本里程碑新增 `Core\Metrics\DailyStorage.cs`（`DailyStorage : IDailyStorage` + `DailyJournal : IDailyJournal`，目录 `{日志目录}\daily-statistics\{sha256(通道 ID)}\yyyy-MM-dd.jsonl`，首行头 `{"record":"header",version/date/route_id/route_name/legacy_imported}`、其后 `{"record":"request","value":{…}}`，与 Rust serde 输出逐字一致，可互读）、`LegacyLogRestore.cs`（legacy.rs 全量移植，仅首日且目录内无任何 jsonl 时导入一次）；`ProxyMetrics.FromDailyLogs(dir, routeId, routeName)`，`ProxyService.WithDailyStatistics(logger, routeId, routeName)` 改为从 `logger.DirectoryPath` 建存储。语义映射：Rust 追加句柄 + 独立写句柄截断 → `FileStream(ReadWrite, FileShare.ReadWrite|Delete, bufferSize 1)` + 独立句柄 `SetLength`；io::ErrorKind 文案 → `IoErrorKind.Describe`（NotFound/PermissionDenied/InvalidData/…）；头不匹配抛 `InvalidDataException`。新增 25 个用例（daily.rs 9 + 头不匹配 1 + jsonl 布局 1、legacy.rs 4、`tests/prompt_cache.rs` 10），总计 167 个全部通过。 |
| M4 Core 保活与 CLI（2–3 天） | Watchdog、Probe、Codex/Claude 会话、Job Object、SafeCliError、JavaQuestions | `proxy_integration` 用例通过；假 CLI 脚本通过 | **已完成（2026-09-22）**：`Core\KeepAlive\KeepAliveWatchdog.cs` 重写为 keepalive.rs 全量（Conversation 引用计数 + InternalSessions 登记、Flight、准备状态机 Pending/Running、指定 Key、1.5–2.5 s 抖动重试、`KeepAliveProbe : IDisposable` 对应 Rust Drop）；`Core\Cli\`：CliCommand（env 绝对路径优先、PATH 上 .exe/.cmd/.bat/.ps1、.ps1 用 powershell 包装）、CliCredential、CliSession（Process 三路管道 + CreateNoWindow、Codex app-server JSON-RPC 握手/config/read/thread/start/turn/start、Claude stream-json、工具请求一律 -32601 拒绝、单条输出 2 MiB 上限）、CliReply（经 ResponseStats 归一化）、SafeCliError（码表/字段白名单/关键字分类，诊断编号为 FNV-1a 64 位十六进制）、ProcessJob（P/Invoke Job Object KILL_ON_JOB_CLOSE，释放时 TerminateJobObject + 等 5 s + Kill）；`JavaQuestions.cs` 由脚本从 Rust 题库生成。管线 `SendDueKeepAliveProbeAsync`/`SendKeepAliveProbeAsync` 按 Rust 文案写「供应商保活 [会话 xxxxxxxx] 第 N 轮…」三类日志；`ProxyService.RequestPreparationWith`。语义映射：tokio select（通道取消/会话让行/超时）→ linked CTS + 取消后按同优先级判定；管道读取不可真正取消 → WhenAny 放弃等待，读任务随进程被杀结束；单调时钟用 Stopwatch（TickCount64 粒度会让抖动到期判定偏差）。新增 52 个用例（keepalive.rs 31 + java_questions 1、keepalive_cli.rs 13 + Claude 解析/命令定位 2、proxy.rs 2、request_logging `preparation_logs_cli_launch_failure` 1、PowerShell 假 Codex CLI 端到端 1），总计 219 个全部通过。未移植：proxy.rs 3 个交付 guard 单测（集成用例覆盖）；request_logging/proxy_integration 中 8 个已被 Rust `#[ignore]` 的旧 HTTP 直发保活用例。 |
| M5 首页、运行状态、运行日志与对话框（3–4 天） | 首页卡内服务商/通道增删改按钮 + 全部对话框；新增 StatusPage（计数/全部启停/状态胶囊/保活/统计瓦片/缓存摘要/请求明细/策略行）与 LogPage（日志面板）；一键准备编排 | 手工对照 Rust 版 | **已完成（2026-09-22）**：「App 层编排」落在 `Core\Workspace\`（ProxyWorkspace 移植 app.rs 的 RetryProxyApp：RefreshServices 含服务替换、选择同步/跨服务商选通道、启停、ProviderUsage、编辑器校验与提交、保活输入回退与钳位、KeepAliveHint 三行模板、一键准备默认/单独供应商开新通道两种模式、PollPendingPreparation/PollPreparationEvents；LogLine/LogBuffer/UiText/CacheText/Editors），放 Core 是为了 xUnit 可测且不依赖 WPF。App 层：`Service\WorkspaceService`（单例；Notice → Snackbar「提示」6 s + FlashWindowEx；日志从 `ProxyLogger.UiLines` 后台消费入 LogBuffer；40 ms DispatcherTimer 合并刷新；仅当提示随时间变化且有页面订阅时开 1 s 定时器）、`Service\Dialogs`（删除确认 + 三个编辑对话框）、`View\Dialogs\`（ProviderEditorDialog/RouteEditorDialog/PrepareOptionsDialog 均继承 ui:ContentDialog，重写 OnButtonClick 校验失败不关闭）、首页卡内服务商/通道 ComboBox + 新增/编辑/删除按钮、`StatusPage`（保活提示独占一行、本地监听复制按钮、五个瓦片、缓存摘要「明细」跳转缓存页）、`LogPage`（LogLineTextBlock 用 SetResourceReference 上色的 Run、VirtualizingStackPanel 回收、自动滚动暂停 5 s、Tag NoSmoothScroll 跳过主窗口平滑滚动）。语义映射：egui 每帧轮询 → 事件驱动（NoticePosted/Refreshed/LogsChanged/Tick）；Rust `notice` 字符串 → Snackbar；`ctx.request_repaint` → RequestRefresh；Rust `flash_window` → WindowFlash。踩坑：WPF 隐式样式只作用于精确类型，ContentDialog 子类必须显式 `Style="{DynamicResource {x:Type ui:ContentDialog}}"` 才有标题/按钮与 Esc；App.xaml 的 ui:TextBox 样式需 BasedOn `DefaultUiTextBoxStyle` 才显示 Icon/Placeholder；LegacyLogRestore 改共享读取避免与日志句柄冲突（ResourceBusy）。开发配置端口改为 28080/28081，避开本机正在运行的 Rust 生产代理（18080/18081）；`build\restart-app.ps1` 只结束 D:\RetryProxy 下的进程。新增 `WorkspaceTests` 37 例（日志解析 10、UiText 4、LogBuffer 1、app.rs 移植 19、新增 3），总计 256 个全部通过；截图 `build\m5-*.png`。未移植：app.rs `running_routes_survive_provider_switches_and_client_disconnects`（依赖 egui 渲染与真实流式连接）及其余 egui 渲染断言。 |
| M6 缓存页、设置、关于（1–2 天） | CachePage 内容、SettingsPage 补日志目录入口、AboutPage、窗口恢复 | 手工对照 Rust 版；窗口恢复按验收 7 注入三种矩形 | **已完成（2026-09-23）**：`CachePage` 全量移植 `CacheView::contents`（标题行「今日 MM-dd 命中」+ 大号命中率 + 已复用/总输入 + 已记录写入；「今日最近 n 次成功请求 · 命中」；20 槽走势图用 `UniformGrid` + 按 56px 折算的柱高、未计入画 ×、每槽悬停 `request_hint`；图例；筛选 chip 全部/完全未命中/未计入 + 最新在前；五列表格（请求编号 `Hyperlink` 点击复制）；`统计范围` CardExpander 含 GPT 缓存标识段落）；页面顶部加全通道 ComboBox 与首页/运行状态页共用选中通道；进入页面时筛选复位为「全部」（对应 Rust `open()`）。`Core\Workspace\CacheFilter.cs`（Accepts/Label/Parse）与 `CacheText` 新增 DateHeadline/TokensText/RecentSummary/LegendCounts/UsageCell/EmptyRowsText/CacheKeyHelp/ClaudeHelp。`SettingsPage` 新增「日志目录」「每日统计目录」两张 CardControl（显示路径 + 打开目录，失败时 Snackbar 提示路径）；「开机自动启动」接入 HKCU Run 项（`Service\AutoStart.cs`，值名 LLM Retry Proxy，进入设置页重读）；「启动时最小化到托盘」落到 `CommonConfig.StartMinimized`，`ApplicationHostService` 以最小化状态 Show 后 Hide（让托盘图标与页面完成加载）。`AboutPage`：版本、一句话简介、项目主页；许可证 GPL-3.0 说明 + 全文链接；依赖致谢列表（BetterGI、WPF-UI 系列、CommunityToolkit.Mvvm、Kestrel、Xaml.Behaviors、Vanara、Semver、Rust 原型）；「检查更新」首期不做；删除了 M0 从 BetterGI 复制但未引用的 `AboutWindow`。窗口恢复：纯逻辑在 `Core\Workspace\WindowRecoveryMath.cs`（WindowRect/MonitorArea/WindowMetrics/RecoveryRect/RecoveryGate，`WindowRect.ToString()` 与 Rust Debug 格式逐字一致），Win32 部分在 `App\Helpers\Win32\WindowRecovery.cs`：Rust 每帧 `poll` → `HwndSource` 钩子监听 WM_WINDOWPOSCHANGED/EXITSIZEMOVE/DISPLAYCHANGE/DPICHANGED/SIZE/SHOWWINDOW 后在 Background 优先级合并检查，仅待确认/待重试期间开 300 ms `DispatcherTimer`；隐藏到托盘或关闭时 suspend；下限/默认尺寸取窗口自身 MinWidth/MinHeight/Width/Height（当前 720×480 / 900×600，非 PRD 5.4 写的 780×540 / 1000×700，沿用 M0 已验证的窗口尺寸）。日志文案同 Rust。验证：`build\check-window-recovery.ps1` 注入 (-32000,-32000,160,28)、(原位,160,28)、(-32000,-32000,1000,700) 三种，均恢复到原矩形且日志恰好 3 条 `WindowRect {..} -> WindowRect {..}`（WPF 会先把 160×28 钳到 MinWidth/MinHeight，仍判为异常）；截图 `build\m6-cache.png`（用生产版当天 jsonl 复制到开发版目录恢复出的真实数据）、`m6-settings.png`、`m6-about.png`。新增 `WindowRecoveryTests` 14 例（window_recovery.rs 12 + ToString 1 + cache.rs 筛选/文案 1），总计 270 个全部通过。未移植：cache.rs `cache_summary_and_detail_window_fit_both_themes_with_full_history`（egui 布局断言）、独立明细窗口的居中/复用逻辑（D8 已改为页面）。 |
| M7 验收与发布（1–2 天） | 两个 PowerShell 脚本适配并跑通、发布脚本、单文件 EXE、README | `dist\RetryProxy.exe` | |

合计约 14–18 个工作日。

---

## 10. 风险与对策

| 风险 | 对策 |
|---|---|
| GPL-3.0 传染 | 已接受（D1）；分发时附带 LICENSE 与源码。 |
| Kestrel 流式行为差异（自动分块、响应缓冲、499） | 关闭响应缓冲（`IHttpResponseBodyFeature.DisableBuffering`）、显式 `FlushAsync`；499 用 `Response.StatusCode = 499` + 中止连接。 |
| `HttpClient` 无读超时 | 每次 `ReadAsync` 用 linked CTS + 单次超时；连续无数据即失败。 |
| `select!(biased)` 优先级在 C# 中易错 | 封装 `PriorityWhenAny`，并用 `request_lifecycle` 用例覆盖。 |
| 字节级 JSON 插入与 STJ 转义差异 | 只在原字节末尾插入，不重序列化；单测比对原始字节。 |
| 日志格式被 Serilog 改写导致 legacy 恢复失效 | 不用 Serilog 写文件（D9）；Serilog 仅作 ILogger 桥可选。 |
| 框架依赖发布需用户预装两个运行时 | README 与首次启动缺失提示写明下载地址；发布脚本产出附带 `runtime-check.cmd`。 |
| UIAutomation 验收脚本需重写定位 | 首页控件统一设置 `AutomationProperties.Name`，与 Rust accessibility 名一致（`通道选择`、`本通道保活`、`客户端类型`）。 |

---

## 附录 A　BetterGI 迁移清单（保留 / 改造 / 删除）

### 工程级
- 保留改造：`BetterGenshinImpact.csproj` → `RetryProxy.App.csproj`；`Build\Scripts\setup_build.cmd`、`Build\MicaSetup.Tools`、`Build\micasetup`、`Build\kachina.config.json`、`Build\BetterGenshinImpact.I18nSync`、`.github\workflows\publish.yml`（裁掉 web 构建）。
- 可选：`Fischless.HotkeyCapture`（全局热键，首期不用）。
- 删除：`Fischless.GameCapture`、`Fischless.WindowsInput`、`Test\*`（重建 xUnit）、`Docs\`、`.cnb*`、mirrorchyan/repo-bot workflow、python 脚本、`.gitmodules`。

### NuGet
- 保留：WPF-UI 4.3.0、WPF-UI.DependencyInjection、WPF-UI.Tray、WPF-UI.Violeta、CommunityToolkit.Mvvm 8.2.2、Microsoft.Xaml.Behaviors.Wpf 1.1.122、Microsoft.Extensions.Hosting/DependencyInjection/Logging 9.x、Serilog.Extensions.Logging（可选）、Semver、Vanara.PInvoke.User32/NtDll、System.Drawing.Common。
- 可选：AvalonEdit、Markdig、Microsoft.Toolkit.Uwp.Notifications、Meziantou.Framework.Win32.CredentialManager。
- 删除：OpenCvSharp4、OnnxRuntime、YoloSharp、ClearScript、PresentMonFps、LibreHardwareMonitor、DryWetMidi、HungarianAlgorithm、Clipper2、CsTrees、BetterGI.Assets.*、BetterGI.VCRuntime、LibGit2Sharp、MailKit/MimeKit、Microsoft.Extensions.AI*、NCalc、XamlAnimatedGif、MouseKeyHook、System.IO.Hashing、Vanara CoreAudio/SHCore、Serilog.Sinks.RichTextBoxEx、Serilog.Sinks.File/Console、WebView2（更新功能启用时再加）、Emoji.Wpf、gong-wpf-dragdrop、Ookii、DeviceId、LazyCache、YamlDotNet、Microsoft.Extensions.Localization。

### 目录级（`BetterGenshinImpact\`）
| 目录 | 结论 | 保留内容 |
|---|---|---|
| `App.xaml(.cs)` | 改造 | Host/DI/异常/主题/字体/转换器；删 CheckIntegration、UseElevated、OCR/脚本/兑换码、游戏服务注册 |
| `Core\Config` | 改造 | `AllConfig`（去 GameTaskManager）、`CommonConfig`、`OtherConfig`（仅语言）、`Global` |
| `Core\{BgiVision,Monitor,Recognition,Recorder,Script,Simulator}` | 删除 | |
| `GameTask`、`Genshin` | 删除 | |
| `Helpers` | 改造 | `Extensions\DependencyInjectionExtensions`、`RuntimeHelper`（去 CheckIntegration/CheckSingleInstance）、`OsVersionHelper`、`UIDispatcherHelper`、`CommandLineOptions`、`Ui\WindowHelper`（去 TaskContext）、`Win32\ConsoleHelper`、`DpiAwareness\*`、`Http\HttpClientFactory`、`TempManager`、`UrlProtocolHelper`、`User32Helper`、`PrimaryScreen`、`StringUtils`、`Base64Helper`、`CultureHelper`、`DirectoryHelper`、`RegexHelper`、`ResourceHelper` |
| `Markup` | 保留 | `ConverterExtension`、`ServiceLocatorExtension` |
| `Model` | 改造 | `UpdateOption`、`Notice`、`EnumItem`、`SettingItem`、`StatusItem`、`Singleton` |
| `Properties` | 保留 | `PublishProfiles\FolderProfile.pubxml`、`launchSettings.json` |
| `Resources` | 改造 | `Fonts\MiSans-Regular.ttf`、`Fonts\deluge-led.ttf`；图标全部替换 |
| `Service` | 改造 | `ApplicationHostService`、`ConfigService`+`Interface\IConfigService`（原样沿用，去 OpenCv 转换器）、`I18n\*`、`Instance\*`（简化单实例）、`UpdateService`（可选）、`Notifier\WindowsUwpNotifier`（可选） |
| `User` | 改造 | `I18n\*.json`（重写内容） |
| `View` | 改造 | `MainWindow`、`Behavior\`（AnimatedNavigationSelectionIndicator、ClipboardInterceptor、ComboBoxPopupScroll、ResponsiveTitleBar、RightClickSelect、SliderSeek、WindowAspectRatio、WindowDrag、WindowResize）、`Converters\`（九个通用 + MillisecondsToTime）、`Controls\{Drawer,Markdown,CodeEditor,MultiSelectComboBox,TwoStateButton,Webview,WpfUiWindow,Style}`、`Windows\{ThemedMessageBox,PromptDialog,JsonMonoDialog,AboutWindow,CheckUpdateWindow,WelcomeDialog}`、`Pages\{HomePage,CommonSettingsPage}` 作模板 |
| `ViewModel` | 改造 | `ViewModel`、`IViewModel`、`Message\`、`MainWindowViewModel`（精简到 <200 行）、`NotifyIconViewModel`、`Windows\FormViewModel` |
| `Wine` | 删除 | |

### 必须解耦的耦合点
`AllConfig.OnAnyPropertyChanged → GameTaskManager`；`WindowHelper.TryApplySystemBackdrop → TaskContext`；`RuntimeHelper.CheckIntegration/CheckSingleInstance`；`ConfigService.JsonOptions` 的 OpenCv 转换器；`App.xaml` 两个游戏转换器；`MainWindowViewModel.OnLoaded/OnActivated` 游戏逻辑；`HomePage` 构造依赖 `HotKeyPageViewModel`；`ConditionalLogEventSink` 谓词读 `MaskWindowConfig`。

### 代码量
BetterGI 主工程 884 个 .cs（16.3 万行）+ 69 个 .xaml（2.7 万行），预计保留约 1.0–1.2 万行（≈6%）。

---

## 附录 B　Rust → C# 文件映射

| Rust 文件 | 行数 | C# 目标 |
|---|---|---|
| `config.rs` | 1823 | `Core\Config\*.cs`（ProxyConfig、ProxyRoute、ProviderEndpoint、ClientType、ConfigMigration、RegistryConfigStore、EnvironmentOverrides） |
| `service.rs` | 482 | `Core\Service\ProxyService.cs`、`ServiceState.cs` |
| `proxy.rs` | 2273 | `Core\Proxy\RetryProxy.cs`、`RequestPipeline.cs`、`HeaderRules.cs`、`RetryPolicy.cs`、`GenerationWait.cs`、`StreamLifecycle.cs`、`ProbeSender.cs` |
| `response_stats.rs` | 1784 | `Core\Stats\ResponseStats.cs`、`EventDecoder.cs`、`GenerationGate.cs`、`TokenUsage.cs`、`FailureSummary.cs`、`DiagnosticSanitizer.cs` |
| `prompt_cache.rs` | 573 | `Core\Cache\PromptCache.cs`、`CacheKeyState.cs`、`CacheRejectionProbe.cs` |
| `metrics.rs` + `metrics\daily.rs` + `metrics\legacy.rs` | 1768 | `Core\Metrics\ProxyMetrics.cs`、`DailyJournal.cs`、`LegacyLogRestore.cs`、`Snapshots.cs` |
| `logging.rs` | 246 | `Core\Logging\RotatingFileLogger.cs`、`RouteLogger.cs` |
| `system_proxy.rs` | 137 | `Core\Service\SystemProxyResolver.cs`（实现 `IWebProxy`） |
| `keepalive.rs` | 1663 | `Core\KeepAlive\KeepAliveWatchdog.cs`、`KeepAliveProbe.cs`、`Conversation.cs`、`InternalSessions.cs`、`KeepAliveSnapshot.cs` |
| `keepalive_cli.rs` | 1226 | `Core\Cli\CliCommand.cs`、`CodexSession.cs`、`ClaudeSession.cs`、`CliCredential.cs`、`SafeCliError.cs`、`ProcessJob.cs`、`CliReply.cs` |
| `java_questions.rs` | 267 | `Core\JavaQuestions.cs` |
| `ui_notifier.rs` | 69 | `Core\IUiNotifier.cs` |
| `ui.rs` | 6056 | `App\ViewModel\Pages\HomePageViewModel.cs`、`RouteEditorViewModel.cs`、`ProviderEditorViewModel.cs`、`PrepareDialogViewModel.cs`、`LogPanelViewModel.cs`、`App\View\Pages\HomePage.xaml`、`App\View\Controls\LogPanel.xaml`、对话框 XAML |
| `ui\cache.rs` | 863 | `App\ViewModel\Pages\CachePageViewModel.cs`、`App\View\Pages\CachePage.xaml`、`App\View\Windows\CacheDetailWindow.xaml` |
| `window_recovery.rs` | 498 | `App\Helpers\WindowRecovery.cs` |
| `icon.rs` | 121 | 静态 `Resources\Images\logo.ico/png` |
| `ui\hidden_repaint.rs` | 49 | 不需要 |
| `rust\tests\*.rs` | 4034 | `Tests\ProxyIntegrationTests.cs`、`RequestLifecycleTests.cs`、`RequestLoggingTests.cs`、`PromptCacheTests.cs` |

---

## 附录 C　文案与规格索引

所有用户可见文案、悬停帮助、校验提示、日志模板均以 Rust 源码为唯一依据，已在同目录三份分析报告中逐条摘录：

- `分析报告A-后端功能与数据规格.md`：配置常量与校验文案、代理错误响应体、日志模板、`network_error_label`、`failure_summary` 表、`/_retry/health` 结构、jsonl schema、legacy 解析规则。
- `分析报告B-保活子系统与UI全量清单.md`：保活状态机与日志模板、CLI 命令行与协议、`SafeCliError` 表、首页各区域控件与文案、`keepalive_hint` 三行模板、对话框字段与校验文案、日志面板着色规则、缓存面板文案常量、托盘菜单、窗口恢复规则、验收脚本检查点。
- `分析报告C-BetterGI框架迁移分析.md`：解决方案与工程结构、DI/Host/日志/配置/托盘/单实例/更新的实现细节、逐目录保留与删除结论、耦合点、许可证。

实施时把 `docs\` 整个目录复制到 `D:\RetryProxy\docs\`，作为 M0–M6 的逐条对照表。
