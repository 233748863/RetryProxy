# LLM Retry Proxy（C# / WPF）

本地 LLM 反向代理：自动重试、等待生成、暂存回复、缓存标识补全、保活与当日统计。本仓库是 Rust 版（egui）的 C# WPF 重写，界面底子来自 [BetterGI](https://github.com/babalae/better-genshin-impact)，以 GPL-3.0 发布。

## 运行要求

框架依赖发布，运行前需安装两个 x64 运行时（下载：<https://dotnet.microsoft.com/download/dotnet/9.0>）：

- .NET Desktop Runtime 9（`Microsoft.WindowsDesktop.App`）
- ASP.NET Core Runtime 9（`Microsoft.AspNetCore.App`，本地监听用 Kestrel）

`dist\runtime-check.cmd` 会检查两者是否齐全。缺失时首次启动会由 .NET 宿主弹出下载提示。

## 目录

| 路径 | 说明 |
|---|---|
| `src\RetryProxy.App` | WPF 程序（WPF-UI，首页 / 一键准备 / 运行状态 / 运行日志 / 缓存明细 / 设置 / 关于） |
| `src\RetryProxy.Core` | 配置、代理管线、统计持久化、保活与 CLI 会话、工作区编排（不依赖 WPF） |
| `src\RetryProxy.Tests` | xUnit 用例（从 Rust 版 `tests/` 与模块单测逐一移植） |
| `tests\` | PowerShell 端到端验收脚本（见下） |
| `build\` | 发布、截图与窗口恢复检查脚本 |
| `docs\` | PRD 与三份分析报告 |

## 构建与发布

通道选择与通道保活参数在首页管理；左侧“一键准备”是独立功能，可新增多项准备任务，每项自行选择 Codex 或 Claude Code、供应商、模型与保活间隔，并单独启停。无需创建或启动通道，切换、修改、停用或删除通道均不影响准备任务。本机供应商模式在开始时读取所选客户端的当前地址与密钥（Codex 读取 `.codex/config.toml` 和 `.codex/auth.json`，Claude Code 读取 `.claude/settings.json`）；也可手填供应商地址与 API Key，模型支持获取后选择或直接输入。准备任务拥有独立后台代理和会话，使用独立的默认超时设置，每轮上游请求只尝试一次，未取得上下文时持续重试；取得后按设定间隔保活（默认 5 分钟，可设 0.5～1440 分钟），直到手动停止。已停止的任务可修改设置并重新开始，运行中的任务沿用启动时的地址与密钥。配置、密钥与会话只保留在本次运行的内存中，关闭软件后清空；不写入已有服务商、通道或客户端配置。过程进入普通日志，分别用“准备”和“保活”标签筛选，不记录 API Key。

```powershell
dotnet build RetryProxy.sln
dotnet test src\RetryProxy.Tests\RetryProxy.Tests.csproj
powershell -File build\publish.ps1          # 先跑测试，再发布到 dist\RetryProxy.exe（单文件、框架依赖、win-x64）
powershell -File build\publish.ps1 -SkipTests
```

首页的“思考强度”按通道保存，从下一轮后台问答生效；一键准备的强度由每项任务单独选择，准备和之后的独立保活共用该设置。默认沿用客户端配置，其余档位需要模型与供应商支持；修改强度不会改写本机客户端配置。Claude 模型列表请求遇到 HTTP 401/403 时，会在同一地址改用 Bearer 认证（通过 Authorization 请求头携带密钥）重试一次，兼容采用 OpenAI 模型目录的供应商。

## 验收脚本

```powershell
# 代理、等待生成重试、缓存合并、两次重启后统计一致（无界面部分，任意 PowerShell）
powershell -File tests\verify_exe.ps1
# 追加托盘隐藏/还原、缓存页、隐藏期间流式请求、窗口异常位置恢复、关闭退出（在私有桌面运行）
powershell -File tests\verify_exe.ps1 -VerifyTray
# 独立准备：空通道启动、模型获取、完成后保活、两项任务分别启停（私有桌面）
pwsh -File tests\verify_preparation.ps1
# 追加首页通道切换后准备任务仍保持运行
pwsh -File tests\verify_preparation.ps1 -WithChannels
# 较小窗口中的弹窗布局与客户端切换（私有桌面）
pwsh -File tests\verify_preparation.ps1 -WithChannels -CompactWindow
```

## 配置与数据

- 配置文件：`User\config.json`（程序目录下）。首次启动且没有该文件时，会一次性导入 Rust 版留在注册表 `HKCU\Software\LLM Retry Proxy\ConfigJson` 的配置。
- 日志：`logs\retry-proxy.log`（轮转 `.1`～`.3`）；当日统计：`logs\daily-statistics\<sha256(通道 ID)>\yyyy-MM-dd.jsonl`，与 Rust 版逐字兼容、可互读。
- 健康与统计：`http://127.0.0.1:<端口>/_retry/health`。
- 环境变量：`RETRY_PROXY_CONFIG_JSON`（整份配置注入，测试用，注入时不写配置文件）、`RETRY_PROXY_CODEX_CLI` / `RETRY_PROXY_CLAUDE_CLI`（指定本机 CLI 路径，支持 `.ps1`）。

## 许可证

GPL-3.0，见 `LICENSE`。依赖致谢见程序内「关于」页。
