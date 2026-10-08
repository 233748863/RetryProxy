# PRD：数据存储迁移（SQLite）

日期：2026-10-04。状态：M1–M5 全部完成，已获当次授权并于 2026-10-05 12:06 正式发布；发布及保护结果见 §9。

## 0. Context

2026-10-04 陛下决定本项目的数据从文本文件迁移到 SQLite，参照 CC Switch（本机 `~/.cc-switch/cc-switch.db` 为 SQLite 库，139 MB，含请求日志 105,864 条、会话日志同步 2,632 条、按日汇总 1,149 条；其自身运行日志仍为文本）。

迁移前现状（全部为文本）：
- 配置：`User\config.json`（schema 7；供应商、Key、通道、接管状态、准备任务、界面偏好），破坏性操作前备份到 `User\backup\`（保留 5 份）。
- 每日统计：`logs\daily-statistics\{sha256(通道 ID)}\{yyyy-MM-dd}.jsonl`（约 72 MB），追加 + 末行覆盖，启动时重放恢复。
- 请求诊断：`logs\request-diagnostics\{日期}-{会话}.jsonl`（约 38 MB），保留含今天的 7 个本机自然日。
- 运行日志：`logs\retry-proxy.log`（5 MiB × 3 轮转）——保持文本，不迁移。

## 1. 已确认决策

| 编号 | 决策 | 日期 |
|---|---|---|
| S1 | 迁移范围：配置 + 每日统计 + 请求诊断，全部进 SQLite | 2026-10-04 |
| S2 | 历史数据不导入；旧 JSONL/JSON 文件保留在磁盘、代码不再读写 | 2026-10-04 |
| S3 | 运行日志保持文本（与 CC Switch 相同） | 2026-10-04 |
| S4 | 上线当天从运行日志恢复当天统计，沿用现有"首日恢复"机制；更早历史不导入 | 2026-10-04 |
| S5 | 设置页目录入口：合并为一张"日志与数据"卡（打开 logs\，含 retry-proxy.log 与 data.db），删除"每日统计目录"卡；"配置和备份"卡不变 | 2026-10-04 |

## 2. 数据库设计

两个库文件。理由：配置库小、珍贵、参与现有"破坏性操作前备份"；数据库大、可丢弃、按天保留；故障域隔离，验收脚本可沿用"注入时不写配置"的文件级检查。

### 2.1 配置库 `User\config.db`

表：
- `meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)`：`schema_version`（库结构版本，独立于配置 schema 7）、`created_at`。
- `config(key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL)`：key 为 `proxy` / `common` / `other` / `preparations`；value 是各节点的现有 JSON 序列化（`proxy` 仍用 `ProxyConfigJson` 规范格式）。序列化格式不变，只把"整文件替换"换成"单事务写 4 行"。

语义保持（对应《PRD-供应商管理》§8）：
- 200 ms 防抖、`SaveChecked` 失败抛出、`OnAnyChangedAction` 触发方式不变。
- 读取失败（库打不开 / 行缺失 / JSON 损坏）：备份 + 阻止本次运行写盘 + 提示，文案指向 `User\config.db`；不使用默认配置覆盖。
- 破坏性操作（删除供应商/Key、接管）前：把全部配置导出为 `User\backup\config_yyyyMMdd_HHmmss_fff.json.bak`（人可读，保留最近 5 份，命名与现规则一致）；导出失败则中止操作。
- 恢复路径：先退出程序，另行保留故障 `config.db` 和存在的 `-wal`、`-shm`，再移走整组库文件，把 JSON 备份复制为 `User\config.json`，重启一次性导入。不要在程序运行中只删除主库。
- 一次性导入：`config` 表为空且存在 `User\config.json` 时，按现有解析与 schema 迁移逻辑导入；先按现规则备份原文件，备份失败则中止导入并阻写，导入成功后把原文件改名为 `User\config.json.migrated.bak`。
- 注册表一次性导入（`HKCU\Software\LLM Retry Proxy\ConfigJson`）保留，导入目标改为数据库。
- `RETRY_PROXY_CONFIG_JSON` 注入：读取照旧、保存为空操作，且不创建 `config.db`（验收脚本据此检查）。
- 配置 schema 7 的字段、键序与迁移规则不变，本文只改存储位置。

### 2.2 数据库 `logs\data.db`

表 `daily_requests`（每日统计）：
- 列：`route_id`、`date`、`request_id`、`updated_at_ms`、`sequence`、`outcome`、`retry_count`、`key_id`、`key_name`、`record_json`（DailyRequest 全量 JSON，保真，缓存明细含在其中）。
- 主键 `(route_id, date, request_id)`；UPSERT 取代"追加 + 末行覆盖"，重复写入幂等。索引 `(route_id, date)`。
- 启动恢复：按 `(route_id, date)` 读回当天记录重建内存态（含缓存页最近 20 条的顺序规则）。
- 7 天趋势与按日汇总走 SQL（`json_extract` 读取用量字段后按现行口径计算，口径变化仍作用于历史数据，与现行为一致），不整表解析。
- 保留：永久（与现状一致）。午夜切换、跨日携带、`保活-` 前缀排除、dirty 重试、警告文案等规则不变，仅落盘端替换。

表 `daily_imports`（一次性导入标记）：`(route_id, date)` 主键；有行表示该通道该天曾从旧运行日志恢复（对应旧 jsonl 头的 `legacyImported` 标志，重启后统计页仍提示"今日数据包含旧日志恢复记录"）。导入的记录与该标记在同一事务写入，导入失败下次打开整体重试。

表 `diagnostic_events`（请求诊断）：
- 列：`id`、`session_id`、`request_id`、`date`、`sequence`、`kind`、`entry_json`、`request_json`（首个事件）、`payload_bytes`、`created_at_ms`；唯一约束 `(session_id, request_id, sequence)`；索引 `(date, request_id, sequence)`。
- 查询：按日期取当日事件后台重放（沿用现有 SummaryState 归并逻辑），详情按请求分页；"单日缓存、可取消、不在界面线程、不一次加载 7 天"的语义保持。
- 保留：含今天的 7 个本机自然日，启动与跨午夜后台清理；删除失败提示语义保持。
- 容量：单日 64 MiB 上限改按 `payload_bytes` 汇总控制；会话（每次运行的段）概念保留（PreviousSession 标记、实时合并）。2026-10-08 优化：数据结构 v3 新增 `diagnostic_request_marks`，缺失优先标记到具体请求，每天及待保存队列分别最多 4096 个；旧会话/日标记及超限退回的日期标记保留在 `diagnostic_marks`，仅提示当日有无法完整定位的遗漏，不影响完整请求的判断。
- 写入：非阻塞入队、单写者、每秒或累计 256 项落库；退出最多等待 2 秒。

### 2.3 通用

- 组件：`Microsoft.Data.Sqlite` 9.0.x（MIT）。`journal_mode=WAL`、`synchronous=NORMAL`、`busy_timeout`；每库一个写线程（沿用现有"队列 + 单写者"模式）。
- 发布：`SelfContained=false + PublishSingleFile=true` 不变；SQLite 原生库随单文件打包（`IncludeNativeLibrariesForSelfExtract=true`），`dist` 仍只有 `RetryProxy.exe` + `runtime-check.cmd`；M1 已完成发布产物结构检查与单文件冒烟（2026-10-04，内嵌原生库提取运行正常，SQLite 3.53.3）；M2 隔离发布包（`.tmp/release-sqlite-daily-stats-20261004`，58.0 MB）实机开库验证通过（2026-10-04：四套件首轮 exit=0，含两次强制重启的统计 health 对比与轮转日志删除不变；其后修复停止时丢弃计数竞态（既有竞态，与存储无关：RequestStop 改为取消前先记「处理中」）并重发，providers、proxy-tray 复跑 exit=0，managed-preparation、keepalive 复跑因机器内存不足中止、首轮已通过）。M3 请求诊断迁移验收：verify_request_diagnostics 改造为经 logs\data.db 断言，实机 44 项全过（2026-10-05，现场 .tmp/request-diagnostics-verify-20261004-r8）；期间修复复制误报（剪贴板同步工具占用剪贴板时写入失败，ClipboardSafe 让出 UI 线程后异步重试）。
- 单实例互斥不变；隔离验收实例各自 runtime 目录、各自建库，互不影响。
- 旧文件保留：`logs\daily-statistics\`、`logs\request-diagnostics\`、`User\config.json.migrated.bak` 保留在磁盘，不再读写。

## 3. 有意变更（取代旧文档条款）

| 原条目 | 变更 |
|---|---|
| 《PRD-CSharp-重构》§8.5 统计互换（Rust jsonl 互读） | 终止 |
| 《PRD-CSharp-重构》§8.4 配置导入验收、D5/§6.1 存储描述 | 存储改为数据库；导入路径改为"注册表 → 数据库" |
| 《PRD-供应商管理》§8 存储/备份/读失败文案、§13 兼容行 | 按 §2.1 取代 |
| 《PRD-请求诊断》§4 文件存储描述 | 按 §2.2 取代 |
| README 数据位置章节 | 已按两库、旧文件保留和安全恢复路径同步；最终验收记录随 M5 补齐 |
| 设置页"每日统计目录"入口 | 按 S5 合并为"日志与数据"卡 |
| 首日恢复触发条件（原：该通道首次出现统计文件） | 改为"该通道在库中没有任何统计记录"；切换日行为不变，导入后不再重复 |
| 无统计文件的日子（原） | 改为按库中该日无行判断：`HasData=false`、显示为空；有请求但缺 token 的行只计入请求数 |

## 4. 验收标准

1. 全量 xUnit 通过；基线 1258 项（M2 后 1270 项：+12 基础设施、−13 旧 DailyJournalTests、+12 DailyStorageTests、+1 吞吐冒烟；M3 后 1269 项：DiagnosticStoreTests 38→37 行，其余为改写、无增减）。
2. `verify_exe.ps1`（含 `-VerifyTray`）通过：注入时不创建 `User\config.db`；已有空库不初始化、旧库结构不升级、原 journal_mode 不改变；两次重启统计恢复（health 对比）不变；轮转日志删除不影响统计。
3. `verify_request_diagnostics.ps1` 改为经数据库断言：记录、筛选、详情、7 天保留、正常/异常重启。
4. 配置导入：以 `dist` 真实结构导入后两条通道 ID 不变、供应商/Key/准备任务完整；原文件改名保留。
5. 备份：删除 Key 前导出 JSON 备份并保留 5 份；导出失败中止删除。
6. 故障：只读库可读取有效配置，但保存必须拒绝、有提示且原库不变；损坏库或无效节点读取失败时备份并阻写。诊断显示"记录不完整"且不阻塞转发。
7. 隔离验收实例的两个库生成在各自 runtime 目录内；发布包为单文件且实机可运行。
8. 迁移后旧 jsonl 目录不再被读写（文件时间戳不变）。

## 5. 里程碑

| 阶段 | 内容 | 状态 |
|---|---|---|
| M1 基础设施 | 引入 SQLite；两库建表与版本管理；写队列；单测；发布产物结构检查（单文件内嵌原生库） | 已完成（2026-10-04） |
| M2 每日统计迁库 | 写入/恢复/趋势查询；切换日恢复（按 S4）；统计相关用例调整；verify_exe 统计检查通过；单文件实机开库验证 | 已完成（2026-10-04） |
| M3 请求诊断迁库 | 事件表写入/查询/详情/保留/容量；诊断用例改写；verify_request_diagnostics 改造 | 已完成（2026-10-05） |
| M4 配置迁库 | ConfigService 换库；一次性导入；备份导出；注入/注册表路径；相关验收脚本改造 | 已完成（2026-10-05）；配置迁移、备份保护、注入只读及最终整包验收通过 |
| M5 清理与发布 | 删除 jsonl 代码路径；设置页入口（按 S5）；README/三份 PRD 同步；全量验收；发布需陛下授权 | 已完成（2026-10-05 12:06）；已获当次授权并替换正式实例，配置迁移、健康、接管及窗口复核通过，见 §9 |

## 6. 风险

| 风险 | 对策 |
|---|---|
| 单文件发布时原生库提取失败/被拦截 | M1 已验证单文件内嵌原生库可提取运行（2026-10-04，冒烟程序 sqlite=3.53.3）；M2 主程序实机开库复验 |
| 库损坏导致配置丢失 | 破坏性操作前 JSON 导出；导入前备份；WAL + 单写者；恢复路径见 §2.1 |
| 统计/诊断写入影响转发性能 | 沿用队列 + 单写者 + 批量事务；M2 吞吐冒烟通过（1000 次 UPSERT，上限 10 秒，整条用例实测 240 ms）；M3 诊断批量事务已实机验收（44 项全过） |
| 迁移期间数据断层 | S2 已接受（不导入）；S4 已确认从运行日志恢复当天统计 |
| 回退旧版本 | 旧文件保留；回退后统计从旧文件继续，迁移期数据不合并（接受） |

## 7. 未决

（无：S4、S5 已于 2026-10-04 拍板，见 §1。）

## 8. 2026-10-05 开发与隔离验收记录（正式替换前）

- 配置保护已落地：旧 JSON 备份失败中止导入；故障备份优先生成包含已提交 WAL 的一致副本，无法读取时保留主库与 WAL，备份失败明确提示；既有库 `proxy:null` 阻写，不回退后覆盖。注入读取改用真实只读连接，已有空文件不建表，既有库不升级结构或改变日志模式。独立 Windows 应用层回归覆盖这些分支、四节点事务、最近五份 JSON 备份、删除失败回滚、注册表读取边界与防抖；Core 测试项目仍不依赖 WPF。
- 清理与专项验收已完成：运行代码不再包含旧 JSONL 存储路径；设置页合并“日志与数据”，中英文文案同步。验收共用运行文件复制器，开发构建包含 `runtimes` 原生依赖，单文件包无需散装原生文件。新增 `verify_config_storage.ps1`，真实配置只读副本在关闭保活/准备、清除接管路径后，使用随机本机端口验收；九项配置导入、重启、旧文件保留、注入和故障检查通过，深浅主题与 760×600 完整目录卡截图已查看。删除 Key 的实机验收增加删除前 JSON 备份内容检查。发布脚本已覆盖所有 `*Tests` 工程。
- 证据与保护：本轮统一目录为 `.tmp/sqlite-resume-20261005/`。首次外观窗口启动等待超时，未改应用或放宽等待，原样复测通过，根因未确认；脚本的空环境变量、共享读取、运行态比较和滚动容器定位问题均保留失败现场后修正。配置注入初始化空库的修复前失败回归也已保存。正式程序与客户端文件持续核对；正式配置运行期间发生变化，保留当前文件，未覆盖。最终整包结果：Release 1311 项通过（Core 1275 + App 36），Debug 两工程分别通过同样数量；13 个验收套件均 exit 0，其中配置真实副本与合成配置各 9 项、请求诊断 44 项全部通过。辅助 Python 8 项通过，运行文件复制与托盘诊断各 5 项在 PowerShell 7 和 Windows PowerShell 均通过。最终目录卡与诊断的深浅主题、760×600 窄窗截图已实际查看。结束时 8 个受保护文件均与开始时逐字节一致，正式 PID 19308 保持运行，18080/18081 均为 ok 且仍归该进程，未留下测试实例。未替换 dist、未提交、未推送。

最终发布候选包：`.tmp/sqlite-resume-20261005/package-verified/RetryProxy.exe`，60866528 字节；SHA-256 `9cd2b51e0f0caadda1c7d71e6b99fb4a834ff4e743db13da4ea6fd96a791652f`。机器汇总：`.tmp/sqlite-resume-20261005/final-summary.json`；套件明细：`final-acceptance/suite-results.json`；初次失败与修复记录：`development-validation-notes.json`。真实注册表未修改，注册表导入由隔离读取器覆盖，不宣称进行过真实注册表迁移。


## 9. 2026-10-05 12:06 正式发布记录

陛下明确授权重新构建、替换旧实例，并按历史规范提交和推送。本次正式替换已完成，未触发回退；后续停机或替换仍需新的当次授权。

源码提交 `5c37618` 已推送至 `origin/feature/provider-client-takeover`，远端提交号核验一致。本次补记仅更新发布记录，不修改程序或再次停止正式实例。

| 项目 | 实际结果 |
|---|---|
| 发布时间 | 2026-10-05 12:06:22 +08:00 |
| 正式进程 | 旧 PID 19308 正常退出，新 PID 32372 |
| 正式端口 | 18080、18081 均为 ok，监听归属均为新进程 |
| 重新构建测试 | Release 1311 项通过、0 失败、0 跳过；Core 1275、App 36 |
| 隔离验收 | 13 个套件全部 exit 0，含真实配置副本与合成配置各 9 项 |
| 程序大小 | 60,866,528 字节 |
| 程序 SHA-256 | `9CD2B51E0F0CAADDA1C7D71E6B99FB4A834FF4E743DB13DA4EA6FD96A791652F` |
| 备份 | `build/instance-backups/upgrade-20261005-120614/` |
| 发布证据 | `.tmp/release-sqlite-20261005/deployment-20261005-120614/` |

1. 构建与隔离验收：重新生成 `.tmp/release-sqlite-20261005/package/`，程序与上一轮已验收包逐字节一致，构建输入校验一致。默认发布门禁运行 Core 与 App 两个测试工程，全部通过；十三个隔离套件重新全跑通过。旧正式实例在隔离验收期间保持运行。
2. 替换与迁库：先备份旧程序、完整 User、客户端与日志，再正常退出旧实例，确认两个客户端恢复为当前 Key 直连。随后只替换 `RetryProxy.exe`、`runtime-check.cmd` 和 `User/I18n/en.json`。新版自动导入原配置，原 JSON 备份后改名；配置库四行齐全，库结构版本为 1，数据版本为 2，两库 quick_check 均为 ok。两条通道、两个供应商、四把 Key、两项准备定义及界面偏好保留。两个客户端重新自动接管，两个客户端文件、Codex auth 与三个首次接管原始备份均保持一致。旧统计和诊断文件的文件名、字节数、修改时间及内容校验值未变。
3. 正式窗口：锁定新进程及正式程序路径，实际打开供应商、统计、请求诊断和软件设置。统计页显示恢复后的当天数据与趋势，请求诊断页显示新版启动后的请求；“日志与数据”整卡可见，目录为 `D:/RetryProxy/dist/logs`。四张截图已逐张查看，最终返回供应商页，保留原客户端选择。窗口检查后的配置、客户端与旧文件复核通过，正确本地健康接口 `/_retry/health` 与监听归属再次通过。

当天统计按 S4 从运行日志恢复，不导入旧 JSONL；日志节流或轮转缺失的重试次数不推算补齐，不能表述为旧统计无损迁移。下表来自停机前和首次启动后的快照，快照之间有正常新请求。

| 客户端 | 停机前请求数 | 首次启动后请求数 | 停机前重试数 | 首次启动后重试数 |
|---|---:|---:|---:|---:|
| Codex | 3181 | 3183 | 1268 | 471 |
| Claude Code | 391 | 391 | 3808 | 149 |

两通道当天恢复标记均已入库，首次检查两库中仅有上线当天数据。后续健康快照的请求数继续增长，正式程序持续处理请求。旧 JSONL 仍完整留在磁盘与停机备份中。

完整证据以 `.tmp/release-sqlite-20261005/` 为根：`package-validation.json`、`final-acceptance/suite-results.json` 记录构建与隔离结果；`live-window/summary.json` 与四张截图记录实际窗口；发布目录内的 `deployment.json`、`direct-after-stop.json`、`migration-baseline.json`、`final-state.json`、`post-ui-state.json`、`statistics-comparison.json` 和 `final-health.json` 记录正常退出、迁库、恢复差异与最终保护结果。一次额外手工健康探测误用了 `/health`，被转发后返回无效地址错误，改用已核实的 `/_retry/health` 后通过；原始问题记录在 `manual-health-probe-note.json`，不把地址误用归为正式程序故障。

首次迁库的回退脚本会先正常退出新版，核对配置仍匹配停机基线，保留新数据库及 WAL/SHM，再恢复旧程序可读的 JSON 和已知版本程序文件；外部修改无法确认时保留现场、中止覆盖。本次未触发该回退路径，不宣称实机回退已验证。真实注册表未修改，注册表导入仍只由隔离读取器测试覆盖。
