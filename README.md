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

通道选择与保活参数在首页管理；点击“一键准备”选择本通道供应商或临时配置新供应商。本通道模式实时读取 CCC Switch 写入本机 `.codex/config.toml`、`.codex/auth.json`（Claude Code 通道读取 `.claude/settings.json`）的当前地址与密钥，可获取模型后选择或自行输入；新供应商模式手填地址和 API Key。独立后台代理只在内存中注入密钥，准备不修改已有服务商、通道或配置文件；每轮上游请求只尝试一次，未取得上下文时持续启动下一轮，取得后按弹窗设定的间隔保活（默认 5 分钟，可设 0.5～1440 分钟），直到手动停止；重启后临时设置与会话消失。过程进入普通日志，可用“保活”标签筛选，不会记录 API Key；旧版曾创建的通道不会自动删除。

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
