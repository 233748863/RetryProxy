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
| `src\RetryProxy.App` | WPF 程序（WPF-UI，供应商 / 统计 / 一键准备 / 运行日志；页脚软件设置 / 关于） |
| `src\RetryProxy.Core` | 配置、代理管线、统计持久化、保活与 CLI 会话、工作区编排（不依赖 WPF） |
| `src\RetryProxy.Tests` | xUnit 用例（从 Rust 版 `tests/` 与模块单测逐一移植） |
| `tests\` | PowerShell 端到端验收脚本（见下） |
| `build\` | 发布、截图与窗口恢复检查脚本 |
| `docs\` | PRD 与三份分析报告 |

## 构建与发布

启动后默认进入“供应商”。供应商、统计、一键准备、运行日志共用顶部的客户端选择，并保存上次选择；每个客户端固定一条代理通道，有供应商时随软件自动运行，手动停止只影响本次运行。

供应商按卡片展示，支持新增、编辑、复制、删除及拖动排序；每张卡片可添加多个 Key，列表仅显示密钥前后各4位（8位及以下完全隐藏）。超过3个 Key 时折叠，当前 Key 始终可见；供应商超过8个时显示搜索。点“切换”立即生效，5秒内可撤销；托盘按客户端提供相同切换入口，窗口隐藏时发送 Windows 通知。当前 Key 及其供应商不可删除；删除前备份配置，备份或保存失败时保留原数据。

供应商、Key、代理设置从右侧抽屉编辑：默认宽400，内容区窄于640时占满内容区，有未保存修改时关闭会确认。供应商可设置认证方式、主模型、Claude角色模型及1M、Codex上下文窗口与压缩阈值；“获取模型”的结果随供应商保存，重启仍有效。运行中保存地址、Key、模型、重试、压缩与保活设置后，新尝试立即生效；改端口先确认，再停止、保存、恢复原运行状态。“统计”合并请求概况与缓存明细；“代理设置”提供单客户端启停。

客户端接管与直连恢复属于下一里程碑，当前界面暂不显示接管、余额和批量准备入口；停止代理和改端口目前均不改动客户端配置。透传压缩关闭时要求上游不压缩，开启时原样转发 Accept-Encoding 和 gzip / deflate / br / zstd 响应流，同时解压解析统计与重试。

Claude 通道（含一键准备选 Claude Code 时）默认模拟 Claude Code 指纹，无需设置：用内置的 BouncyCastle 按本机 Claude Code 的握手特征建立 TLS（TLS 1.3、X25519MLKEM768、扩展顺序与 JA3 / JA4 一致），不使用 Windows 系统 TLS，证书仍按 Windows 证书库校验，支持直连及 HTTP / SOCKS5 系统代理；同时在入站连接上记录客户端请求头的原始顺序与大小写，出站时按原样重排（Kestrel 与 HttpClient 默认都会改动顺序）；Codex 通道同样默认按 Codex 原顺序与大小写转发请求头，TLS 仍用 Windows 系统 TLS（Codex 在 Windows 上也用它，握手特征本就一致），客户端没发 Accept-Encoding 时代理也不补。通道运行期间每 12 小时在后台运行一次本机 `claude`，把地址临时指向本机端口抓取握手首包（不发出 API 请求、不消耗额度），指纹变化且能完整复现时自动改用并保存到 `User\claude-code-client-hello.bin`，否则继续使用现有指纹；“软件设置”提供语言、外观、启动与托盘设置。后台准备与保活从 20 条短回复提示中随机抽取，每轮只请求一个简短回答，不调用工具。

左侧“一键准备”是独立功能，可新增多项准备任务，每项自行选择 Codex 或 Claude Code、供应商、模型与保活间隔，并单独启停。准备任务按当前客户端筛选，分为“跟随当前”和“手动填写”；开始时从软件管理的当前供应商与 Key 解析凭据，运行中的任务沿用开始时的配置，不读取真实客户端配置中的密钥；也可手填供应商地址与 API Key，模型支持获取后选择或直接输入。准备任务拥有独立后台代理和会话；准备期间代理先暂存回复，仅将完整、含有效上下文用量与文本的结果交给客户端，其余结果由代理在每轮 600 秒内不限次数地重试，到期向客户端返回 HTTP 504，随机等待 1.5～2.5 秒后开始下一轮准备；取得上下文后按设定间隔保活（默认 5 分钟，可设 0.5～1440 分钟），直到手动停止。已停止的任务可修改设置并重新开始，运行中的任务沿用启动时的地址与密钥。任务设置（客户端、供应商来源与地址、手填的 API Key、模型、思考强度、保活间隔）在新增、修改或删除时写入 `User\config.json` 的 `preparations` 节点，其中手填的 API Key 未加密；跟随当前模式不在任务中另存密钥，每次开始时重新解析。重启软件后任务列表按保存的设置恢复，一律显示为已停止，需手动开始；后台代理与会话只在本次运行有效。不写入已有服务商、通道或客户端配置。日志全过程归入“一键准备”，持续标明任务编号、客户端及“准备 / 独立保活 / 服务”行为，不记录 API Key。

运行日志按“通道代理 / 通道保活 / 一键准备 / 系统”筛选来源，可叠加级别和关键字搜索；“仅当前客户端”同时筛选该客户端的通道与独立准备日志；准备任务的“查看日志”会直接按任务筛选，准备完成后的保活、停止和异常仍保留原任务标识。每次请求及后台问答沿用开始时的来源和行为，避免状态切换后收尾日志被分错类；文件与界面使用相同标识，例如 `[一键准备][准备 1 · Codex][独立保活][请求 …]`。

```powershell
dotnet build RetryProxy.sln
dotnet test src\RetryProxy.Tests\RetryProxy.Tests.csproj
powershell -File build\publish.ps1          # 先跑测试，再发布到 dist\RetryProxy.exe（单文件、框架依赖、win-x64）
powershell -File build\publish.ps1 -SkipTests
```

代理设置中的“思考强度”按客户端通道保存，从下一轮后台问答生效；一键准备的强度由每项任务单独选择，准备和之后的独立保活共用该设置。默认沿用客户端配置，其余档位需要模型与供应商支持；修改强度不会改写本机客户端配置。Claude 的认证方式按供应商设置选择 Bearer 或 x-api-key；手填准备供应商默认 Bearer。获取模型按钮独立使用手填地址与密钥，并按系统网络设置选择代理，不读取普通通道配置；遇到认证型 HTTP 401/403 时在同一地址切换一次认证格式，网关返回网页式 403 时停止重试并提示拦截原因。实际准备单独启动本机 CLI，仅覆盖本次的地址、密钥、模型与思考强度；普通通道透传客户端鉴权，一键准备的临时代理按所选模式转发。

## 验收脚本

```powershell
# 代理、等待生成重试、缓存合并、两次重启后统计一致（无界面部分，任意 PowerShell）
powershell -File tests\verify_exe.ps1
# 追加托盘隐藏/还原、缓存页、隐藏期间流式请求、窗口异常位置恢复、关闭退出（在私有桌面运行）
powershell -File tests\verify_exe.ps1 -VerifyTray
# 独立准备：空通道启动、模型获取、完成后保活、两项任务分别启停（私有桌面）
pwsh -File tests\verify_preparation.ps1
# 追加供应商/统计跨页客户端同步、准备任务独立启停与日志筛选
pwsh -File tests\verify_preparation.ps1 -WithChannels
# 较小窗口中的抽屉、准备对话框与客户端切换（私有桌面）
pwsh -File tests\verify_preparation.ps1 -WithChannels -CompactWindow
# 通道自动保活与真实请求让行（旧通道准备入口的验收移入准备脚本及 xUnit）
pwsh -File tests\verify_keepalive.ps1 -Automatic -NoScreenshot
# 供应商卡片、嵌套 Key 编辑、即时切换/撤销、隐藏托盘切换、草稿取消与窄抽屉
pwsh -File tests\verify_providers.ps1
```

验收时显式传入 `-ExePath` 指向开发构建产物；测试复制程序到临时目录，使用独立端口和客户端目录，不停止或覆盖正在使用的 dist 实例。

## 配置与数据

- 配置文件：`User\config.json`（程序目录下）。首次启动且没有该文件时，会一次性导入 Rust 版留在注册表 `HKCU\Software\LLM Retry Proxy\ConfigJson` 的配置。
- 配置版本 schema 7（2026-09-29 起）：服务商按客户端（Codex / Claude Code）分开，可挂多个 API Key（明文保存）；每个客户端固定一条通道（Codex 默认 18080、Claude Code 默认 18081）。旧版配置在首次打开时自动升级：同一服务商被两个客户端使用时各拆一份，同一客户端有多条通道时只保留选中的那条（其次是第一条），通道 ID 不变、当日统计照常累加；升级前原文件备份到 `User\backup\`（只保留最近 5 份），升级内容写入日志。不支持退回旧版本。
- 配置先写临时文件再整体替换，写到一半中断也不会损坏原文件；配置文件读不出来或内容无效时，先备份原文件，本次运行使用默认设置且不保存任何修改，修正或删除 `User\config.json` 后重启即可。
- 日志：`logs\retry-proxy.log`（轮转 `.1`～`.3`）；当日统计：`logs\daily-statistics\<sha256(通道 ID)>\yyyy-MM-dd.jsonl`，与 Rust 版逐字兼容、可互读。
- 健康与统计：`http://127.0.0.1:<端口>/_retry/health`。
- 访问限制（2026-09-29 起）：通道只接受本机客户端的请求，Host 须为 `127.0.0.1` 或 `localhost`，带 `Origin` 头（网页发起）的请求一律拒绝，健康检查同样受限；被拒绝时返回 HTTP 403，日志每分钟最多记一条。
- 上游地址（2026-09-29 起）：Claude Code 把请求路径原样接在服务商地址后；Codex 的客户端地址为 `http://127.0.0.1:<端口>/v1`，服务商地址没有路径时按 `<地址>/v1` 处理，例 `https://anyrouter.top` → `https://anyrouter.top/v1/responses`，`https://new.sharedchat.cc/codex` → `https://new.sharedchat.cc/codex/responses`。
- 本地口令（2026-09-29 起）：每条通道在 `config.json` 里有一个随机本地口令。带本地口令的请求由代理换成当前服务商的当前 Key，并按服务商的模型设置改写模型；切换 Key 立即生效，还没向客户端输出的请求改用新 Key 重发。不带口令的请求（客户端自带 Key 的原用法）原样透传，请求完成行记为"服务商 · 客户端凭据"；切换服务商时，在途的这类请求留在原服务商上走完，不会把客户端的密钥发给新服务商。日志与健康检查不出现 Key 或口令。供应商页已支持管理 Key、切换及撤销；客户端接管随后续里程碑提供。
- 环境变量：`RETRY_PROXY_CONFIG_JSON`（整份配置注入，测试用，注入时不写配置文件）、`RETRY_PROXY_CODEX_CLI` / `RETRY_PROXY_CLAUDE_CLI`（指定本机 CLI 路径，支持 `.ps1`）。

## 许可证

GPL-3.0，见 `LICENSE`。依赖致谢见程序内「关于」页。
