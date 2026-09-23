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
| `src\RetryProxy.App` | WPF 程序（WPF-UI，首页 / 运行状态 / 运行日志 / 缓存明细 / 设置 / 关于） |
| `src\RetryProxy.Core` | 配置、代理管线、统计持久化、保活与 CLI 会话、工作区编排（不依赖 WPF） |
| `src\RetryProxy.Tests` | xUnit 用例（从 Rust 版 `tests/` 与模块单测逐一移植） |
| `tests\` | PowerShell 端到端验收脚本（见下） |
| `build\` | 发布、截图与窗口恢复检查脚本 |
| `docs\` | PRD 与三份分析报告 |

## 构建与发布

通道选择、单通道启停、保活参数与“一键准备”统一在首页管理；运行状态页展示首页所选通道的统计和保活状态，并保留全部通道的启停操作。“一键准备”可选择本机默认配置、修改当前通道的独立配置，或新建独立通道。独立通道拥有自己的服务商地址、模型和 API Key，即使与其他通道地址相同也不会复用；可在当前通道重新配置。密钥通过 Windows 当前用户加密后存入配置，重启后在同一用户下恢复；模型与地址一并保存。不更改本机 CLI 配置，也不会在日志中输出密钥。旧版创建但尚未保存密钥的通道，可以选择“在当前通道独立准备”重新输入一次密钥与模型。

```powershell
dotnet build RetryProxy.sln
dotnet test src\RetryProxy.Tests\RetryProxy.Tests.csproj
powershell -File build\publish.ps1          # 先跑测试，再发布到 dist\RetryProxy.exe（单文件、框架依赖、win-x64）
powershell -File build\publish.ps1 -SkipTests
```

## 验收脚本

```powershell
# 代理、等待生成重试、缓存合并、两次重启后统计一致（无界面部分，任意 PowerShell）
powershell -File tests\verify_exe.ps1
# 追加托盘隐藏/还原、缓存页、隐藏期间流式请求、窗口异常位置恢复、关闭退出（在私有桌面运行）
powershell -File tests\verify_exe.ps1 -VerifyTray
# 保活：一键准备 / 自动保活 / 让行真实请求 / 前两次失败后重试 / 终止准备（PowerShell 7 + UIAutomation，各模式分别运行）
pwsh -File tests\verify_keepalive.ps1
pwsh -File tests\verify_keepalive.ps1 -Automatic
pwsh -File tests\verify_keepalive.ps1 -Interrupt
pwsh -File tests\verify_keepalive.ps1 -RetryPreparation
pwsh -File tests\verify_keepalive.ps1 -CancelPreparation
```

## 配置与数据

- 配置文件：`User\config.json`（程序目录下）。首次启动且没有该文件时，会一次性导入 Rust 版留在注册表 `HKCU\Software\LLM Retry Proxy\ConfigJson` 的配置。
- 日志：`logs\retry-proxy.log`（轮转 `.1`～`.3`）；当日统计：`logs\daily-statistics\<sha256(通道 ID)>\yyyy-MM-dd.jsonl`，与 Rust 版逐字兼容、可互读。
- 健康与统计：`http://127.0.0.1:<端口>/_retry/health`。
- 环境变量：`RETRY_PROXY_CONFIG_JSON`（整份配置注入，测试用，注入时不写配置文件）、`RETRY_PROXY_CODEX_CLI` / `RETRY_PROXY_CLAUDE_CLI`（指定本机 CLI 路径，支持 `.ps1`）。

## 许可证

GPL-3.0，见 `LICENSE`。依赖致谢见程序内「关于」页。
