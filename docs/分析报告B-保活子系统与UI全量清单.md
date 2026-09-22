> 本文由分析子代理于 2026-09-21 生成，作为《PRD-CSharp-重构.md》附录 C 的逐条对照依据。

我已完整通读全部指定文件（含 ui.rs 全部 6056 行，其中 4064 行以后为单元测试）以及与保活驱动直接相关的 `proxy.rs::send_probe`、`service.rs` 保活轮询循环。以下是报告。

---

# 保活子系统规格 + 现有 UI 全量清单（供 C# WPF 重写）

## A. 保活子系统

### A1. 状态机（`D:\API-Proxy\rust\src\keepalive.rs`）

每条通道独立持有一个 `Arc<KeepAliveWatchdog>`（`ui.rs::RetryProxyApp.route_keepalives: HashMap<route_id, Arc<KeepAliveWatchdog>>`），Watchdog 内部是 `Mutex<KeepAliveState>` + `tokio::sync::Notify wake` + `question_picker` 闭包 + 可选 `CliCommand`（测试用）+ `Mutex<UiNotifier>`。

**核心常量**
- `KEEPALIVE_CONTEXT_LIMIT: u64 = 50_000`（默认会话 token 阈值）
- `PREPARATION_RETRY_MIN_DELAY = 1_500ms`、`PREPARATION_RETRY_MAX_DELAY = 2_500ms`
- `service.rs::KEEPALIVE_POLL_INTERVAL = Duration::from_secs(5)`（空闲轮询）
- idle 下限：`configure()` / `new()` 中 `idle.max(Duration::from_secs(1))`
- 配置侧：`config.rs` `DEFAULT_KEEPALIVE_IDLE_MINUTES = 3.0`、`MIN = 0.5`、`MAX = 1440.0`

**枚举**
```rust
enum KeepAliveFlavor { Claude, Codex, #[default] Unknown }   // label(): "Claude Code" / "Codex" / "自动识别"
enum PreparationResult { Ready, Failed(String), Cancelled }   // 送给 UI 弹窗
enum PreparationState { Pending, Running(u64 /*flight id*/) } // 私有
```
`KeepAliveFlavor::detect(path)`：路径去 query、去尾斜杠后，`/messages` 结尾 → Claude；`/responses` 或 `/chat/completions` 结尾 → Codex；否则 Unknown。`From<ClientType>` 直接映射。

**`KeepAliveState` 字段**（WPF 里就是每通道的 ViewModel 后端状态）
`enabled, idle: Duration, context_limit: u64, last_activity: Instant, active_requests: u64, flavor, template: Option<KeepAliveTemplate>(现已无实际用途，take_due 恒返回 None), session: Option<KeepAliveSession>, totals: KeepAliveTotals{completed,failed,interrupted}, last_success: Option<KeepAliveSuccess{model,context_tokens}>, flight: Option<KeepAliveFlight{id,cancel,result}>, next_flight: u64, preparation: Option<PreparationState>, credential: Option<CliCredential>, preparation_attempts: u64, preparation_retry_at: Option<Instant>, preparation_last_error: Option<String>, preparation_results: VecDeque<PreparationResult>, running_services: usize`

`KeepAliveSession { conversation: Arc<Conversation>, turns, context_tokens: Option<u64>, model: Option<String>, credential: Option<CliCredential> }`；`Conversation { id: Uuid v4 字符串, cancel: CancellationToken, process: AsyncMutex<Option<CliSession>> }`。`Conversation::new()` 把 `(id, cancel)` 登记到全局 `internal_sessions()`（`OnceLock<Mutex<HashMap<String, CancellationToken>>>`），`Drop` 时 cancel 并移除。

**触发/判定逻辑 `begin_due_probe_locked()`**（唯一产生一轮问答的入口，返回 `Option<KeepAliveProbe>`）：
```
preparing = preparation == Some(Pending)
拒绝条件（返回 None）：
  running_services == 0
  || active_requests != 0
  || flight.is_some()                        // 同通道同时只有一轮
  || (preparing && preparation_retry_at > now)   // 重试等待未到
  || (!preparing && (!enabled || last_activity.elapsed() < idle))  // 自动保活未到空闲阈值
否则：
  question_index = question_picker() % 250; question = JAVA_QUESTIONS[idx]
  若 session.credential != state.credential → clear_session()（配置不一致不复用）
  session = get_or_insert(新 Conversation, turns=0, credential=当前)
  turn = session.turns + 1
  next_flight += 1; flight = {id, cancel = conversation.cancel.clone(), result: None}
  若 preparing: preparation = Running(flight_id); preparation_attempts += 1; preparation_retry_at = None
```
注意：自动保活模式下 `last_activity.elapsed() >= idle` 是唯一时间条件；一键准备（Pending）不看空闲。

**驱动循环**（`service.rs` 第 347–360 行，每通道一个 tokio task）：
```rust
loop {
  select! { _ = sleep(5s) => {}, _ = keepalive.preparation_requested() => {}, _ = cancel => return }
  proxy.send_due_keepalive_probe().await;   // 内部 begin_due_probe() -> send_probe()
}
```
`preparation_requested()`：若 `preparation==Pending && active_requests==0 && running_services>0 && flight.is_none()` 且有 `preparation_retry_at`，则 `select!(wake.notified(), sleep_until(retry_at))`；否则只等 `wake.notified()`。所以 5 秒轮询决定"最多晚 5 秒发现空闲"，准备重试有专用唤醒。

**真实请求到达/结束**
- `request_started(flavor)`：`active_requests += 1; last_activity = now`；若 `flight` 存在且 `result.is_none()`（尚未确认完成）→ `clear_session()` + `flight.cancel.cancel()`；flavor 为 Unknown 时采纳传入 flavor。已确认完成（`result==Ready`）的 flight 不会被清除（README"已确认完成的回复不会被随后到达的真实请求清除"）。
- `request_finished()`：`active_requests -= 1; last_activity = now`；若归零且 `preparation==Pending` → `wake.notify_one()`。
- 调用点：`proxy.rs::RequestFinishGuard.start()`/`Drop`，仅对 `counts_as_real_request`（无内部标记且非 HEAD）的请求调用。

**会话 token 阈值清理 `KeepAliveProbe::complete_locked(model, context_tokens)`**
- 前置：`!cancel.is_cancelled()` 且 flight 匹配且 `result.is_none()` 且 session id 匹配，否则返回 None（调用方记为中断）。
- `session.turns += 1; session.model = model; session.context_tokens = context_tokens; totals.completed += 1; last_success = Some{model, context_tokens}`
- `reset_reason`：
  - `context_tokens == None` → `"CLI 未返回完整用量，已清理后台会话，下轮新建"`
  - `tokens > context_limit` → `format!("当前会话超过 {context_limit} token，已清理后台会话，下轮新建")`
  - 有 reset_reason 时 `clear_session()`（注意：超过阈值是 **>** 严格大于；等于不清理，见测试 `session_reuses_latest_context_and_rolls_over_only_above_configured_limit`）。
- `flight.result = Some(Ready)`；返回 `KeepAliveCompletion{context_tokens, context_limit, reset_reason}`。
- `set_context_limit(limit)`：`limit.max(1)`；若当前 session 的 `context_tokens > 新上限` 立即 `clear_session()`。
- context_tokens 的来源：`proxy.rs::send_probe` 中 `reply.stats.context_tokens(flavor == Claude)`，`response_stats.rs::context_tokens(claude)` = `input (+cache_read+cache_creation 若 claude) + output`；任一缺失 → None。

**失败/中断 `finish_unsuccessfully(reason, interrupted)`**：flight 匹配且未出结果时 `flight.result = Failed(reason)`；`interrupted ? totals.interrupted+=1 : totals.failed+=1`；`clear_session()`。

**`Drop for KeepAliveProbe`**（每轮结束统一收尾）：
- 取走 `state.flight`；若 `result.is_none()` → `totals.interrupted += 1; clear_session()`。
- `last_activity = now`（无论成功失败都重算空闲时间）。
- 若 `preparation == Running(this flight)`：
  - `Ready` → `preparation=None; retry_at=None; last_error=None; preparation_results.push_back(Ready)`
  - 否则 → `preparation = Pending; preparation_retry_at = now + rand[1500..=2500]ms; preparation_last_error = Failed(reason) 的 reason 或 "本轮已让行或中断，等待继续准备"; wake.notify_one()`
- 否则若 `preparation == Pending` → `wake.notify_one()`。

**其它状态入口**
- `configure(enabled, idle)`：无变化直接返回；否则 `last_activity=now`；关闭时 `clear_session()` + `cancel_preparation(Failed("保活已关闭，本次准备已取消"))` + cancel 当前 flight。
- `configure_flavor(flavor)`：Unknown 忽略；变更时清 template/session/flight/preparation/credential/retry_at/last_error/results（通道改类型全部重置）。
- `register_service(flavor) -> KeepAliveServiceGuard`：`running_services += 1`（首个时 `last_activity=now`），Guard `Drop` 时 `-= 1`，归零 → `clear_session()` + `cancel_preparation(Failed("本通道已停止"))` + cancel flight。
- `request_preparation()` = `request_preparation_inner(None)`（沿用当前 credential）；`request_preparation_with(Option<CliCredential>)` = 切换配置。逻辑：`running_services==0 → Err("请先启用本通道")`；已有 preparation → `Ok(false)`；`same_credential = state.credential == credential`；`reusable_flight` = 未取消且同配置的现有 flight；配置不同时 `clear_session()` 并 cancel 未完成 flight；`state.credential = credential`；`preparation = reusable ? Running(id) : Pending`；`preparation_attempts = reusable ? 1 : 0`；清 retry_at/last_error；`wake.notify_one()`。
- `cancel_preparation()`（用户"终止准备"）：内部 `KeepAliveState::cancel_preparation(Cancelled)`：无 preparation → false；若 Running 且 flight 已 Ready → 推 Ready 并返回 false（README"取消后仍保留已确认结果"）；否则 cancel flight，推 `Cancelled`。然后 `clear_session(); last_activity=now`。**credential 保留**（终止只结束本次，通道仍记住已选 Key）。
- `take_preparation_result()`：UI 每帧 `pop_front()`。
- `snapshot() -> KeepAliveSnapshot`（UI 读取的结构，见 B9）。

**单轮执行 `send_probe`**（`proxy.rs` 391–483）：
- 超时 = `min(timeout_seconds, total_timeout_seconds)`（通道单次超时与总等待较小者）。
- 日志开头：`"供应商保活 [会话 {前8位}] 第 {turn} 轮，随机题号 {idx+1}/250，{flavor} CLI，{沿用本机客户端配置|使用本次输入的 Key 经本通道转发}，问题：{question}"`
- `select!(biased; 通道 cancel → Interrupted("通道或应用已停止"); probe.cancel → Interrupted("已让行真实请求或保活设置发生变化，本轮会话已清理"); timeout(probe.execute()) → 超时 Failed("CLI 本轮执行超过 {n} 秒，已终止并清理会话"))`
- 成功：`probe.complete(model, context_tokens)`；日志 `"{prefix} 完整回复{log_fields}，当前会话 {tokens|未获取}/{limit} token，首字 x.xx 秒 / 耗时 y.yy 秒，回答：{前180字符去控制字符}{，reset_reason}，{空闲 N 秒后进行下一轮|自动保活已关闭}"`；complete 返回 None → `probe.interrupt("完整回复确认前本轮已取消，未计为成功")`。
- 失败：`probe.fail(reason)`，WARN `"{prefix}，响应未完成：{reason}，耗时 {e:.2} 秒，{after_failure}"`；after_failure 在准备中为 `"准备未完成，随机等待 1.500～2.500 秒后继续重试；可点击“终止准备”取消"`。
- 中断：`probe.interrupt(reason)`，INFO `"{prefix}，本轮已中断：{reason}，耗时 ..."`。
- `KeepAliveProbe::execute()`：`conversation.process` 为空时 `CliSession::start(flavor, session_id, command_override, credential)`；然后 `ask(question, session_id)`；首字时间加上启动耗时。**进程与 Conversation 绑定**：会话清理（`clear_session` → Conversation Drop）即杀进程；同会话多轮复用同一进程。

### A2. CLI 启动与协议（`keepalive_cli.rs`）

**可执行文件定位 `CliCommand::discover(flavor)`**
- 环境变量优先：Claude → `RETRY_PROXY_CLAUDE_CLI`，其它 → `RETRY_PROXY_CODEX_CLI`；必须是绝对路径且是文件，否则 `Err("{variable} 必须指向已安装 CLI 的绝对路径")`。
- 否则遍历 `PATH`，Windows 依次尝试 `claude|codex` + `.exe/.cmd/.bat/.ps1`。
- `.ps1` 用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <path>` 包装。
- 找不到：`"未找到本机 {Codex|Claude Code} CLI，请先安装并完成配置"`。

**通用启动**：`tempfile` 临时目录前缀 `retry-proxy-keepalive-` 作 cwd；stdin/stdout/stderr 全 piped；`kill_on_drop(true)`；Windows `creation_flags(0x0800_0000)`(CREATE_NO_WINDOW)；stderr 用任务丢弃；spawn 失败 → `"无法启动 {label} CLI：{error.kind()}"`。

**Codex：`codex app-server --listen stdio://`，JSON-RPC（每行一个 JSON）**
- 追加 `-c` 项（每项一个 `-c`）：
  `features.shell_tool=false`、`features.unified_exec=false`、`features.hooks=false`、`features.plugins=false`、`features.apps=false`、`features.multi_agent=false`、`features.view_image=false`、`web_search="disabled"`、`project_doc_max_bytes=0`
- 指定 Key 时再追加（`codex_credential_overrides`）：
  ```
  model_provider="retry_proxy_prepare"
  model_providers.retry_proxy_prepare.name="Retry Proxy 指定 Key 准备"
  model_providers.retry_proxy_prepare.base_url="{base_url}/v1"     (JSON 字符串转义)
  model_providers.retry_proxy_prepare.wire_api="responses"
  model_providers.retry_proxy_prepare.env_key="RETRY_PROXY_PREPARE_KEY"
  ```
  并设进程环境变量 `RETRY_PROXY_PREPARE_KEY=<api_key>`（密钥不进命令行）。
- 握手 `initialize_codex()`：
  1. `rpc("initialize", {"clientInfo":{"name":"codex_cli_rs","version":"2.0.0"},"capabilities":{"experimentalApi":true}})`
  2. 写通知 `{"method":"initialized"}`（无 params）
  3. `rpc("config/read", {"includeLayers":false})` → `codex_background_overrides()`：遍历 `/config/mcp_servers` 的键，生成 `{"mcp_servers":{"<name>":{"enabled":false},...}}`；无工具则 `{}`。
  4. `rpc("thread/start", {"cwd":<tempdir>,"ephemeral":true,"approvalPolicy":"never","sandbox":"read-only","developerInstructions":INTERVIEW_INSTRUCTIONS,"config":overrides})`；取 `/thread/id`（空 → `"Codex 未返回后台会话 ID"`）和 `model`。
- 提问 `ask_codex`：`{"id":n,"method":"turn/start","params":{"threadId":..,"input":[{"type":"text","text":question}],"responsesapiClientMetadata":{"retry_proxy_keepalive":<session_id>}}}`。
- 事件解析 `CodexTurn::observe`：
  - `item/agentMessage/delta` 非空 delta → 首字时间
  - `item/completed` → item type `agentMessage` 且 phase != `commentary` 的 `text` 作为答案
  - `thread/tokenUsage/updated` → `params.tokenUsage.last` 映射为 `{input_tokens: inputTokens, output_tokens: outputTokens, input_tokens_details.cached_tokens: cachedInputTokens, output_tokens_details.reasoning_tokens: reasoningOutputTokens}`
  - `error` → 记 `last_error = safe_cli_error(event)`
  - `turn/completed`：`/turn/status != "completed"` → `Err("接收回答 turn/completed 失败：{reason}")`（reason 取 `/turn/error` 或之前的 last_error）；否则再扫 `/turn/items`，返回完成。
  - 其它 threadId 的事件跳过；收到带 `id`+`method` 的服务端请求（工具审批）→ `reject_tool_request` 回 `{"id":..,"error":{"code":-32601,"message":"Background keepalive does not execute tools"}}`。
- `rpc()` 匹配 `id == request_id && method 缺失` 的行；`error` 非 null → `"客户端调用 {method} 失败：{safe_cli_error}"`。
- INTERVIEW_INSTRUCTIONS = `"请用中文回答当前这道初级 Java 面试题，解释准确，控制在二到五句话，不要反问，不调用工具，不读取或修改任何文件。"`

**Claude Code：`claude --print --input-format stream-json --output-format stream-json --verbose --include-partial-messages --no-session-persistence --tools "" --strict-mcp-config --mcp-config {"mcpServers":{}} --disable-slash-commands --settings <json> --append-system-prompt <INTERVIEW_INSTRUCTIONS>`**
- `--settings` 默认 `{"disableAllHooks":true}`；指定 Key 时 `claude_credential_settings`：
  ```json
  {"disableAllHooks":true,"env":{"ANTHROPIC_AUTH_TOKEN":"<key>","ANTHROPIC_API_KEY":"","ANTHROPIC_BASE_URL":"<base_url>"}}
  ```
  同时进程环境 `ANTHROPIC_AUTH_TOKEN=<key>`、`env_remove("ANTHROPIC_API_KEY")`、`ANTHROPIC_BASE_URL=<base_url>`（双重覆盖）。
- 环境变量 `ANTHROPIC_CUSTOM_HEADERS`：取原值（先看 command.environment，再看进程 env），按行过滤掉已有的 `x-retry-keepalive:` 行，末尾追加 `x-retry-keepalive: <session_id>`；并 `env_remove("CLAUDECODE")`。
- 提问 `ask_claude`：`{"type":"user","session_id":<thread_id, 初次为空串>,"parent_tool_use_id":null,"message":{"role":"user","content":question}}`。
- 事件解析 `ClaudeTurn::observe`：每条含 `session_id` 的事件更新 `thread_id`；`type=="control_request"` → 回 `{"type":"control_response","response":{"subtype":"error","request_id":<...>,"error":"Background keepalive does not execute tools"}}`；`system` → model；`stream_event`：`message_start` → `message()`（model/usage/stop_reason/content text 拼接）；`message_delta` → stop_reason + usage 合并（`input_tokens` 为 0 时不覆盖已有值）；`content_block_delta/start` 有文本 → 首字；`assistant` → `message()`；`result`：`subtype != "success" || is_error==true` → `Err(safe_cli_error)`；`stop_reason` 不是 `end_turn|stop_sequence` → `Err("Claude 未完整结束本轮文本回复")`；取 `result` 字符串为答案，usage 缺失时取 `event.usage`。
- 读取保护：`MAX_EVENT_BYTES = 2 MiB`，超限 `"CLI 单条输出超过 2 MiB 保护值"`；EOF → `"CLI 在完整回复前退出"`；非对象行忽略。
- `CliReply::new`：答案空 → `"CLI 未返回完整的文本答案"`；将答案+usage 组装成伪响应（Claude: `/v1/messages` 格式 `{"type":"message","model","stop_reason":"end_turn","content":[{"type":"text","text"}],"usage"}`；Codex: `/v1/responses` 格式 `{"model","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text"}]}],"usage"}`）喂给 `ResponseStats` 复用统一的 usage 解析与日志字段；`answer()` 为 None → `"CLI 答案为空或超过 2 MiB 保护值"`（实际 answer 捕获上限在 response_stats 中为 512 KiB）。

**`CliCredential::new(api_key, base_url)`** 校验：trim 后空 → `"请输入用于准备的 API Key"`；含空白/控制字符 → `"API Key 不能包含空白或控制字符"`；base_url trim 且去尾 `/`，空 → `"准备入口地址不能为空"`。`Debug` 输出 `api_key: "<redacted>"`。

### A3. 后台请求识别标记（`keepalive.rs` + `proxy.rs`）

- 请求头名：`x-retry-keepalive`（`proxy.rs::KEEPALIVE_MARKER_HEADER`），值 = 会话 id（Conversation.id）。备用：请求头 `x-codex-turn-metadata` 的 JSON 中 `retry_proxy_keepalive` 字段。`internal_request_cancel(headers)` 返回登记的 CancellationToken。
- 正文标记：`internal_body_request_cancel(body)` —— 正文为空或无登记会话时直接 None；反序列化 `{ "client_metadata": { "x-codex-turn-metadata": "<JSON 字符串>" } }`，再解析内层字符串取 `retry_proxy_keepalive`，必须是字符串且已登记。测试 `body_markers_require_valid_registered_turn_metadata` 列出所有拒绝形态（嵌套对象而非字符串、未登记 id、数字、null 等）。
- 登记：`Conversation::new()` 插入 `internal_sessions()`；清除：`Conversation::drop`。
- 处理（`proxy.rs::handle_request`）：有内部标记的请求 `proxy.metrics = 新空 ProxyMetrics`（不计统计）、`proxy.cancel = 会话 token`（会话取消即中断请求）、`request_id = "保活-{uuid}"`、不调用 `request_started/finished`。HEAD 请求也不算真实请求。无头标记时正文读完后再判断（受总等待与客户端断开约束）。
- 转发前清除：`copy_request_headers` 丢弃 `x-retry-keepalive`、`x-retry-preparation-id`；`x-codex-turn-metadata` 头中删除 `retry_proxy_keepalive`，其余字段保留（空则整个头删掉）。`strip_internal_request_metadata(body)` 对正文同样处理，`client_metadata` 空则删除整个键。

### A4. 进程管理

- `ProcessJob::attach(child)`：`CreateJobObjectW` → `SetInformationJobObject(JobObjectExtendedLimitInformation, LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)` → `AssignProcessToJobObject`。失败文案：`"无法创建 CLI 进程保护组"`、`"CLI 进程句柄不可用"`、`"无法保护 CLI 子进程，已取消启动"`。
- `CliSession::drop`：`stderr_task.abort()` → `TerminateJobObject(job, 1)` → `WaitForSingleObject(child, 5000)` → `child.start_kill()`。Job 句柄本身 `OwnedHandle` 关闭时也会 kill-on-close。
- 单轮超时：`min(timeout_seconds, total_timeout_seconds)` 秒，见 A1。
- 应用退出：`ui.rs::shutdown()` 对每个 service `stop(15s)`，通道停止 → Guard Drop → clear_session → 进程回收。

### A5. 错误分类 `safe_cli_error(value)`（`keepalive_cli.rs` 741–820）

- details 取 `/params/turn/error` → `/params/error` → `error` → 整个值。
- 诊断编号：`DefaultHasher` 对 `details.to_string()` 求 hash，`format!("{:016x}")`，16 位十六进制。
- 文本 = details 对象中 `message, code, type, subtype, result, errors, codexErrorInfo, data` 字段值拼接并 `to_ascii_lowercase()`。
- 协议错误码映射（有 code 时优先）：
  | code | 文案 |
  |---|---|
  | -32700 | 客户端无法解析请求 |
  | -32600 | 客户端拒绝请求 |
  | -32601 | 本机客户端不支持此调用，请检查版本兼容性 |
  | -32602 | 客户端调用参数不兼容 |
  | -32603 | 本机客户端内部错误 |
  输出格式：`"{reason}（错误码 {code}）；{safe_protocol_detail}；诊断编号 {id}"`
- `safe_protocol_detail(text)`：
  - `invalid transport` + `mcp_servers` → `"后台工具连接配置无效"`
  - `missing field`→`缺少字段`、`unknown field`→`不支持字段`、`invalid type`→`字段类型不兼容`、`unknown variant`→`字段取值不兼容`；紧随其后的字段名须以 `` `x` ``/`\"x\"`/`'x'` 形式且在白名单：`experimentalRawEvents, persistExtendedHistory, sandboxPolicy, sandbox, permissions, approvalPolicy, ephemeral, cwd, modelProvider, developerInstructions, mcp_servers, config, threadId, clientInfo, capabilities, input`；命中 → `"{reason}：{字段、字段}"`，否则 `"{reason}（字段名未列入安全白名单）"`。
  - `not initialized|initialize must` → `客户端初始化握手未完成`；`experimentalapi|experimental api` → `错误涉及客户端实验协议能力`；`failed to load config|failed to load bootstrap configuration|invalid config|error loading config|failed to parse config` → `本机客户端配置加载或覆盖失败`；`sandbox|permissions|approval policy|approval_policy` → `错误涉及本机客户端权限设置，未自动放宽权限`；`ephemeral` → `错误涉及本机客户端临时会话参数`；`mcp` → `错误涉及本机客户端工具服务配置`；兜底 `拒绝原因未识别，原始错误输出不写入日志`。
- 非协议码的关键字分类（顺序匹配）：`401|invalid_api_key|authentication_error` → `CLI 认证失败，请检查本机客户端登录或密钥配置`；`403|permission_denied` → `CLI 请求被供应商拒绝（权限不足）`；`429|rate_limit` → `CLI 请求触发供应商限流`；`context_length|context window` → `CLI 会话达到供应商上下文限制，已清理会话`；`model_not_found|unsupported model` → `本机 CLI 配置的模型不可用`；`timeout|timed out` → `CLI 请求超时`；`connection|connect error|network` → `CLI 无法连接供应商`。格式 `"{reason}{（错误码 n）}；诊断编号 {id}"`。
- 兜底：`"CLI 未完成本轮回复{code}；诊断编号 {id}；原始错误输出不写入日志，以保护认证信息"`。

### A6. 一键准备重试循环与通知

- 重试延迟：`preparation_retry_delay(rng) = rng.gen_range(1500..=2500) ms`（毫秒粒度），在 `Drop for KeepAliveProbe` 中设置 `preparation_retry_at`，`preparation_requested()` 用 `sleep_until` 唤醒。不受通道 max_retries 限制，直到 Ready/Cancelled/Failed(通道停止/保活关闭)。
- 终止：UI `cancel_selected_preparation()` → `watchdog.cancel_preparation()`。
- 完成通知：UI 每帧 `poll_preparation_events()`（`ui.rs` 2263）：若已有 notice 则跳过；遍历所有通道 `take_preparation_result()`，生成 notice 并写日志：
  - Ready → `"通道“{name}”：准备完成"`（INFO）
  - Failed → `"通道“{name}”：准备未完成，{reason}"`（WARN）
  - Cancelled → `"通道“{name}”：准备已终止"`（INFO）
  返回 true 时 `update()` 发送 `ViewportCommand::RequestUserAttention(Informational)`（任务栏闪烁）。notice 用 `notice_modal` 弹"提示"对话框。多个通道各自一条，不覆盖（notice 存在时不再取新结果）。
- `ui_notifier.rs::UiNotifier`：`Option<Arc<dyn Fn()>>` 回调，`notify()` 只请求重绘（不得回访业务状态）；`ui.rs::new` 中安装：窗口可见时 `request_repaint_after_for(40ms)` 合并；隐藏时 500ms 节流并 `PostMessageW(WM_PAINT)`。WPF 中对应 Dispatcher 通知即可。

### A7. `java_questions.rs`

`pub(crate) const JAVA_QUESTIONS: [&str; 250]` 纯中文字符串数组，250 道，全部以 `？` 结尾、`< 256` 字节、无重复（有单元测试断言）。抽取：`rand::thread_rng().gen_range(0..250)`，等概率允许重复，每轮重新抽。可直接原样搬到 C# `static readonly string[]`。

### A8. 统计字段位置

| 字段 | 维护处 |
|---|---|
| 成功/失败/中断轮次 `totals.completed/failed/interrupted` | `KeepAliveState.totals`，`complete_locked`、`finish_unsuccessfully`、`Probe::drop` 递增；只在 `configure_flavor` 时不清（实际上任何地方都不清零，仅进程重启归零） |
| 最近成功用量 `last_success{model, context_tokens}` | `complete_locked` |
| 本会话轮次 `session.turns` / 会话用量 `session.context_tokens` / 会话模型 | `KeepAliveSession`，会话清理后归零/None |
| 准备次数 `preparation_attempts`、重试倒计时 `preparation_retry_at`、最近错误 `preparation_last_error` | `request_preparation_inner`、`begin_due_probe_locked`、`Probe::drop` |
| 真实请求计数 `active_requests` | `request_started/finished` |
| 通道请求统计（今日请求/成功/重试/失败/处理中/缓存） | `metrics.rs::ProxyMetrics`，保活请求用独立空 metrics 不计入 |

---

## B. UI 全量清单（`ui.rs` / `ui/cache.rs`）

### B0. 窗口与全局

- 标题 `"LLM Retry Proxy"`；初始内尺寸 `DEFAULT_INNER_SIZE = [1000, 700]`，最小 `MIN_INNER_SIZE = [780, 540]`（`window_recovery.rs`）；`vsync: false`；图标 `icon::rgba(64)`（程序化绘制：深靛 `[16,25,57]` 圆角底板、青绿 `[49,218,190]`/`[92,239,218]` 链路（两个圆环+横线）、橙色 `[255,174,86]` 弧线+三角箭头；WPF 可直接换成静态 ico）。
- 字体：`C:\Windows\Fonts\msyh.ttc` → `msyh.ttf` → `simhei.ttf` 首个存在者，插入 Proportional/Monospace 首位。
- 主题 `ThemePreference::System`，`Palette::light()/dark()` 颜色表（RGB）：

| 名称 | 浅 | 深 |
|---|---|---|
| canvas | 244,246,251 | 16,19,26 |
| top_bar | 255,255,255 | 21,25,34 |
| surface | 255,255,255 | 24,28,38 |
| surface_soft | 246,248,252 | 31,36,48 |
| log_bg | 250,251,253 | 19,23,31 |
| border | 226,231,240 | 45,52,67 |
| border_soft | 235,239,246 | 38,44,58 |
| text | 24,32,46 | 228,233,243 |
| text_weak | 88,100,120 | 160,170,189 |
| text_faint | 140,151,170 | 116,126,145 |
| accent | 63,106,232 | 104,143,247 |
| accent_soft | 232,238,253 | 34,45,72 |
| success | 22,148,108 | 58,189,140 |
| warning | 196,132,22 | 224,168,68 |
| danger | 211,62,72 | 238,104,112 |

- 布局：顶栏 `TopBottomPanel` 高 48、内边距 (14,8)，底部 1px `border` 线；中央 `CentralPanel` 内边距 (14,12)：`home_page` = `route_detail` 卡片（自适应高） + `CARD_GAP 8` + `log_panel`（剩余全部高度）。
- 启动流程 `RetryProxyApp::new`：Logger（EXE 目录 `logs`）、`load_persistent_config`（失败回退 `builtin_config`）、选中 provider/route、托盘创建（失败 WARN `"托盘初始化失败，最小化将保留在任务栏"`）、`refresh_services()`、自动启动 `desired_running` 的通道。
- 配置保存 `save()`：失败 notice `"保存失败：{error}"`。
- `poll_events()`：通道 Error 状态首次出现 → notice `"通道“{name}”异常：{error}"`（多条以 `\n\n` 拼接）；不改 `desired_running`。

### B1. 主窗口各区域

**顶部栏 `top_bar`**
- 左：22×22 accent 圆角方块 logo + 文本 `"Retry Proxy"`(12.5 粗)。
- 右（从右到左）：按钮 `"全部停用"`（ghost，悬停 `"立即停止所有通道，并丢弃所有处理中的请求"`）、按钮 `"全部启用"`（accent 填充）、标签 `"{running} / {total} 个通道在运行"`（running>0 用 success 色否则 text_weak）、版本 `"v{CARGO_PKG_VERSION}"`(9.5 faint)。

**服务商选择行 `provider_picker`**
- 标签 `"服务商"`；ComboBox 宽 224，选中文本为服务商名或 `"未选择服务商"`；下拉项文本 `"{name} · {url} · {usage} 通道"`。
- 标签 `"{n} 个通道"`。
- 右侧紧凑按钮：`"＋ 新增服务商"`、`"删除"`、`"编辑"`（未选中时 notice `"先选中一个服务商"`）。删除进入确认框。
- 切换服务商只切换页面通道列表并保存 `selected_route_id`。

**通道选择行 `route_picker` + 状态/操作**
- 标签 `"通道"`；ComboBox 宽 168（accessibility 名 `"通道选择"`），选中文本为通道名或 `"暂无通道"`；下拉项 `"{name} · {port} · {state_label}"`；无通道时禁用。
- `"＋ 新增通道"`（primary 紧凑，无服务商时禁用；`open_route_editor(None)` 无服务商 notice `"请先新增服务商，再创建通道"`）。
- 状态胶囊 `state_pill`：圆点 + 文本，`state_label`：Stopped `"已停止"`、Starting `"启动中"`、Running `"运行中"`、Stopping `"停止中"`、Error `"异常"`；`state_color`：Running (34,168,122)、Starting/Stopping (214,152,42)、Error (219,76,86)、Stopped (146,156,173)；背景 = 色 ×0.14 alpha。
- 右侧：`"删除"`（danger ghost → 确认框）、`"编辑"`（悬停 `"最大重试 {n} 次\n单次 / 总等待 {a} / {b} 秒\n退避间隔 {c} – {d} 秒"`；运行中点击 notice `"请先停用通道"`）、`"启用通道"`（primary，Stopped/Error 时）或 `"停用通道"`（ghost，悬停 `"立即停止监听，并丢弃所有处理中的请求"`）。
- 无通道时提示：无服务商 `"请先新增服务商，再为它创建通道"`，否则 `"该服务商暂无通道，点击「＋ 新增通道」创建"`。

**保活区 `route_keepalive`**（一行 horizontal）
1. `keepalive_cell`：小标题 `"本通道保活"`(10 faint)；Checkbox（无文字，accessibility 名 `"本通道保活"`，悬停 `"仅控制当前通道的自动保活。\n{hint}"`）；分钟输入框（宽 28，仅启用时可编辑，悬停 hint）；标签 `"分钟"`。解析 `parse_idle_minutes`（正有限数），失败回落原值，再 clamp 到 0.5–1440。
2. `"会话上限"`(10 faint) + `DragValue` 范围 1..=u64::MAX、步进 1000、后缀 `" token"`，悬停 `"仅作用于当前通道；超过该用量后清理后台会话，下轮重新创建，不会清理您的聊天记录。"`
3. 按钮组（上方空标签占位对齐）：
   - 未准备中：拆分按钮 `split_button_main("一键准备")`（左圆角 7，右直角）悬停 `"立即按当前配置（{本机默认配置|指定 Key 经本通道}）用本机 {Codex|Claude Code} 发起后台问答，不弹窗。\n要换成默认配置或输入 Key，点右侧箭头。"`；`split_button_arrow("▼")`（宽 20，右圆角）悬停 `"选择准备方式：本通道沿用本机默认配置，或选定另一个供应商、输入其 Key，开一条新通道单独准备，不影响本通道。该选择同时决定之后自动保活使用的配置。"`
   - 准备中：danger ghost `"终止准备"`（悬停 `"终止当前通道的本次准备和后续重试。"`）+ `preparing_indicator`（12px、8 个点每秒转一格）。
4. 说明列：`"仅当前通道 · 后台 Java 问答 · 250 题随机抽取"`(10.5 weak) + hint 文本(10.5 faint)；有 `preparation_last_error` 时 hint 悬停 `"最近一次准备未完成：{reason}\n将持续重试，可点击“终止准备”取消。"`

**保活 hint `keepalive_hint(route)` 三行文本**：
```
{status} · {flavor.label} · {本机默认配置|指定 Key 经本通道} · {model 或 "模型沿用本机配置"}
本次运行：成功 {completed} 轮 · 失败 {failed} 轮 · 中断 {interrupted} 轮 · 最近成功用量 {"{n} token"|"未返回用量"|"尚无成功回复"}
会话 {session_id 前 8 位|"待新建"} · 本会话 {turns} 轮 · 会话用量 {context_tokens|"待回复"}/{keepalive_context_limit} token
```
status 优先级：无 watchdog → `"等待通道保活初始化"`（整段）；未运行 → `"等待本通道启用"`；preparing && active>0 → `"等待 {n} 个真实请求结束后准备"`；preparing && probing → `"正在进行第 {attempts} 次准备，失败自动重试"`；preparing && retry_after → `"第 {attempts} 次未完成，{ceil 秒} 秒后继续准备"`；preparing → `"准备已排队，即将发送"`；active>0 → `"{n} 个真实请求处理中，保活让行"`；probing → `"正在进行第 {turns+1} 轮问答"`；未启用 → `"自动保活已关闭 · 可一键准备"`；否则 `"距下轮 {format_duration_cn(idle - idle_for)}"`。
`format_duration_cn`：<60 `"{n} 秒"`；<3600 `"{m} 分"` / `"{m} 分 {s} 秒"`；否则 `"{h} 小时"` / `"{h} 小时 {m} 分"`。

**本地监听 + 统计区**
- `copy_field("本地监听", url, accent=true)`：整块可点复制；小标题三态 `"本地监听"` / 悬停 `"点击复制"`(accent) / 点击后 1.6 秒 `"已复制"`(success)；值等宽粗体。
- 右侧日期说明：`"今日 {statistics_date} · 重启保留"`，有 `historical_unfinished_requests>0` 追加 `" · 历史未完成 {n}"`；有 `statistics_warning` 改为 `"今日 {date} · 统计日志异常"`(warning 色)；悬停帮助：`"按本机日期统计，午夜自动切换。请求按编号去重，重试单独计数；跨日仍未完成的请求计入新一天。\n处理中只显示当前实际请求；历史未完成表示日志没有成功或失败的结束记录，计入总请求，单独列出。\n首次升级按现有日志恢复，已被覆盖的旧日志无法补回，缺失用量保持未获取。"`（restored_from_legacy_logs 追加 `"\n今日数据包含旧日志恢复记录。"`；有 warning 前置 `"{warning}\n"`）。
- 5 个 `stat_tile`（数字 16 粗 + 标签 10）：`"今日请求"`(text) / `"成功"`(success) / `"重试"`(accent) / `"失败"`(danger) / `"用户处理中"`(warning)。
- 缓存摘要 `cache::summary`（见 B5），点 `"明细"` → `cache_view.open(route_id)`。
- `active_request_rows`（见 B4）。
- 窗口高度 ≥ 680 时再显示策略行（surface_soft 底）三列 `kv`：`"最大重试"`=`"{n} 次"`、`"单次 / 总等待"`=`"{a} / {b} 秒"`、`"退避间隔"`=`"{c} – {d} 秒"`。`cache::compact(ctx)` = 屏幕高 < 680。

### B2. 对话框（均为 `egui::Modal`，圆角 14、内边距 20、阴影）

**提示 `notice_modal`**：标题 `"提示"`，正文 notice，按钮 `"确定"`。最大宽 380。

**确认 `confirm_modal`**（最大宽 400）：
- DeleteRoute：标题 `"删除通道"`，正文 `"确认删除通道“{name}”？该通道的监听端口与重试设置会一并移除。"`
- DeleteProvider：标题 `"删除服务商"`，正文 `"确认删除服务商“{name}”？仍被通道引用时无法删除。"`
- 按钮 `"确认删除"`（danger 填充）、`"取消"`。
- `delete_route` 运行中 notice `"请先停用通道"`；`delete_provider` 被引用 notice `"该服务商仍被通道使用"`。

**新增/编辑服务商 `provider_editor`**（宽 420）：标题 `"新增服务商"`/`"编辑服务商"`；副标题 `"上游基础地址只需填到域名，路径与查询串会原样转发"`；字段 `"服务商名称"`（hint `"例如 anyrouter.top"`）、`"上游基础地址"`（hint `"https://example.com"`）；按钮 `"保存"`、`"取消"`。`commit_provider` 校验：编辑时若有非停止通道引用 → `"请先停止引用该服务商的通道"`；`ProviderEndpoint::validate()` 错误；名称(忽略大小写)或地址重复 → `"服务商名称或地址重复"`；改名同步所有通道 `provider_name`；名称/地址变化时移除受影响通道的 watchdog（重建）。

**新增/编辑通道 `route_editor`**（宽 460）：标题 `"新增通道"`/`"编辑通道"`；副标题 `"所属服务商：{provider}"`；字段：`"通道名称"`（hint `"例如 Claude Code"`）；Grid 两列：`"客户端（必选）"` ComboBox（`"请选择"`/`Codex`/`Claude Code`，accessibility 名 `"客户端类型"`）、`"本地端口"`、`"最大重试次数"`、`"单次超时（秒）"`、`"总等待上限（秒）"`（悬停 `"包含连接、重试等待和接收响应；到时结束本次请求，不再重试。"`）、`"等待生成上限（秒）"`（悬停 `"每次收到上游成功事件流的响应头后开始计时；空消息不重置。尚未转发内容时到期可重试，总等待上限优先。"`）、`"退避最小间隔（秒）"`、`"退避最大间隔（秒）"`；按钮 `"保存"`、`"取消"`。
新增默认值：端口 `first_free_port()`（18080 起首个配置未用）、其余复制当前选中通道（无则 `ProxyRoute::default`；App 初始字符串默认 retries "6"、timeout "300"、generation "300"、total "600"、base "0.5"、max "4"）。
`route_from_editor` 校验文案：`"请选择客户端：Codex 或 Claude Code"`、`"端口必须是整数"`、`"重试次数必须是整数"`、`"超时必须是数字"`、`"总等待上限必须是数字"`、`"等待生成上限必须是数字"`、`"最小间隔必须是数字"`、`"最大间隔必须是数字"`；再经 `ProxyRoute::validate()`（`"转发通道名称不能为空"`、`"保活会话用量阈值必须大于 0"`、`"通道“{name}”：保活空闲时长必须在 0.5 到 1440 分钟之间"` 等）和 `ProxyConfig::validate(false)`。编辑保持 id、provider_name、desired_running、keepalive_* 不变。

**一键准备选项窗 `prepare_modal`**（宽 460）：
- 标题 `"一键准备"`；副标题 `"通道“{name}” · {client} · 后台随机抽一道 Java 题问答，收到完整回复即准备完成。此处的选择同时决定承接通道之后自动保活使用的配置。"`
- Radio 1 `"当前通道，沿用本机默认配置"`，缩进说明 `"直接启动本机 {client}，供应商、模型和密钥均由 CLI 当前配置决定（例如 CC Switch 切换到的供应商）；准备成功后自动保活也沿用这套配置。{with_key 时追加：本通道当前用的是指定 Key，选此项会换回默认配置并丢弃该 Key 的会话。}"`
- Radio 2 `"单独准备另一个供应商，开新通道"`，说明 `"选定或新建服务商，程序为它开一条 {client} 通道并启动，后台 {client} 只把输入的 Key 经这条新通道转发到该服务商。本通道“{name}”、本机 CLI 配置和正在使用的供应商都不受影响；Key 只驻留内存，不写入配置或日志。新通道准备成功后，它的自动保活继续用这把 Key。"`；仅此模式启用的字段：
  - `"目标服务商"` ComboBox（所有服务商名 + `"新增服务商…"`，默认选第一个不是本通道所属的服务商，没有则新建）
  - 新建时：`"服务商名称"`（hint `"例如 备用供应商"`）、`"服务商地址"`（hint `"https://api.example.com"`）
  - `"API Key"` 密码框（hint `"sk-…"`，Enter 提交）+ 切换 `"显示"`（悬停 `"仅在本窗口内显示，便于核对。"`）
  - 计划文案 `plan_text`：已选服务商 → Reuse `"将使用该服务商已有的通道“{name}”（{url}），并启动它。"` / Create `"将新建并启动通道“{name}”，监听 http://127.0.0.1:{port}。"`；新建服务商 → `"将新增该服务商，并为它新建、启动一条通道。"`
- 错误文本（danger 色）留在窗内；按钮 `"开始准备"`（Default）/`"开新通道准备"`（Separate）、`"取消"`。
- 校验文案（`submit_separate_preparation`/`separate_target_provider`）：`"请输入该供应商的 API Key"`、`"服务商“{name}”已不存在，请重新选择"`、`"请输入服务商地址"`、`"请输入服务商名称"`、`"服务商“{name}”已存在但地址不同，请换个名称或直接选它"`、`"通道“{name}”无法启动：{notice}"`、`"弹窗已关闭"`；`CliCredential::new` 错误；通道不存在 notice `"通道已不存在，无法准备"`。
- 单独准备流程：`separate_channel_plan`：同服务商、同 client_type、非本通道的已有通道 → Reuse；否则 Create，名称 `"{provider} · {client_label}"`，重名加 `" 2"`,`" 3"`…，端口 `first_bindable_free_port()`（18080 起、配置未用且能 `TcpListener::bind` 的最小值）。新通道复制 origin 的重试/超时/保活参数，`desired_running=true`。保存配置、切到该服务商与通道、日志 `"通道“{origin}”发起单独准备：目标服务商“{provider}”，经通道“{target}”（{url}）转发，不影响通道“{origin}”及其供应商"`；`pending_preparation` 登记后 `poll_pending_preparation()` 每帧：Starting 等待；Running → `request_preparation_with(Some(credential))`，日志 `"通道“{name}”一键准备已提交：后台 {client} 使用输入的 Key 经 {url} 转发到服务商“{provider}”，本机客户端配置与通道“{origin}”不受影响；本通道自动保活也使用这把 Key"`；Error → WARN `"通道“{name}”未能启动，单独准备取消：{reason}"`；Stopped/Stopping → notice `"通道“{name}”未运行，单独准备已取消"`；通道消失 → `"承接单独准备的通道已不存在，本次准备取消"`。
- 主按钮/默认模式 `prepare_route`：无通道 `"请先选择一条通道，再一键准备"`；未运行 `"请先启用通道“{name}”，再一键准备"`；提交成功日志：有 Key `"通道“{name}”一键准备已提交：后台 {client} 使用已指定的 Key 经本通道转发，不改动本机客户端配置；自动保活也使用这把 Key"` / 默认 `"通道“{name}”一键准备已提交，沿用本机 CLI 默认配置，正在等待完整回复；自动保活也使用默认配置"`；失败 notice `"通道“{name}”无法准备：{reason}"`。`open_prepare_dialog` 前置同样两条提示。

**缓存明细窗口**：见 B5。

### B3. 日志面板 `log_panel`

- 头部左：`"运行日志"`(11.5 粗 weak) + `"{shown} / {total}"` + 级别 chip：`"全部"`/`"信息"`/`"警告"`/`"错误"`（`LogFilter::ALL`）+ 有选中通道时 chip `"仅 {route_name}"`（`log_only_selected`）。
- 头部右：`"目录"`（explorer 打开 `logs`，失败 notice `"日志目录：{path}"`）、`"清空"`、Checkbox `"自动滚动"`（悬停 `"手动上滚后暂停跟随，连续 5 秒无操作自动恢复；关闭后保持手动浏览"`）、搜索框 hint `"搜索关键字 / 请求 ID"` 宽 168。
- 空态：无日志 `"暂无日志，启用通道后会在这里显示运行状态"`；筛选无结果 `"没有匹配当前筛选条件的日志"`。
- 缓冲：`LOG_RETAIN_LINES = 2000`，超过时一次丢最旧 `LOG_TRIM_LINES = 500`；虚拟化只布局可见行（WPF 用 VirtualizingStackPanel 即可）。
- 筛选 `log_matches`：级别 `accepts` + 通道名 `line.contains("[{name}]")` + 小写关键字子串。
- 行解析 `split_log_line`：格式 `YYYY-MM-DD HH:MM:SS LEVEL [tag][tag] body`；时间戳按第 4/7/13/16 字节为 `-`,`-`,`:`,`:` 判定；级别词 `ERROR`/`WARNING`/`INFO`（缺失视为 INFO）；连续 `[...]` 为标签。
- 着色 `log_line_job`（等宽字体 11，行高 size+3）：时间只显示 `HH:MM:SS`（text_faint）；级别徽章 `"INFO"`/`"WARN"`/`"ERR "` 用 `level_color`（Info→success 绿、Warning→warning 橙、Error→danger 红）；标签 `[..]` accent；正文按级别：Error→danger、Warning→warning、Info→text_weak；正文首个 `HTTP <3位数字>`（`find_status_code`）单独着色：2xx/3xx → 该行级别色（成功行绿、告警/错误行橙/红）、4xx → warning、5xx → danger。
- 自动滚动规则（`LOG_SCROLL_RESUME_DELAY = 5s`）：`follow = autoscroll && resume_at.is_none() && !manual_scroll`；手动滚轮/在区域内拖动且未在底部 → `resume_at = now + 5s`；期间任何输入活动（指针按下或有事件）重置为 `now+5s`；到期或回到底部（`offset+viewport >= content-1`）→ 清除；关闭自动滚动 → 清除并保持手动；重新勾选 → 立即回到底部。

### B4. 请求明细区 `active_request_rows`

数据 `MetricsSnapshot.requests: Vec<ActiveRequest{request_id, method, path, phase, attempt}>`。空则不显示。行高 18，区域最大高 `min(18 + max(0, 窗口高-540), 66)`，可滚动。每行：`request_id`（等宽 11）、`"第 {attempt} 次"`(10.5 weak)、阶段 `RequestPhase::label()`：`"等待回复"`/`"等待生成"`(text_weak)、`"等待重试"`(warning)、`"接收内容"`(accent)、`"{method} {path}"`(faint，截断，悬停全文)。无倒计时。

### B5. 缓存面板（`ui/cache.rs`）

**摘要 `summary`**（三列，surface_soft 底；高度 <680 用 `compact_summary`）：
- 列 1：`metric("今日缓存命中", rate_text)`（悬停 `RATE_HELP`）；`ProgressBar(rate/100)` 高 5 success 色不动画（悬停 `"已复用 {cached} / 总输入 {input} token\n{RATE_HELP}"`）；`"已记录写入 {creation_total_text} token"`（悬停 `creation_help`）。
- 列 2：`metric("最近一次成功", request_rate_text(latest) | "等待请求")`（悬停 `request_hint` | `"等待本通道的成功请求"`）；下行 `"读取 {cached} / 总输入 {input}"` | `"用量未计入累计命中率"` | `"收到回复后更新"`。
- 列 3：`metric("完全未命中", "{n} 次")`（>0 用零命中色；悬停 `"{n} 次请求的缓存读取量为 0，共 {tokens} token 输入。\n{USAGE_HELP}"`）；右对齐 `"明细"` 按钮（悬停 `"在独立窗口中查看，可拖到主窗口外"`）+ `"有效 {measured} 次 · 未计入 {unmeasured} 次"`。
- compact 版：列 3 改为 `"完全未命中 {n} 次"` + `"明细"`。
- 文案常量：`RATE_HELP = "命中率 = 从缓存读取的输入量 ÷ 总输入量，按输入量计算，不按请求次数平均。\n总输入按回复格式统一计算；缓存写入单独展示，只有缓存读取计为命中。\n仅统计本通道当天成功完成且用量有效的请求；按本机日期切换，重启后从日志恢复当天数据。"`；`USAGE_HELP = "token = 模型计算输入用量的单位。\n未获取、无输入和用量异常的请求不计入命中率，也不算完全未命中。"`
- `rate_text`：`None`→`"暂无数据"`；`0<v<0.01`→`"<0.01%"`；`99.99<v<100`→`">99.99%"`；否则 `"{v:.2}%"`（两位小数）。`rate_color`：>0 → 浅色 (16,117,85)/深色 success；=0 → 浅 (148,95,14)/深 warning；None → text_weak。`count_text`：≤9999 原样；≤99,999,999 `"{:.1}万"`；≤999,999,999,999 `"{:.2}亿"`；否则 `"{:.2}万亿"`。`optional_count`：None→`"未获取"`。`request_rate_text`：有命中率则百分比；`usage_is_invalid`→`"用量异常"`；`(Some(0),Some(0))`→`"无输入"`；否则`"未获取"`。`creation_total_text`：无测得→`"未获取"`；部分→`"{n}（部分）"`。`creation_help = "缓存写入 = 本次回复报告的新建缓存用量，不计为缓存命中。\n{m} / {n} 个有效请求取得写入用量，合计 {t} token；未获取的写入用量不填成 0。"`

**明细窗口 `CacheView::show`**：独立原生视口 id `"channel-cache-details"`，标题 `"缓存明细 · {route_name}"`，内尺寸 700×520，最小 600×360，可缩放，任务栏显示。打开/居中逻辑：`open()` 置 `focus_requested=true`；每帧若 focus_requested：最小化/最大化则先还原；否则用主窗口 `outer_rect` 中心（物理像素，`× pixels_per_point`）减自身 outer 尺寸一半计算位置，差距 >1px 则 `OuterPosition`，稳定后再 `Visible(true)+Focus`（处理跨显示器 DPI 变化）。窗口复用：再次点"明细"只重新居中，保留用户改过的尺寸。关闭不停代理。
内容：
- 行 1：`"今日 {MM-dd} 命中"` + 大号命中率(22 粗) + `"已复用 {cached} / 总输入 {input} token"` + `"· 已记录写入 {n} token"`。
- 行 2：`"今日最近 {n} 次成功请求 · 命中 {recent_rate}"` + `"从旧到新 →"`。
- 走势图 `trend`：高 56，`CACHE_HISTORY_LIMIT = 20` 个槽，每槽底色 surface_soft 圆角 3；有命中率按比例填充（最小 3px）用 rate_color；未计入画 × 叉；悬停每槽显示 `request_hint`；空时居中 `"暂无成功请求"`。
- 图例：`"有命中"`(绿)、`"完全未命中"`(橙)、`"未计入"`(weak) + `"· 今日有效 {n} 次 / 未计入 {m} 次"`。
- 筛选 chip：`"全部"`/`"完全未命中"`/`"未计入"` + `"最新在前"`。
- 表 `request_rows` 五列：`"完成时间 / 请求"`（`HH:MM:SS` + 可点击 request_id accent 等宽，悬停 `"点击复制请求编号，可在日志文件中查找"`，点击复制）、`"模型"`、`"缓存命中率"`(13 粗 rate_color)、`"读取 / 总输入 token"`、`"缓存写入 token"`；行高 36，最大高 `min(屏高×0.34, 252)`；空态 `"收到成功回复后，这里会显示缓存记录"` / `"最近记录中没有符合筛选的请求"`。
- `request_hint = "{time} · {id} · {model}\n命中 {rate} · 读取 {cached} / 总输入 {input} token\n缓存写入 {creation} token\n缓存标识：{cache_key_status}\n{USAGE_HELP}"`
- 折叠 `"统计范围"`：RATE_HELP、USAGE_HELP、`"Claude 总输入 = 未缓存输入 + 缓存读取 + 缓存写入；任一项未获取时不计算命中率。其他兼容接口使用回复中的总输入量。"`，有标识计数时追加 `"GPT 缓存标识 = 供服务商识别同一会话的信息。\n客户端自带 {a} 次 · 代理补全 {b} 次\n未补全：缺少会话 {c} 次 / 服务商不兼容 {d} 次；兼容重发 {e} 次。\n补全计数包含处理中和失败请求，明细仅保留最近 20 次成功请求。"`
- `CacheFilter::ZeroHit` = `usage()` 存在且 cached==0；`Unmeasured` = `usage()` 为 None。

### B6. 托盘

- `create_tray()`：菜单项 id/文本：`show`→`"显示窗口"`、`start-all`→`"全部启用"`、`stop-all`→`"全部停用"`、`exit`→`"退出"`；tooltip `"LLM Retry Proxy"`；图标 32px；左键不弹菜单（`with_menu_on_left_click(false)`）。
- `poll_tray`：菜单事件映射到 `start_all/stop_all/exit(exit_requested=true + ViewportCommand::Close)`；`TrayIconEvent::DoubleClick` → show。
- `TrayWindowState::update(minimized, tray_available, show_requested, exit_requested, frame_nr)`：exit → 无动作；show 或（无托盘且 Hidden）→ `Restoring(frame)` + Restore；Restoring 同帧忽略，下一帧转 Visible；有托盘且 `minimized==Some(true)` 且 Visible → Hidden + Hide。
- Hide：清焦点（避免光标闪烁重绘）、`Minimized(true)` + `Visible(false)`；Restore：`Minimized(false)` + `Visible(true)` + `Focus`。关闭窗口 = 退出（`on_exit → shutdown`：停所有服务 15s、保存配置、销毁托盘）。
- `hidden_repaint.rs::HiddenWindowRepaint`：Win32 `SetTimer(hwnd, 0x5250_5549, 200ms)` 回调在窗口不可见时 `PostMessageW(WM_PAINT)`，仅在 Hidden 且（缓存明细打开或有重绘请求）时启用；作用是让 egui 在隐藏时仍能刷新明细窗口/消化日志。WPF 不需要。

### B7. `window_recovery.rs`

- 常量 `CONFIRM_DELAY = 250ms`、`RETRY_INTERVAL = 5s`；UI 侧 `WINDOW_RECOVERY_REPAINT_INTERVAL = 300ms`（pending 时轮询）。
- `poll()` 每帧（Hidden/退出时 `suspend()`）：`inspect_window` 在不可见/最小化/最大化/正在移动缩放(`GUI_INMOVESIZE`) 时返回 None → suspend；取 DPI、`GetWindowRect`、用 `AdjustWindowRectExForDpi` 把 MIN/DEFAULT 内尺寸换算成外尺寸；`caption_height = 32dp`、`grip_width = 96dp`、`grip_height = 16dp`（× dpi/96 向上取整）。
- `is_usable`：宽高 ≥ 最小外尺寸（或不大于显示器工作区）且标题栏条带（顶部 32dp）与某显示器工作区交集 ≥ 96×16dp。
- 可用 → 记 `saved` 并 suspend。不可用 → `RecoveryGate::ready`：首次记 `invalid_since`，持续 ≥250ms 且距上次尝试 ≥5s 才动作。
- `recovery_rect`：saved 可用 → 用 saved；否则取与 `saved.unwrap_or(current)` 重叠面积最大（并列取主显示器）的显示器工作区，尺寸用 source 尺寸（若不小于最小值）否则 DEFAULT，clamp 到工作区，居中。
- `SetWindowPos(SWP_NOACTIVATE|SWP_NOZORDER)` 不抢焦点；成功 WARN `"窗口位置或尺寸异常，已自动恢复：{current:?} -> {restored:?}"`；首次失败 WARN `"窗口位置或尺寸恢复未完成，将间隔 5 秒重试：{current:?} -> {target:?}"`（同一次持续异常只记一次）。
- 位置记忆仅在本次运行内存（`saved`），不持久化。

### B8. 性能相关设计（WPF 不需要，仅知晓）

`vsync:false`；无定时全量重绘，改为 `UiNotifier` 事件驱动 + `next_timed_repaint()`：Restoring 50ms、窗口恢复 pending 300ms、保活 hint 有倒计时/准备中时 1s（`keepalive_hint_changes_over_time`）；`NOTIFY_REPAINT_DELAY=40ms` 合并；隐藏时 `HIDDEN_NOTIFY_INTERVAL=500ms` 节流；`preparing_indicator` 不用 Spinner；日志虚拟化 + 行高缓存；`FrameStats`（`RETRY_PROXY_FRAME_STATS` 环境变量，每 10 秒日志 `"界面帧统计：..."`）；`copy_field` 1.6s 后自唤醒。

### B9. UI ↔ 后端数据结构

- `KeepAliveSnapshot { flavor, model: Option<String>, session_id: Option<String>, turns: usize, context_tokens: Option<u64>, context_limit: u64, totals: KeepAliveTotals{completed,failed,interrupted}, last_success: Option<KeepAliveSuccess{model,context_tokens}>, active_requests: u64, probing: bool (flight 存在且未出结果), preparing: bool, with_key: bool, preparation_attempts: u64, preparation_retry_after: Option<Duration>, preparation_last_error: Option<String> }` —— `watchdog.snapshot()`；另用 `watchdog.idle()`、`idle_for()`、`enabled()`、`take_preparation_result()`。
- `MetricsSnapshot { statistics_date: String, historical_unfinished_requests, restored_from_legacy_logs, statistics_warning: Option<String>, total_requests, active_requests, successful_requests, retry_count, failed_requests, requests: Vec<ActiveRequest>, cache: CacheSnapshot, gpt_cache: CacheSnapshot }` —— `service.metrics.snapshot()`。
- `CacheSnapshot { measured_requests, unmeasured_requests, input_tokens, cached_tokens, cache_creation_tokens, cache_creation_measured_requests, zero_hit_requests, zero_hit_input_tokens, client_key_requests, added_key_requests, missing_session_requests, unsupported_key_requests, compatibility_fallbacks, recent_requests: VecDeque<CacheRequest> (≤20, 旧→新) }` + 方法 `hit_rate_percent()`, `recent_hit_rate_percent()`。
- `CacheRequest { request_id, model, completed_at_unix_ms: i64, input_tokens, cached_tokens, cache_creation_tokens: Option<u64>, input_accounting: IncludesCached|ExcludesCached, cache_key_status: String }` + `total_input_tokens()`, `usage()`, `usage_is_invalid()`, `hit_rate_percent()`。
- `ServiceState { Stopped, Starting, Running, Stopping, Error }`；`ProxyService { route_name, metrics, keepalive, request_start(config)->Result<bool>, request_stop(), stop(timeout), state(), startup_error(), is_running(), request_preparation(), request_preparation_with(Option<CliCredential>) }`。
- 配置：`ProxyConfig { providers: Vec<ProviderEndpoint{name, base_url}>, routes: Vec<ProxyRoute{ id, name, provider_name, client_type: ClientType{Codex,Claude}, listen_port: u32, max_retries: u64, timeout_seconds, generation_timeout_seconds, total_timeout_seconds, base_delay_seconds, max_delay_seconds: f64, desired_running, keepalive_enabled: bool, keepalive_idle_minutes: f64, keepalive_context_limit: u64 }>, selected_route_id, schema_version: 6, ... }`，注册表 `HKCU\Software\LLM Retry Proxy\ConfigJson`；测试用 `RETRY_PROXY_CONFIG_JSON` 环境变量注入。
- 日志：`Logger` → `Receiver<String>` 行文本（UI 每帧 `try_recv`），`logger.set_ui_notifier`。

---

## C. tests 目录验收脚本

**`verify_rust_exe.ps1`**（参数 `-VerifyTray`、`-UseCurrentDesktop`(需 VerifyTray)）：临时目录复制 EXE，PowerShell Job 模拟上游（`/test` 首次 500 再 200 返回 `{"result":"rust-ok"}`；`/v1/responses` 首次只发 `response.created`+心跳 2 秒后关闭、第二次发 delta+completed；`/v1/messages`、`/v1/chat/completions` 返回固定 usage；`/tray-stream` 阻塞直到 `finish-stream` 文件出现）。配置 `generation_timeout_seconds=0.5, max_retries=1`。检查点：
1. `/_retry/health` 可达；`/test` 返回 `rust-ok` 且 `retry_count == 1`。
2. 等待生成超时重试：返回体与第二次尝试完全一致、`x-request-id == new-generation-attempt`（不泄漏旧尝试头）、`retry_count == 2`、`failed_requests == 0`、`active_requests` 归零。
3. 缓存合并：`cache.input_tokens == 2000`、`cached_tokens == 1500`、`cache_creation_tokens == 100`、`gpt_cache.measured_requests == 1`、`statistics_date` 为今天、`statistics_warning` 为 null。
4. （VerifyTray）在私有桌面 `CreateDesktop` 启动，`window_verification.cs` 注册假 `Shell_TrayWnd` 窗口（只回应 `WM_COPYDATA 0x004A` 托盘注册），按 PID 查找主窗口 `"LLM Retry Proxy"` 与托盘类 `tray_icon_app`；对普通与最大化各测三种最小化（`SC_MINIMIZE 0xF020`、`ShowWindowAsync(6)`、`0xF022`），要求窗口不可见但 `IsIconic`，菜单命令 `WM_COMMAND 1000` 恢复且保持最大化状态；`-UseCurrentDesktop` 额外投递 `6002/WM_LBUTTONDBLCLK` 验证双击恢复。
5. 缓存明细：点 `"明细"` 后出现独立窗口 `"缓存明细 · E2E"`（非主窗口），居中误差 ≤2px；可移到主窗口外且不改主窗口、再点明细重新居中但保留改过的尺寸；可最大化/还原/最小化；主窗口移动后点明细恢复并居中；`WM_CLOSE` 关闭后代理仍工作；可重新打开。
6. 隐藏期间：流式请求在最小化后继续（`active_requests==1`）、隐藏时能关闭明细窗口、流完整包含 `before-minimize`/`after-minimize`/`response.completed`、后台请求不唤起窗口、菜单恢复后无失败/未完成请求。
7. 窗口恢复：注入 `(-32000,-32000,160,28)`、`(原位,160,28)`、`(-32000,-32000,1000,700)` 三种，均自动恢复到原矩形，日志中 `WindowRect {..} -> WindowRect {..}` 恰好 3 条，代理不中断。
8. 关闭主窗口 → 进程退出。
9. 两次重启（删除普通日志及轮转文件）后 `/_retry/health` 的 `metrics` JSON 与重启前完全一致；重启后再发一条 chat 请求，`total_requests +1`、`cache.input_tokens == 3000`、`cached_tokens == 2200`。
10. 程序目录不生成 `config.json`；清理路径必须在临时目录内且非重解析点。

**`verify_keepalive_exe.ps1`**（PowerShell 7 + UIAutomation；模式互斥 `-Automatic/-Interrupt/-RetryPreparation/-CancelPreparation`，`-NoScreenshot`）：写出假 `codex.ps1` 并设 `RETRY_PROXY_CODEX_CLI` 指向它（验证 `.ps1` 包装路径），假 CLI 实现 app-server 协议：`initialize` 回 result；`initialized` 必须无 params；`config/read` 返回两个 MCP 工具（含私密 env）且在 `bootstrap-failures` 计数>0 时回 `-32603`；`thread/start` 校验 `config` 恰好只有 `mcp_servers` 且每个工具只有 `{enabled:false}`（否则回 `-32600 failed to load bootstrap configuration: invalid transport in mcp_servers`）；`turn/start` 检查 `responsesapiClientMetadata.retry_proxy_keepalive` 存在，然后**用正文标记**（`client_metadata["x-codex-turn-metadata"]` 含 `retry_proxy_keepalive` + `thread_id` + `fixture_field`）POST 到本地代理 `/v1/responses?beta=a%20b`，再发 delta、tokenUsage(40/12)、`turn/completed`（`Interrupt/CancelPreparation` 模式下第二轮发完 delta 后无限挂起并写 `HELD`）。模拟上游校验：路径含查询串原样、UA/originator/Authorization 原样、无 `x-retry-keepalive`/`x-retry-preparation-id` 头、正文字段未被改写/未注入 `max_output_tokens`/`reasoning`、后台请求 tools 为空且正文标记已清除但 `thread_id`/`fixture_field` 保留、后台请求不含用户消息 `正常客户端验证消息`。检查点：
1. 通道独立设置：Codex 通道开关状态与 `0.5` 分钟、Claude 通道 `false`/`9` 分钟，通过 `"通道选择"` 下拉切换（项名 `"Claude Code 校验通道 · {port} · 已停止"`）反复切换互不影响。
2. 真实请求完成日志含 `输入 40 / 输出 12 token`。
3. 默认：两次点 `"一键准备"` 各出现一条 `准备完成` 并点 `"确定"`；`-Automatic`：45 秒内出现 `供应商保活.*回答：`（空闲 30 秒自动）；`-Interrupt`：第二轮 HELD 后发真实请求，日志 `本轮已中断：已让行真实请求`，之后仍完成；`-CancelPreparation`：第二轮 HELD 后点 `"终止准备"`，日志 `准备已终止`；`-RetryPreparation`：前两次 `config/read` 失败后第三次成功，CLI `START` 次数 = 3。
4. 上游事件数 = 真实请求数 + CLI 请求数（默认 1+2，Interrupt 2+3，Automatic 1+1）且 issues 为空；`health.metrics.total_requests` = 真实请求数（保活不计入）；后台请求全部经过本地代理。
5. 窗口某元素文本包含 `"成功 {c} 轮 · 失败 {f} 轮 · 中断 {i} 轮"`（默认 2/0/0，Automatic 1/0/0，Interrupt 2/0/1，Retry 2/2/0，Cancel 1/0/1）且 `最近成功用量 52 token`。
6. 日志含 `随机题号`、`首字`、`耗时`、`回答：`、`当前会话 52/50000 token`。
7. 可选截图 `window.png`。

**`window_verification.cs`**：上述托盘/窗口验收的 Win32 辅助类 `RetryProxyTrayVerification`：`StartPrivateProcess`（私有桌面 + 假 Shell_TrayWnd 消息循环）、`ClosePrivateDesktop`、`FindWindow(pid,title,class)`、`Bounds`、`IsUsable`（可见、非最小化、客户区 ≥ 780×540 按 DPI 换算或工作区上限、标题栏 32dp 落在工作区内 —— 与 `window_recovery.rs::is_usable` 对应）、`MinimizeDirectly`、`SetBounds(SWP 0x4414)`、`PostMessage`。

**`diagnose_long_responses.mjs`**（Node 24）：非验收，十分钟串行长输入诊断。从 `~/.codex/config.toml` 读 `model_provider` 与其 `base_url`（必须是回环地址）；定位 npm 安装的原生 `codex.exe`；先用本地捕获服务器 + `codex app-server`（同样的 `-c features.* =false`、`web_search="disabled"`、`project_doc_max_bytes=0`、MCP `enabled=false` 覆盖、`thread/start` 用 `model:"gpt-6-astra"`、`ephemeral/approvalPolicy never/sandbox read-only`）抓取一次真实客户端请求（头+体，返回 400 中止）；然后以该 payload 为模板，把用户消息替换成合成长文本（`BEGIN-MARKER: cedar-417`/`MIDDLE-MARKER: amber-629`/`END-MARKER: violet-853`，初始 10,000 行，首次拿到 `usage.input_tokens` 后按目标 120,000 token 校准一次到 5,000–15,000 行），在 600 秒窗口内串行 POST 到 `{base_url}/responses`，不设超时不取消最后一条。每条记录 `request_start/response_headers/terminal_event/request_result`（状态、首字/首文本/终止事件时间、事件类型计数、usage、脱敏错误 `code/type/param/message/upstream_status/incomplete_reason`、请求 id 头），观察上限 帧 8 MiB / 响应 16 MiB（超限只停解析继续读到自然结束），结果 outcome 分类（`completed/completed_without_text/upstream_error/upstream_incomplete/done_without_response_completed/stream_ended_without_completion/http_error/transport_error/diagnostic_*`），写入 `artifacts/long-requests-<uuid>.jsonl`（mode 0600），末尾 `summary`。密钥通过 `safeText` 脱敏。

---

关键文件路径：
- `D:\API-Proxy\rust\src\keepalive.rs`（状态机）
- `D:\API-Proxy\rust\src\keepalive_cli.rs`（CLI 启动/协议/错误分类/Job Object）
- `D:\API-Proxy\rust\src\java_questions.rs`（250 题）
- `D:\API-Proxy\rust\src\proxy.rs` 第 378–483 行 `send_probe`、485–568 行标记识别、1440–1502 行标记清除
- `D:\API-Proxy\rust\src\service.rs` 第 22 行、347–360 行轮询循环
- `D:\API-Proxy\rust\src\ui.rs`、`D:\API-Proxy\rust\src\ui\cache.rs`、`D:\API-Proxy\rust\src\ui\hidden_repaint.rs`、`D:\API-Proxy\rust\src\ui_notifier.rs`、`D:\API-Proxy\rust\src\window_recovery.rs`、`D:\API-Proxy\rust\src\icon.rs`
- `D:\API-Proxy\rust\src\metrics.rs` 第 16–160 行（快照结构）、`D:\API-Proxy\rust\src\config.rs` 第 11–26、144–259 行
- `D:\API-Proxy\tests\verify_rust_exe.ps1`、`verify_keepalive_exe.ps1`、`window_verification.cs`、`diagnose_long_responses.mjs`
