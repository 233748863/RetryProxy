> 本文由分析子代理于 2026-09-21 生成，作为《PRD-CSharp-重构.md》附录 C 的逐条对照依据。

我已完整阅读所有目标文件。以下是面向 C# 移植的《后端功能与数据规格》报告。

---

# LLM Retry Proxy 后端功能与数据规格（Rust → C# 移植指南）

源码位置：`D:\API-Proxy\rust\src\`，crate 名 `retry_proxy`（lib）+ 二进制 `RetryProxy`（`main.rs` 仅调用 `retry_proxy::ui::run_app()`，Windows 下 `windows_subsystem = "windows"`）。
`lib.rs` 导出模块：`config`、`keepalive`、`keepalive_cli`、`logging`、`metrics`、`proxy`、`service`、`system_proxy`、`ui`、`ui_notifier`；私有模块 `icon`、`java_questions`、`prompt_cache`、`response_stats`、`window_recovery`。

---

## 1. 配置模型（`config.rs`）

### 1.1 常量

| 常量 | 值 |
|---|---|
| `DEFAULT_LISTEN_PORT` | 18080 |
| `DEFAULT_MAX_RETRIES` | 6 |
| `DEFAULT_TIMEOUT_SECONDS` | 300.0 |
| `DEFAULT_GENERATION_TIMEOUT_SECONDS` | 300.0 |
| `DEFAULT_TOTAL_TIMEOUT_SECONDS` | 600.0 |
| `DEFAULT_BASE_DELAY_SECONDS` | 0.5 |
| `DEFAULT_MAX_DELAY_SECONDS` | 4.0 |
| `CURRENT_SCHEMA_VERSION` | 6 |
| `REGISTRY_SUBKEY` | `Software\LLM Retry Proxy` (HKCU) |
| `REGISTRY_VALUE` | `ConfigJson` (REG_SZ，字符串) |
| `TEST_CONFIG_ENV`（私有） | 环境变量名 `RETRY_PROXY_CONFIG_JSON` |
| `DEFAULT_KEEPALIVE_IDLE_MINUTES` | 3.0 |
| `MIN_KEEPALIVE_IDLE_MINUTES` | 0.5 |
| `MAX_KEEPALIVE_IDLE_MINUTES` | 1440.0 |
| `KEEPALIVE_CONTEXT_LIMIT`（来自 `keepalive.rs`） | 50_000 |

### 1.2 类型

**`ConfigError`**：单一变体 `Message(String)`，`io::Error` 可转换为它。

**`ClientType`**（枚举，Default = `Codex`）：`Codex`、`Claude`。
- `label()`：`"Codex"` / `"Claude Code"`；序列化字符串 `as_str()`：`"codex"` / `"claude"`。
- `for_legacy_route(name, port)`：**仅迁移用**——名称小写包含 `"claude"` 或端口 == 18081 → `Claude`，否则 `Codex`。

**`ProviderEndpoint`**：`name: String`（trim）、`base_url: String`（trim + 去尾部 `/`）。
`validate()`：名称非空；地址非空；`validate_base_url`：必须可解析为 URL，scheme 为 `http`/`https` 且有 host；不能含用户名/密码；不能含 query/fragment。

**`ProxyRoute`**（通道）字段及默认值：

| 字段 | 类型 | 默认 |
|---|---|---|
| `id` | String | "" |
| `name` | String | "" |
| `provider_name` | String | "" |
| `client_type` | ClientType | Codex |
| `listen_port` | u32 | 18080 |
| `max_retries` | u64 | 6 |
| `timeout_seconds` | f64 | 300 |
| `generation_timeout_seconds` | f64 | 300 |
| `total_timeout_seconds` | f64 | 600 |
| `base_delay_seconds` | f64 | 0.5 |
| `max_delay_seconds` | f64 | 4.0 |
| `desired_running` | bool | false |
| `keepalive_enabled` | bool | false |
| `keepalive_idle_minutes` | f64 | 3.0 |
| `keepalive_context_limit` | u64 | 50000 |

`ProxyRoute::validate()` 顺序：id 非空 → name 非空 → provider_name 非空 → `keepalive_context_limit > 0` → `keepalive_idle_minutes` 有限且在 [0.5, 1440] → `validate_retry_settings(...)`。
`local_url()` = `http://127.0.0.1:{listen_port}`。

**`validate_retry_settings`** 规则（错误文案前缀 `转发通道“{name}”：`）：
- `listen_port ∈ [1, 65535]`
- `timeout_seconds` 有限且 `0 < x ≤ 86400`
- `total_timeout_seconds` 有限且 `0 < x ≤ 86400`
- `generation_timeout_seconds` 有限且 `0 < x ≤ 86400`
- `base_delay_seconds` 有限且 `∈ [0, 3600]`
- `max_delay_seconds` 有限、`≥ base_delay_seconds`、`≤ 86400`

**`RouteRuntimeOverrides`**（环境变量覆盖，不持久化）：`route_id: String`，以及 `Option<>`：`upstream_base_url`、`listen_port(u32)`、`max_retries(u64)`、`timeout_seconds`、`generation_timeout_seconds`、`total_timeout_seconds`、`base_delay_seconds`、`max_delay_seconds`（f64）。

**`ProxyConfig`**（整体配置 + "当前选中通道镜像"字段）：
`upstream_base_url`、`client_type`、`listen_port`、`max_retries`、`timeout_seconds`、`generation_timeout_seconds`、`total_timeout_seconds`、`base_delay_seconds`、`max_delay_seconds`、`desired_running`、`keepalive_enabled`、`keepalive_idle_minutes`、`keepalive_context_limit`（默认同上表）、`providers: Vec<ProviderEndpoint>`、`routes: Vec<ProxyRoute>`、`selected_route_id: String`、`schema_version: u32 (=6)`、`runtime_overrides: Option<RouteRuntimeOverrides>`。

关键方法：
- `normalize()`：规范化 base_url/selected_route_id/providers/routes；若 routes 非空：selected_route_id 为空则取第一个路由；调用 `mirror_selected_route()`（把选中路由的所有字段复制到顶层，upstream_base_url 取对应 provider 的 base_url，再 `apply_runtime_overrides(route.id)`）。若 routes 为空：upstream_base_url 为空则取 providers[0].base_url；upstream_base_url 非空但 providers 中无该地址则用 `generated_provider_name`（hostname，重名则 `"{host} (2)"`, `(3)`…；无法解析 URL 时 `"默认服务商"`）新增 provider；再 `apply_runtime_overrides("")`。
- `provider_by_name(name)`：**大小写不敏感**匹配。
- `runtime_config_for(route_id)`：构造一个 routes 为空、`selected_route_id` 为空的运行时 `ProxyConfig`（providers 保留），应用 overrides（仅当 `overrides.route_id == route.id`），然后 `validate(true)`。这是启动通道时传给 `ProxyService::request_start` 的对象。
- `with_selected_route`、`with_route_running(route_id, bool)`、`with_running_state(bool)`。
- `validate(require_upstream)`：`keepalive_context_limit>0` → 顶层 `validate_retry_settings`（前缀空）→ 每个 provider validate，**名称重复（小写比较）**、**地址重复** → 每个 route validate，**ID 重复**、**名称重复（小写）**、**端口重复**、**引用服务商不存在（小写）** → routes 非空时 `selected_route_id` 必须存在 → overrides 端口若与其他路由端口相同则报 `本地端口重复` → upstream_base_url 为空时 `require_upstream` 为真则报 `请先新增并选择服务商`，否则通过；非空则 `validate_base_url(…, "上游基础地址")`。

### 1.3 注册表存储与 JSON 格式

- 读取：`RETRY_PROXY_CONFIG_JSON` 环境变量优先（测试注入，且此模式下 **保存为空操作**）→ `HKCU\Software\LLM Retry Proxy\ConfigJson`（不存在则返回 None）→ `builtin_config()`。
- 保存 `save_persistent_config`：`validate(false)` 后 `serde_json::to_string_pretty(canonical_value(config))` 写入注册表（`create_subkey` + `set_value`）。
- `canonical_value` 输出 JSON 结构（键顺序如下）：

```json
{
  "schema_version": 6,
  "client_type": "codex|claude",          // 选中路由的类型，否则顶层
  "upstream_base_url": "...",             // 选中路由对应 provider 的 base_url
  "listen_port": 18080, "max_retries": 6, "timeout_seconds": 300.0,
  "generation_timeout_seconds": 300.0, "total_timeout_seconds": 600.0,
  "base_delay_seconds": 0.5, "max_delay_seconds": 4.0, "desired_running": true,
  "providers": [{"name":"...","base_url":"..."}],
  "selected_route_id": "...",
  "routes": [{
    "id","name","provider_name","client_type","listen_port","max_retries",
    "timeout_seconds","generation_timeout_seconds","total_timeout_seconds",
    "base_delay_seconds","max_delay_seconds","desired_running",
    "keepalive_enabled","keepalive_idle_minutes","keepalive_context_limit"
  }]
}
```
注意：顶层字段是选中路由的镜像；若无选中路由但有 runtime_overrides，顶层写默认值且 `upstream_base_url` 为空、`desired_running=false`（不把环境变量覆盖写回）。**providers 不再保存 keepalive 字段**。

### 1.4 解析与迁移（`parse_config_value` → `(ProxyConfig, migrated: bool)`）

解析辅助：`required_string`/`optional_string`/`integer`(as_u64)/`number`(as_f64)/`boolean`，类型不符即报错（错误文案如 `配置文件中的 routes 第 {index} 项 listen_port必须是整数`）。**不做静默默认，类型错误直接失败。**

步骤：
1. `schema_version` = `as_u64`，缺省 0。
2. `providers` 数组 → `parse_provider`（`name`、`base_url` 必填字符串）。
3. `routes` 数组 → `parse_route(value, index, schema_version)`：`id`/`name`/`provider_name` 必填；`desired_running` 默认 false；`timeout_seconds` 默认 300；`listen_port` 默认 18080；`client_type`：字符串 `"codex"`/`"claude"`；**缺失时仅当 `schema_version < 6` 才用 `for_legacy_route(name, port)` 推断，否则报错 `…必须指定客户端类型 client_type：codex 或 claude`**（`null`/`"auto"`/`true` 等均报错）；`max_retries` 默认 6；`generation_timeout_seconds` 默认 300；`total_timeout_seconds` **默认 `max(600, timeout_seconds)`**；`base_delay_seconds` 默认 0.5；`max_delay_seconds` 默认 4.0；`keepalive_enabled` 默认 false；`keepalive_idle_minutes` 默认 3.0；`keepalive_context_limit` 默认 50000。
4. **旧版 provider 级保活迁移** `inherit_provider_keepalive`：对每个 route，找同名 provider（ASCII 大小写不敏感），对 `keepalive_enabled`/`keepalive_idle_minutes`/`keepalive_context_limit` 三个键，**route JSON 中缺失而 provider JSON 中存在**的才继承（逐字段）；有任何继承则 `migrated_keepalive = true`。provider 上的非法类型值（如 `"true"`, `"3"`, `-1`）会报错。
5. 顶层：`listen_port`、`selected_route_id`(默认 "")、`upstream_base_url`(默认 "")、`client_type`（缺省：`schema_version<6` → `for_legacy_route("", listen_port)`；否则若存在 `routes` 键 → Codex；否则报错）、`max_retries`、`timeout_seconds`、`generation_timeout_seconds`、`total_timeout_seconds`(默认 `max(600,timeout)`)、`base_delay_seconds`、`max_delay_seconds`、`desired_running`；顶层 keepalive 三项取默认；`schema_version` 置为 6；然后 `normalize()`。
6. **单地址旧格式迁移**：`legacy = !has_routes && upstream_base_url 非空`。若 legacy 且 routes 为空：找到 base_url 匹配的 provider，创建 `ProxyRoute { id: "legacy-default", name: "默认通道", provider_name, client_type, listen_port, max_retries, timeout_seconds, generation_timeout_seconds, total_timeout_seconds, base_delay_seconds, max_delay_seconds, desired_running, 其余默认 }`，再从该 provider JSON 继承 keepalive 三项（route_value 传 `Null`，即全部继承），`selected_route_id = "legacy-default"`，再 normalize。
7. 返回 `migrated = legacy || migrated_keepalive || schema_version < 6`。

`load_persistent_config(environ)`：解析 → `validate(false)` → `environment_overrides` 非 None 则设置 `runtime_overrides` 并 `normalize()` → 再 `validate(false)`。

### 1.5 环境变量覆盖（`environment_overrides`）

`route_id` = routes 为空 ? "" : `selected_route_id`（**只作用于当前选中通道**）。变量（值 trim，空视为未设置；解析失败报 `环境变量 X 的值无效`）：
`UPSTREAM_BASE_URL`(string)、`RETRY_PROXY_PORT`(u32)、`RETRY_MAX_RETRIES`(u64)、`RETRY_TIMEOUT_SECONDS`(f64)、`RETRY_GENERATION_TIMEOUT_SECONDS`(f64)、`RETRY_TOTAL_TIMEOUT_SECONDS`(f64)、`RETRY_BASE_DELAY_SECONDS`(f64)、`RETRY_MAX_DELAY_SECONDS`(f64)。任一存在才返回 Some。覆盖只影响顶层镜像与 `runtime_config_for(选中通道)`，`canonical_value` 保存时不写回（测试 `generation_timeout_override_is_route_local_and_not_persisted` 等验证）。

### 1.6 内置默认配置 `builtin_config()`

```
upstream_base_url = "https://anyrouter.top", client_type=Codex, listen_port=18080,
max_retries=100, timeout=600, generation=300, total=600, base_delay=0.5, max_delay=8.0, desired_running=true
providers: [("anyrouter.top","https://anyrouter.top"), ("sotamodel.net","https://sotamodel.net")]
routes:
  1) id="legacy-default", name="默认通道", provider="anyrouter.top", client_type=Codex(默认), port=18080,
     max_retries=100, timeout=600, generation=300, total=600, base=0.5, max=8.0, desired_running=true, keepalive 默认
  2) id="2da608c46f0842039fdf8ad07e46cf20", name="Claude Code", provider="anyrouter.top", client_type=Claude,
     port=18081, max_retries=200, timeout=300, generation=300, total=600, base=0.5, max=1.0, desired_running=false
selected_route_id="legacy-default"
```
`application_dir()` = 当前 exe 所在目录（失败则 `"."`），日志目录 = `{application_dir}/logs`。

---

## 2. 服务生命周期（`service.rs`）

### 2.1 状态机

`ServiceState`：`Stopped`、`Starting`、`Running`、`Stopping`、`Error`。

转换：
- `request_start(config)`：先 `config.validate(true)`；仅当状态 ∈ {Stopped, Error} 才启动（否则返回 `Ok(false)`）。置 `Starting`，保存 `runtime_config`，清 `startup_error`；配置保活看门狗（enabled、idle=分钟×60 秒、context_limit、flavor=`KeepAliveFlavor::from(client_type)`）；创建 `oneshot` stop 通道；spawn OS 线程 `retry-proxy-{route_name}` 运行 `run_service`。
- `run_service`：创建 Tokio Runtime（失败 → Error `无法初始化 Tokio 运行时`）；创建 `CancellationToken` 存入 inner；`RetryProxy::new(...)`（失败 → Error）；`TcpListener::bind(127.0.0.1:port)`（失败 → Error `无法监听 http://127.0.0.1:{port}：{err}`）；`keepalive.register_service(flavor)`；置 `Running`；日志 `代理服务已启动：{local_url}（上游请求跟随系统代理）`；spawn 保活轮询任务（`KEEPALIVE_POLL_INTERVAL` = 5 秒 或 `preparation_requested()` 触发 → `send_due_keepalive_probe()`）；`tokio::select!` 于 `axum::serve(listener, router)` 与（stop 信号 | cancel）。**停止是硬停**：先读 `metrics.snapshot().active_requests`，然后 `cancel.cancel()`，不做 graceful shutdown；abort 保活任务；`set_state_if_not_error(Stopped)`（同时清 cancel）；日志 `代理服务已停止，丢弃 {N} 个处理中的请求` 或 `代理服务已停止`；`runtime.shutdown_timeout(100ms)`。
- `request_stop()`：状态 ∈ {Stopped, Error, Stopping} 直接返回；否则置 `Stopping`，`cancel.cancel()`，发送 stop 信号。
- `stop(timeout)`：`request_stop` 后轮询线程结束（10ms 间隔），超时则置 `Error` + `代理服务未能在 {N} 秒内停止`。UI 退出时对每个 service `stop(15s)`。
- `start(config, timeout)`：同步版（测试用），轮询 10ms 直到 Running / Error / 超时（超时则 `request_stop` 并报 `代理服务启动超时`）。
- `ensure_running()`：状态 == Running 且 cancel 未取消，否则 `通道未运行，请先启用本通道`。

### 2.2 运行时对象

`ProxyService { route_name, metrics: Arc<ProxyMetrics>, keepalive: Arc<KeepAliveWatchdog>, logger: Logger, inner: Arc<Mutex<ServiceInner>> }`
`ServiceInner { state, runtime_config: Option<ProxyConfig>, startup_error: Option<String>, cancel: Option<CancellationToken>, stop_sender: Option<oneshot::Sender>, thread: Option<JoinHandle>, notifier: UiNotifier }`

- `ProxyService::with_daily_statistics(logger, route_id, route_name)`：`ProxyMetrics::from_daily_logs(logger.directory(), route_id, route_name)`，恢复后若有 `statistics_warning` 则 route_logger.warn，否则若 total_requests>0 写 info `已恢复 {date} 当日统计：请求 {n}，成功 {n}，失败 {n}，重试 {n}，历史未完成 {n}`。
- UI 侧（`ui.rs` 约 580-606 行）：每个 route.id 对应一个 `ProxyService`（`HashMap<route_id, ProxyService>`）；路由改名或看门狗替换且服务处于 Stopped/Error 时，用 `ProxyService::new` 重建但**复用旧 `metrics` Arc**。`start_route` 调用 `config.runtime_config_for(route_id)` → `request_start`，成功则 `with_route_running(route_id, true)` 并保存。启动失败**不改 desired_running**。

### 2.3 与 UI 的通信

- **`UiNotifier`**（`ui_notifier.rs`）：`Option<Arc<dyn Fn()>>` 回调，`notify()` 请求重绘；默认 no-op。`ProxyService::set_ui_notifier` 同时下发给 `metrics` 和 `keepalive`。状态变化（`set_state`/`set_state_if_not_error`/`set_error`）、metrics 每次变化、日志每行入队都调用 notify。
- 状态通过 `Arc<Mutex<ServiceInner>>` 共享读取；日志通过 `std::sync::mpsc::sync_channel(10_000)` 传给 UI（`LogReceiver = Receiver<String>`），UI 在 `poll_events` 中 `try_recv` 取空。
- 无 tokio 与 UI 之间的 channel；UI 直接轮询 `service.state()`、`metrics.snapshot()`、`keepalive.snapshot()`。

---

## 3. 代理核心（`proxy.rs`）

### 3.1 常量

| 常量 | 值 |
|---|---|
| `MAX_REQUEST_BODY_BYTES` | 100 MiB (104,857,600) |
| `MAX_RETRY_RESPONSE_BODY_BYTES` | 1 MiB (1,048,576) |
| `MAX_GENERATION_PREFIX_BYTES` | 1 MiB (1,048,576) |
| `KEEPALIVE_MARKER_HEADER` | `x-retry-keepalive` |
| `HOP_BY_HOP_HEADERS` | connection, keep-alive, proxy-authenticate, proxy-authorization, te, trailer, transfer-encoding, upgrade |
| `RETRYABLE_STATUS_CODES` | 408, 425, 429（另加 500–599） |
| `RETRY_JITTER_SECONDS` | 0.5 |

### 3.2 路由与构造

- `RetryProxy::router()`：`GET /_retry/health` → `health_handler`；其余全部方法/路径 → `proxy_handler`；`DefaultBodyLimit::max(100 MiB)`。
- `RetryProxy::with_resolver(config, logger, metrics, cancel, proxy_resolver)`：`config.validate(true)`；构造 reqwest Client：**禁止重定向**（`Policy::none()`）、`connect_timeout = timeout_seconds`、`read_timeout = timeout_seconds`（连续无数据超时）、`no_proxy()` + `Proxy::custom(resolver.resolve(url))`（系统代理由 `system_proxy.rs` 决定）、TLS 为 native-tls（SChannel）。`PromptCache::new(upstream_base_url)`。`random_value` 默认 `rand::random::<f64>` ∈ [0,1)。

### 3.3 `proxy_handler` 错误映射

| `ProxyError` | HTTP | 正文 |
|---|---|---|
| `Cancelled` | **499** | 空 |
| `DeadlineExceeded` | **504** | `{"error":{"type":"proxy_timeout","message":"请求超过总等待上限，已停止重试"}}` |
| `Body(msg)` | **400** | `{"error":{"type":"invalid_request","message":"<msg>"}}` |

Content-Type 均为 `application/json; charset=utf-8`。

### 3.4 `handle_request` 流程

1. `deadline = now + total_timeout_seconds`；`started_at = now`；`request_id = Uuid::new_v4().simple()`（32 位 hex）。
2. `safe_path = uri.path()`（无 query）。
3. **保活请求识别（请求头阶段）** `internal_request_cancel(headers)`：读取 `x-retry-keepalive` 头值，或 `x-codex-turn-metadata` 头（JSON）中的 `retry_proxy_keepalive` 字符串；在全局 `internal_sessions()` 表（`HashMap<marker, CancellationToken>`）中查到才算内部请求。
4. `counts_as_real_request = internal_cancel.is_none() && method != HEAD`（HEAD 探测不算真实请求）。
5. 若为内部请求：`proxy.metrics` 换成全新的 `ProxyMetrics::default()`（不计入统计）、`proxy.cancel` 换成该会话的 token、`request_id = "保活-" + id`。
6. 创建 `RequestFinishGuard`（Drop 时：若 `awaiting_response` 仍为 true → `metrics.failure` + warn `[{id}] 客户端在响应转发前断开，已取消当前请求，不再重试，耗时 {:.2} 秒`；然后 `keepalive.request_finished()` 与 `metrics.request_finished()`）。
7. `select!(biased)`：通道 cancel → Cancelled；内部 cancel → Cancelled；`sleep_until(deadline)` → DeadlineExceeded；否则：
   - `to_bytes(body, 100 MiB)`（失败 → `Body(err)`）。
   - 若是真实请求且尚未识别：**正文阶段保活识别** `internal_body_request_cancel(body)`：body 非空且会话表非空时，解析 `{"client_metadata":{"x-codex-turn-metadata":"<json string>"}}`，内层 JSON 的 `retry_proxy_keepalive` 在会话表中 → 转为内部请求（同第 5 步）。
   - `guard.start()`：真实请求此时 `metrics.request_started(id, method, path)` + `keepalive.request_started(flavor)`（flavor 由 `KeepAliveFlavor::detect(path)`：以 `/messages` 结尾 → Claude；`/responses` 或 `/chat/completions` → Codex；否则 Unknown）。
   - 真实请求：`strip_internal_request_metadata(body)`——若 body 是 JSON 且 `client_metadata["x-codex-turn-metadata"]`（字符串形式 JSON）含 `retry_proxy_keepalive` 键，则移除该键；内层空则删除 `x-codex-turn-metadata`；`client_metadata` 空则删除之；重新序列化。非 JSON / 无该键 → 原样。
   - `select!(biased)`：`proxy.cancel` → Cancelled；否则 `handle_request_inner(...)`。
8. 结果处理：Cancelled → `metrics.failure` + info `[{id}] 通道或后台任务已取消，已停止当前请求，不再重试，耗时 {:.2} 秒`；DeadlineExceeded → failure + warn `[{id}] {method} {path} -> 请求总等待达到 {total} 秒，已取消当前请求，不再重试，向客户端返回 HTTP 504，耗时 {:.2} 秒`；Body → failure。
9. 成功 → `wrap_request_lifecycle(response, guard, deadline, cancel)`。

### 3.5 `wrap_request_lifecycle`（响应交付）

- 置阶段 `ReceivingResponse`。
- 独立 task 把 body 数据流通过 **容量 1 的 `mpsc::channel`** 转发；`select!(biased)`：接收端关闭 → Ok；cancel → `Interrupted("代理请求已取消")`；deadline → `TimedOut("代理请求达到总等待上限")`。转发完成后再等待下游"读完队列"信号（`delivery_receiver`），同样受 cancel/deadline（`响应未在总等待上限内交付`）约束；之后 drop guard（结清处理中计数）。
- 下游流：读完所有块后，若 completion 或 delivery 结果为 Err 则向客户端 yield 错误（连接中断）。

### 3.6 `handle_request_inner`（重试循环）

1. `copy_request_headers`：删除 hop-by-hop、`Connection` 头列出的 token、`host`、`content-length`、`x-retry-keepalive`、`x-retry-preparation-id`；`x-codex-turn-metadata` 头若为 JSON 含 `retry_proxy_keepalive` 则移除该键（空对象则整个头删除）。其余头（含 `authorization`）原样透传。
2. `request_metadata(body)`：JSON 反序列化取 `model`（`clean_model`：去控制字符、最多 128 字符、trim，空则 None）与 `stream`(bool 默认 false)。
3. POST 请求捕获 `KeepAliveTemplate`（method、path_and_query、headers、body）。
4. `streaming = metadata.stream || Accept 头包含 "text/event-stream"`。
5. `cache_request = prompt_cache.prepare(method, path, headers, body, background = id 以 "保活-" 开头)`；`metrics.cache_key(id, state)`。
6. 若 `streaming || model.is_some()`：设置请求头 `Accept-Encoding: identity`。
7. `build_target_url`：`{base 去尾/}/{path 去头/}` + `?query`；解析失败 → Body 错误。
8. `using_system_proxy = resolver.resolve(target).is_some()`。
9. `total_attempts = max_retries + 1`；`last_response: Option<BufferedResponse>`。
10. `for attempt in 0..total_attempts`：
    - `metrics.request_attempt(id, attempt+1)`（阶段 WaitingResponse）。
    - `send_cache_aware(...)`（见 §5.3）→ `UpstreamResponse { status, headers(copy_response_headers: 去 hop-by-hop、Connection token、content-length), expected_body_bytes = Content-Length, stream }`。
      - 非流式请求额外设置 reqwest 整体 `timeout = timeout_seconds`；流式不设整体超时（只有 connect/read timeout）。
      - 网络错误 → `handle_attempt_failure(status=None)`；返回 true（耗尽）→ `retry_exhausted_response`；否则 continue。
    - `retryable = is_retryable_status(status)`（408/425/429/5xx）。
    - **若 retryable 且 attempt < max_retries**：若 `Content-Length` 缺失或 ≤ 1 MiB → `buffer_upstream_response`：
      - `Buffered{body, first_byte_seconds}` → 记 `last_response`；info 日志 `format_completed_attempt`（无 details）；`delay = retry_delay(attempt, Some(status), Some(headers))`；`metrics.retry(id, attempt+1)`；阶段 `WaitingRetry`；warn `[{id}] 上游 HTTP {status} 可重试，{delay:.3} 秒后再次请求`；`wait_delay(delay)`；continue。
      - `TooLarge{prefix, chunk}` → 两块作为前缀透传，**不再重试**（往下走转发）。
      - 网络读错 → `handle_attempt_failure(Some(status))`。
      - Content-Length > 1 MiB 或 TooLarge：warn `[{id}] 上游 HTTP {status} 错误正文超过 1048576 字节暂存上限，改为完整流式转发，不再因本次状态码重试`。
    - 若 retryable 但已是最后一次：warn `[{id}] 重试耗尽，向客户端返回最后一次上游响应 HTTP {status}`。
    - `prepare_stream_response(...)`：Ok → 返回；Cancelled → Cancelled；`NoGeneration`/`Network` → `handle_attempt_failure(Some(status))`（耗尽则 `retry_exhausted_response`，否则下一轮）。
11. 循环结束 → `retry_exhausted_response`。

`retry_exhausted_response`：有 `last_response` → warn `[{id}] 重试耗尽，返回客户端最后一次完整上游响应 HTTP {status}` 并原样返回（状态、头、体）；否则 **502** `{"error":{"type":"upstream_unavailable","message":"上游暂时不可用，已尝试 {attempts} 次"}}`。

`handle_attempt_failure`：warn `[{id}] 第 {n}/{total} 次 {method} {path} -> {上游 HTTP x | 上游状态码：无}，{label}，{将在 {d:.3} 秒后重试 | 已达到重试上限}，{timing}`；label 来自 `network_error_label` 或 NoGeneration 原因；有下次则 `metrics.retry` + 阶段 WaitingRetry + `wait_delay`（`retry_delay(attempt_number-1, None, None)`，**网络/生成失败不使用 Retry-After**）；耗尽则 `metrics.failure` 返回 true。

`wait_delay(d)`：`d = clamp(d, 0, total_timeout_seconds)`；受 cancel 打断。

### 3.7 退避算法 `retry_delay(attempt, status, headers)`

```
if status ∈ {429, 503} 且 Retry-After 可解析为 seconds:
    return seconds + rand()*0.5          // 不受 max_delay 限制
base = base_delay_seconds
repeat attempt 次: if base == 0 || base >= max_delay: break; base = min(base*2, max_delay)
if base == 0: return 0
minimum = max(base - 0.5, 0)
maximum = min(base + 0.5, max_delay)
return minimum + rand() * (maximum - minimum)
```
`attempt` 为 0 起（第一次重试 attempt=0）。测试给定：base=0.5,max=4,rand=0 → attempts 0..4 = 0.0, 0.5, 1.5, 3.5, 3.5。

`parse_retry_after(value)`：trim 后解析 f64（有限值，取 `max(0)`）；否则 `httpdate::parse_http_date` → 与当前时间差秒数（过去则 0）。

### 3.8 错误正文暂存 `buffer_upstream_response`

跳过空块；首个非空块记录 `first_byte_seconds` 并置阶段 `ReceivingResponse`；若 `chunk.len() > 1 MiB - body.len()` → `TooLarge{prefix=body, chunk}`（恰好等于上限仍可缓冲）；受 cancel 打断。

### 3.9 `prepare_stream_response`（等待生成 + 转发）

- `stats = ResponseStats::new(headers, path, model).with_cache_key_state(state)`。
- `generation_gate` 仅当 `200 ≤ status < 300` 且 `stats.is_api_event_stream()`（Content-Type `text/event-stream` 且是 API 路径/有 model）时创建；此时阶段置 `WaitingGeneration`；`generation_deadline = now + generation_timeout_seconds`（**每次尝试收到响应头后计时一次，不因心跳重置**）。
- 循环读块（`select!(biased)`：cancel → Cancelled；生成超时（仅有 gate 时）→ 若 `gate.finish()` 为 true（存在未识别/未终止消息）则 warn `[{id}] 等待生成到期时存在未识别的消息，原样转发已收内容，不再重试` 并开始转发，否则 `NoGeneration("等待生成达到 {N} 秒，尚未向客户端转发响应{failure_log_fields}")`）：
  - 非空块：`stats.observe`；若有 gate：块超过 `1 MiB - prefix.len()` → warn `[{id}] 生成前消息超过 1048576 字节暂存上限，改为完整流式转发，不再重试` 并 break 开始转发（gate 视为放行）；否则 `gate.observe(chunk)` 为 false → 追加 prefix 继续等待；为 true → break。
  - 流结束（None）：若 gate 存在且 `!gate.finish()` → `stats.finish` → `NoGeneration("上游流在生成内容前结束，未收到完成事件，尚未向客户端转发响应{...}")`（可重试）；否则 break。
  - 网络错误 → `Network(error)`（可重试，因尚未转发）。
- 阶段置 `ReceivingResponse`；创建 `StreamLifecycle`；若上游已结束调用 `finish()` 否则 `check_completion()`。
- 输出流：若未完成且已过 deadline → `interrupted(超时原因)` 并 yield `TimedOut`；先 yield prefix（生成前暂存的原始字节）、再 first_chunk；随后 `while !upstream_finished && (!completed || status >= 400)`：cancel → `interrupted("代理通道已停止，请求已取消")` + `Interrupted`；deadline → `interrupted("请求总等待达到 {N} 秒，已停止接收上游响应")` + `TimedOut`；上游 None → `finish()`；块 → `observe` 后 yield；读错 → `interrupted(network_error_label(ReadingResponse))` + `UnexpectedEof`。
- 结束后若 `2xx && stats.missing_terminal_event()` → yield `UnexpectedEof("上游流提前结束，未收到完成事件")`（以传输中断通知客户端）。
- **成功流内出现终止事件（`response.completed`/`message_stop`/`[DONE]`）时，`completed` 为 true 且 status<400，循环立即退出，不等上游断开。**

`StreamLifecycle`：`check_completion`：收到字节数 ≥ Content-Length → `finish()`；否则 status<400 时按 `stats.outcome()`：Complete → `complete()`，Failed → `interrupted(reason)`。`complete()`：info 完成日志（`format_completed_attempt` + `stats.log_fields()`）；**status == 200** → `metrics.success(id, stats.cache_request(id))` + `keepalive.remember(template)`；否则 `metrics.failure`。`interrupted(reason)`：`metrics.failure`；warn `[{id}] 第 {n}/{total} 次 {method} {path} -> 上游 HTTP {status}，响应未完成，原因：{failure_summary 或 reason}，不再重试（已进入响应转发阶段）{failure_log_fields}，{timing}`。Drop 未完成时：cancel 已取消 → `代理通道已停止，请求已取消`；过 deadline → 超时原因；否则 `客户端断开或响应未读完`。

### 3.10 日志格式函数

- `timing_text`：`首字 {:.2} 秒 / 耗时 {:.2} 秒` 或 `首字：无 / 耗时 {:.2} 秒`。
- `attempt_prefix`：attempt_number==1 && status==200 → 空；否则 `第 {n}/{total} 次 `。
- `format_completed_attempt`：`[{id}] {prefix}{method} {path} -> 上游 HTTP {status}{details}，{timing}`。
- `network_error_label(error, using_system_proxy, phase)`：`{reason}（{kind}），链路：{系统代理|直连}{cause_fields}`；kind/reason：is_timeout → `Timeout` + (`建立上游连接超时` | AwaitingResponse:`收到上游响应前超时` | ReadingResponse:`读取上游响应超时`)；is_connect → `ConnectError`/`建立上游连接失败`；否则 `ClientError` + (`发送请求或等待上游响应失败` | `读取上游响应失败`)。
- `network_cause_fields`：沿 source 链（最多 16 层）找最深的 `io::Error`；无 → `，底层原因：未提供可识别的系统错误`；有 → `，底层原因：{描述}` + 可选 `，系统错误码 {code}`。描述表：ConnectionRefused→连接被拒绝、ConnectionReset→连接被重置、ConnectionAborted→连接被中止、TimedOut→网络操作超时、NotConnected→连接未建立、BrokenPipe→连接已关闭、UnexpectedEof→连接提前结束、PermissionDenied→系统拒绝网络访问、AddrNotAvailable→网络地址不可用、AddrInUse→网络地址已被占用、HostUnreachable→目标主机不可达、NetworkUnreachable→目标网络不可达、InvalidData→收到无法解析的网络数据、其他→系统网络错误。

### 3.11 保活探测发送 `send_probe`（供参考）

`timeout = min(timeout_seconds, total_timeout_seconds)`；日志 `供应商保活 [会话 {session前8位}] 第 {turn} 轮，随机题号 {i}/250，{Codex|Claude Code} CLI，{沿用本机客户端配置|使用本次输入的 Key 经本通道转发}，问题：{q}`；结果日志前缀 `供应商保活 [会话 xxxxxxxx] 第 N 轮 {flavor} CLI`，成功 `… 完整回复{log_fields}，当前会话 {ctx}/{limit} token，{timing}，回答：{前180个非控制字符}{reset}，{next_round}`；失败 warn `…，响应未完成：{reason}，耗时 {:.2} 秒，{after_failure}`；中断 info `…，本轮已中断：…`。准备重试延迟 1.5–2.5 秒（`PREPARATION_RETRY_MIN/MAX_DELAY`）。

---

## 4. 响应统计（`response_stats.rs`）

常量：`MAX_OBSERVATION_BYTES` = 8 MiB（JSON 正文/单个 SSE 事件观察上限）；`MAX_DIAGNOSTIC_IDENTIFIER_BYTES` = 128；答案捕获上限 512 KiB；错误消息 `sanitize_error_detail` 取前 240 个非控制字符（仅内部，**不写日志**）。

### 4.1 格式判定 `BodyFormat`

`Content-Encoding` 存在且非 `identity` → `Opaque`；Content-Type（去参数、小写）`text/event-stream` → `EventStream`；`application/json` 或 `*+json` → `Json`；否则 `Detect`：首个非空白字节 `{`/`[` → Json；`:`/`d`/`e`/`r` → EventStream；其他 → Opaque。

`is_api_response` 初值：`model.is_some()` 或路径（去尾 `/`）以 `/messages`、`/responses`、`/chat/completions` 结尾；后续遇到 model/usage/`response.*`/`message_*`/`content_block_*`/`choices`/`[DONE]` 也置 true。

`upstream_request_id`：依次取响应头 `x-request-id`、`request-id`、`x-oneapi-request-id`，经 `clean_diagnostic_identifier` 过滤。

### 4.2 SSE 解码 `EventDecoder`

按字节处理，支持 `\r`、`\n`、`\r\n`；去 UTF-8 BOM；`data:`（可选一个空格）累积并加 `\n`；`event:` 设置名称；`data`/`event` 无冒号行；`:` 注释、`id:`、`retry:` 忽略；其他字段名 → `unsupported = true`。空行结束事件（data 非空才产出，去掉末尾 `\n`）。事件累计超过 8 MiB → 丢弃并 `skip_event`、`unsupported = true`。`pending_line_is_supported()`：当前未完成行是否为 BOM/`:`/`data:`/`event:`/`id:`/`retry:` 的前缀或以之开头。

### 4.3 等待生成门 `GenerationGate`

`observe(chunk)`：`ready = 任一事件 !is_waiting_event(event) || decoder.unsupported || !pending_line_is_supported()`。`finish()` = `observe(b"\n\n")`（强制结算未终止事件）。

`is_waiting_event`（返回 true 表示"仍在等待，不转发"）：
- data 为空：事件名 ∈ {"", "message", "ping", "keepalive", "heartbeat"}。
- data 非 JSON → false（放行）。
- `event_type = value.type ?? event.name`。若 event 名非空且 ≠ "message" 且 ≠ event_type → false；`has_generated_content` → false；`value.error` 非 null → false。
- 按 event_type：
  - `ping`/`keepalive`/`heartbeat`：对象仅含 `type`/`timestamp`/`time`/`sequence_number`，`data` 为 null 或空对象。
  - `response.created`/`response.in_progress`/`response.queued`：`response` 为对象，`output` 空/缺、`error` 空、`incomplete_details` 空、`status` 缺或 ∈ {queued, in_progress}。
  - `response.output_item.added`：`item.type` ∈ {message, reasoning} 且 `content`、`summary` 空、`encrypted_content` 空、`status` 缺或 in_progress。
  - `response.content_part.added`：`part.type == output_text`、`text` 空、`annotations`、`logprobs` 空。
  - `response.reasoning_summary_part.added`：`part.type == summary_text` 且 text 空。
  - `response.output_text.delta`/`response.refusal.delta`/`response.reasoning_text.delta`/`response.reasoning_summary_text.delta`：`delta == ""` 且 logprobs 空。
  - `message_start`：`message` 对象，`content` 空、`stop_reason` null、`error` null。
  - `content_block_start`：`content_block.type` ∈ {text, thinking} 且 text/thinking/signature 空、citations 空。
  - `content_block_delta`：`delta.type == text_delta && text == ""` 或 `thinking_delta && thinking == ""`。
  - `""`/`message`（OpenAI chat）：`choices` 数组每项 `finish_reason` null 且 `delta` 对象仅含 `role == "assistant"`、`content`/`reasoning`/`reasoning_content` 为空。
  - 其他 → false（未知事件保守放行）。

`has_generated_content(value, event_type)`（也用于首字时间）：
- `response.*.delta`：`delta` 非空。
- `content_block_delta`：`/delta/text`、`/delta/thinking`、`/delta/partial_json` 任一非空。
- `content_block_start`：`content_block.type` ∉ {text, thinking}（如 tool_use）或 text/thinking/signature 非空。
- `response.output_item.added|done`：`item.type` ∉ {message, reasoning}（如 function_call、web_search_call）或 `item.content`/`summary`/`encrypted_content` 非空。
- `response.content_part.added|done`：`part.text` 或 `part.refusal` 非空。
- 通用：`choices[].delta|message` 的 `content`/`reasoning_content`/`reasoning`/`tool_calls`/`function_call` 非空；或 `/response/output` 非空；或 `/message/content` 非空。

### 4.4 事件结果 `observe_value`

`envelope = value.response（对象）?? value.message（对象）?? value`。
- `envelope.model`（clean_model）→ 模型名。
- `envelope.usage`（对象）→ `TokenUsage.update(usage, message_delta = event_type == "message_delta")`。
- `last_event_type` = clean 后的 event_type。
- `outcome`：`response.completed`/`message_stop` → Complete；`response.failed` → Failed("上游返回 response.failed")；`response.incomplete` → Failed("上游返回 response.incomplete")；`error` → Failed("上游返回错误事件")；`envelope.error` 非 null → Failed("上游返回错误响应")；`envelope.status == "incomplete"` → Failed("上游返回未完成的响应")；否则保持。`data: [DONE]` → Complete。
- `finish()`：Json 格式则解析整个正文 `observe_value(value, "")`；EventStream 结算后若无 outcome 且 `is_api_response` → `missing_terminal_event = true`，outcome = Failed("上游流提前结束，未收到完成事件")。

`TokenUsage` 提取（JSON pointer，取第一个存在的 u64）：
- input：`/input_tokens`, `/prompt_tokens`
- output：`/output_tokens`, `/completion_tokens`
- cache_read：`/cache_read_input_tokens`, `/input_tokens_details/cached_tokens`, `/prompt_tokens_details/cached_tokens`, `/prompt_cache_hit_tokens`
- cache_creation：`/cache_creation_input_tokens`, `/input_tokens_details/cache_write_tokens`, `/prompt_tokens_details/cache_write_tokens`
- reasoning：`/output_tokens_details/reasoning_tokens`, `/completion_tokens_details/reasoning_tokens`
- Claude `message_delta` 特殊规则：input 只在 `next>0 && (当前==0 || next<当前 || (之前来自 delta 且相等))` 时替换；否则保留 `message_start` 的值；cache_read/cache_creation 在 delta 中只在替换 input 或当前为 0 时更新。output 总是取最新。

首字时间 `first_content_seconds()`：API 事件流 → 首个 `has_generated_content` 事件时间；否则首字节时间。

### 4.5 日志字段 `format_log_fields(failed)`

失败时先追加：每个 `，{label} {value}`（error_fields）→ `，上游请求 ID {id}` → API 事件流：`，生成内容：已读取到|未读取到`、`，最后事件 {type}`。然后：`，模型 {model}`；API 响应：`，输入 {n|未获取} / 输出 {n|未获取} token`（失败且缺失时附 `（未读取到用量统计）`/`（用量统计不完整）`）；`，缓存命中 {n} token`、`，缓存写入 {n} token`、`，推理 {n} token`（存在时）；`，缓存命中率 {:.1}%`（+`（完全未命中）`）；`，缓存标识：{label}`。

`observe_failure` 提取 error_fields（`error = envelope.error 对象 ?? value.error 对象 ?? value`）：`("上游错误码", error.code)`、`("上游错误类型", error.type，且 ≠ event_type)`、`("错误参数", error.param)`、`("未完成原因", envelope.incomplete_details.reason)`；字符串经 `clean_diagnostic_identifier`，整数直接转字符串。

### 4.6 错误码 → 中文 `failure_summary()` 全表

| 标签 | 值 | 说明 |
|---|---|---|
| 上游错误码/上游错误类型 | `rate_limit_exceeded`, `rate_limit_error`, `too_many_requests` | 上游请求超限 |
| 同上 | `insufficient_quota` | 上游可用额度不足 |
| 同上 | `overloaded_error`, `server_overloaded` | 上游服务繁忙 |
| 同上 | `context_length_exceeded` | 请求内容超过上游长度限制 |
| 同上 | `invalid_api_key`, `authentication_error` | 上游身份验证失败 |
| 同上 | `server_error`, `api_error` | 上游服务内部错误 |
| 未完成原因 | `max_output_tokens`, `max_tokens` | 达到上游输出长度限制 |
| 未完成原因 | `content_filter` | 上游内容审核拦截 |
| 其他 | — | None（保留原 reason） |

### 4.7 安全标识过滤 `clean_diagnostic_identifier`

拒绝：空；长度 > 128 字节；含非 `[A-Za-z0-9_\-.\[\]]` 字节；小写后以 `sk-`、`sess-`、`eyj` 开头。

### 4.8 缓存请求 `cache_request(id)`

id 以 `保活-` 开头 → None；`CacheInputAccounting::for_path(path)` 为 None → None；否则 `CacheRequest{ request_id, model ?? "未获取", completed_at_unix_ms=now, input_tokens, cached_tokens=cache_read, cache_creation_tokens, input_accounting, cache_key_status = state.label() ?? "保持原请求" }`。
`context_tokens(claude)`：input（Claude 加 cache_read+cache_creation）+ output，checked_add。

---

## 5. Prompt Cache Key 补全（`prompt_cache.rs`）

常量：`MAX_REQUEST_INSPECTION_BYTES` = 4 MiB；`MAX_UNSUPPORTED_SCOPES` = 128；`MAX_CACHE_ERROR_BYTES` = 64 KiB。

`CacheKeyState`（serde snake_case）：`unchanged`(label None)、`client`("客户端已设置")、`added`("代理已补全")、`missing_session`("未补全（缺少会话信息）")、`unsupported`("保持原请求（上游不支持）")。

### 5.1 `PromptCache::prepare(method, path, headers, body, background)`

1. `openai_endpoint(path)`：path 去尾 `/` 后 == `/responses`|`/v1/responses` → `"responses"`；`/chat/completions`|`/v1/chat/completions` → `"chat/completions"`；否则返回原样(Unchanged)。
2. 排除：`background`、method ≠ POST、body > 4 MiB、`!editable_body(headers)`（Content-Encoding 非 identity；Content-Type 存在且既非 `application/json` 也非 `*+json`；存在任一头 `content-md5`、`digest`、`content-digest`、`signature`、`signature-input`、`x-amz-content-sha256`）。
3. 轻量探测 `RequestProbe{model, prompt_cache_key 是否存在}`；非 JSON → 原样；`is_gpt_model(model)`：以 `gpt-` 开头、≤128 字节、全 ASCII 可见字符；否则原样。
4. 已存在 `prompt_cache_key`（**含 null/""/任意值**）→ `Client`。
5. 整体解析为 JSON 对象（否则原样）；`session_identity`：
   - 请求头 `thread_id` → `session_id`（`valid_identity`：非空、≤256 字节、全 ASCII 可见）；
   - 请求头 `x-codex-turn-metadata`（≤16 KiB JSON）中 `thread_id` 或 `threadId`；
   - 正文 `client_metadata["x-codex-turn-metadata"]`（字符串 JSON）中同上；
   - 正文 `conversation`（字符串或对象 `.id`）。
   - 均无 → `MissingSession`。
6. `scope = SHA256(hash_part(namespace) ‖ hash_part(endpoint) ‖ hash_part(model) ‖ for name in [authorization, x-api-key, api-key, openai-organization, openai-project]: hash_part(name) ‖ hash_part(count as u64 LE 8 bytes) ‖ for each value: hash_part(value))`，其中 `hash_part(v) = update(len(v) as u64 LE 8 bytes) + update(v)`；`namespace = SHA256(upstream_base_url bytes)`。
7. scope 在 `unsupported` LRU 中 → `Unsupported`。
8. `key = "rp1_" + hex(SHA256(hash_part("retry-proxy:prompt-cache:v1") ‖ hash_part(scope) ‖ hash_part(session)))[0..60]`（共 64 字符）。
9. 插入：找到最后一个非空白字节位置 `closing`（即 `}`），在其前插入 `,"prompt_cache_key":"<key>"`，其余字节完全保留。state = `Added`，记录 scope。

### 5.2 `reject(request)`

scope 入 `VecDeque`（去重；满 128 则 `pop_front`）；`amended = None`；state = `Unsupported`。

### 5.3 兼容重发（`proxy.rs::send_cache_aware`）

循环：发送 `request.body()`（amended 优先）；若 `is_amended && status ∈ {400,422} && json_error && !encoded && (Content-Length 缺失或 ≤ 64 KiB)`——`json_error`：Content-Type 缺失或 `application/json`/`*+json`；`encoded`：Content-Encoding 非 identity——则 `probe_cache_error`：累积读块，超过 64 KiB → `Replay([prefix, chunk])` 原样转发；读错 → `Replay([prefix, Err])`；读完 → `rejects_cache_key(prefix)` 为 true → `Rejected`（`prompt_cache.reject`，`metrics.cache_fallback`，info `[{id}] 上游不接受代理补充的缓存标识，使用原请求兼容重发一次；当前通道对同一接口、模型及鉴权暂停补充`，continue 重发原请求——**不计普通重试次数**），否则 Replay。

`rejects_cache_key(body)`：JSON；`error.param == "prompt_cache_key"` 且 `error.code ∈ {unknown_parameter, unsupported_parameter, unrecognized_parameter}`；或 `error.message`（≤256）trim、去尾 `.`、小写后以 `unknown parameter: `/`unsupported parameter: `/`unrecognized request argument supplied: ` 开头且余下为 `prompt_cache_key`/`'prompt_cache_key'`/`"prompt_cache_key"`；或 `detail[]` 中有 `type == "extra_forbidden"` 且 `loc == ["body","prompt_cache_key"]`。

---

## 6. 日志（`logging.rs`）

- 常量：`MAX_LOG_BYTES` = 5 MiB（5,242,880）；`BACKUP_COUNT` = 3；`QUEUE_CAPACITY` = 10,000（UI mpsc 队列）。
- 文件：`{log_dir}/retry-proxy.log`，append 模式。`Logger::new(dir)` 返回 `(Logger, LogReceiver)`；`Logger::silent(dir)` 无 UI 队列。
- 行格式：`{YYYY-MM-DD HH:MM:SS} {LEVEL} {message}\n`，本地时间；LEVEL 为 `INFO` / `WARNING` / `ERROR`（`warn()` 写 `WARNING`）。
- `RouteLogger.prefix`：route_name 为空 → 原文；message 以 `[` 开头 → `[{route}]{message}`（即 `[通道][请求ID] …`）；否则 `[{route}] {message}`。
- 轮转：写入前若 `file.len + line.len > 5 MiB`：关闭当前文件（先用 `NUL` 占位），`rotate_files`：从 3 到 1 依次 `.log.{i-1}` → `.log.{i}`（`.log` → `.log.1`），目标存在先删除；然后 truncate 新建 `.log`。
- 写入后 `flush`；若有 UI 队列 `try_send(去尾换行的行)`，成功则 `notifier.notify()`，满/断开则丢弃。
- UI 内存缓冲（`ui.rs` `LogBuffer`）：`LOG_RETAIN_LINES` = 2000，超过时一次性丢弃最旧 `LOG_TRIM_LINES` = 500 行；`LOG_SCROLL_RESUME_DELAY` = 5 秒。

---

## 7. Metrics（`metrics.rs`、`metrics/daily.rs`、`metrics/legacy.rs`）

### 7.1 数据类型

- `CacheInputAccounting`（serde snake_case）：`includes_cached`（`/responses`、`/v1/responses`、`/chat/completions`、`/v1/chat/completions`）、`excludes_cached`（`/messages`、`/v1/messages`）；其他路径 None。`total_input_tokens`：IncludesCached → input；ExcludesCached → input+cached+created（三者必须齐全，checked_add）。
- `CacheRequest`（Serialize/Deserialize，字段名即 JSON 键）：`request_id: String`、`model: String`、`completed_at_unix_ms: i64`、`input_tokens: Option<u64>`、`cached_tokens: Option<u64>`、`cache_creation_tokens: Option<u64>`、`input_accounting`、`cache_key_status: String`。`usage()`：`(total_input, cached)` 需 `input>0 && cached<=input`。`usage_is_invalid()`：cached>input 或 Claude 三项齐全但相加溢出。
- `RequestPhase`（snake_case）：`waiting_response`("等待回复")、`waiting_generation`("等待生成")、`waiting_retry`("等待重试")、`receiving_response`("接收内容")。
- `ActiveRequest`：`request_id, method, path, phase, attempt: u64`。
- `CacheSnapshot`：`measured_requests, unmeasured_requests, input_tokens, cached_tokens, cache_creation_tokens, cache_creation_measured_requests, zero_hit_requests, zero_hit_input_tokens, client_key_requests, added_key_requests, missing_session_requests, unsupported_key_requests, compatibility_fallbacks: u64`，`recent_requests: VecDeque<CacheRequest>`（最多 `CACHE_HISTORY_LIMIT` = 20，旧→新）。`hit_rate_percent()` = input>0 ? 100*cached/input : null。`record()`：有 usage 则累加（任何 checked_add 溢出则整条丢弃），cached==0 计 zero_hit；无 usage 计 unmeasured；均进入 recent。
- `MetricsSnapshot`：`statistics_date: String`(YYYY-MM-DD)、`historical_unfinished_requests`、`restored_from_legacy_logs: bool`、`statistics_warning: Option<String>`、`total_requests`、`active_requests`、`successful_requests`、`retry_count`、`failed_requests`、`requests: Vec<ActiveRequest>`、`cache`、`gpt_cache`。

### 7.2 `/_retry/health` 响应（HTTP 200，`application/json; charset=utf-8`）

```json
{
  "status": "ok",
  "metrics": {
    "statistics_date": "2026-09-21",
    "historical_unfinished_requests": 0,
    "restored_from_legacy_logs": false,
    "statistics_warning": null,
    "total_requests": 0, "active_requests": 0, "successful_requests": 0,
    "retry_count": 0, "failed_requests": 0,
    "requests": [{"request_id":"…","method":"POST","path":"/v1/responses","phase":"waiting_retry","attempt":2}],
    "cache": { "measured_requests":0,"unmeasured_requests":0,"input_tokens":0,"cached_tokens":0,
               "cache_creation_tokens":0,"cache_creation_measured_requests":0,"zero_hit_requests":0,
               "zero_hit_input_tokens":0,"client_key_requests":0,"added_key_requests":0,
               "missing_session_requests":0,"unsupported_key_requests":0,"compatibility_fallbacks":0,
               "recent_requests":[{"request_id":"…","model":"…","completed_at_unix_ms":0,"input_tokens":null,
                  "cached_tokens":null,"cache_creation_tokens":null,"input_accounting":"includes_cached",
                  "cache_key_status":"保持原请求"}] },
    "cache_hit_rate_percent": null,
    "gpt_cache": { …同 CacheSnapshot… },
    "gpt_cache_hit_rate_percent": null
  }
}
```
`gpt_cache` 仅统计 `input_accounting == includes_cached && is_gpt_model(model)` 的请求。

### 7.3 `ProxyMetrics` 行为

所有以 `保活-` 开头的 request_id 在 `request_started` 与 `change` 中直接忽略。每次操作先 `rollover(now)`，再 notify UI。`request_started` 插入 active（phase WaitingResponse, attempt 1）+ `DailyChange::Started`；`request_attempt` 置 WaitingResponse + attempt；`request_phase`；`request_finished` 移除 active；`success(id, cache)`、`retry(id, attempt)`、`failure(id)`、`cache_key(id, state)`（state≠Unchanged）、`cache_fallback(id)`。

### 7.4 每日统计（`daily.rs`）

- 目录：`{log_dir}/daily-statistics/{hex(SHA256(route_id))}/{YYYY-MM-DD}.jsonl`（`StorageSpec`）。
- `LOG_VERSION` = 1。行格式（serde tag `record`）：
  - 头行：`{"record":"header","version":1,"date":"2026-09-21","route_id":"…","route_name":"…","legacy_imported":false}`
  - 记录行：`{"record":"request","value":{"request_id":"…","updated_at_unix_ms":0,"sequence":0,"outcome":"success"|"failure"|null,"retry_count":0,"last_retry_attempt":null,"cache_key":"added"|null,"cache_fallback":false,"cache":<CacheRequest|null>}}`
- `DailyRequest.retry(attempt)`：仅当 `attempt > last_retry_attempt` 才计数（幂等）。
- `MetricsState.change(id, change, now_ms)`：`Started` 仅确保存在；`Succeeded(cache)` 仅当 outcome 为 None（覆盖 cache 的 request_id 与 completed_at）；`Failed` 仅当 None；`Retry` 仅当 None；`CacheKey` 仅当尚未设置；`CacheFallback` 置 true。有变化（新建、outcome、retry_count、cache_key、cache_fallback）才更新 `updated_at`、`sequence++`、`account()`，加入 dirty，然后 `flush()`（追加整行，写失败则记录 `write_warning` "当日统计日志写入失败（{kind}），未保存数据将在下次更新时重试"，未写的留在 dirty 下次重试）。
- `account(before, after, new)`：new → total+1；retry_count 累加差值；outcome 从 None→Success → successful+1 并 `cache.record`（GPT 满足条件再 `gpt_cache.record`）；→Failure → failed+1；cache_key 首次设置 → 相应计数器+1（cache 与 gpt_cache 都加）；cache_fallback false→true → compatibility_fallbacks+1。
- `snapshot()`：`historical_unfinished = total - successful - failed - (active 中 outcome 为 None 的数量)`；`statistics_warning = write_warning ?? read_warning`。
- **恢复 `DailyJournal::open`**：目录不存在则创建；当日文件不存在：若目录中**没有任何 `.jsonl`**（首日）且 `import_legacy` → `legacy::restore`；用临时文件写头行 + 记录行，`persist_noclobber`（已存在则忽略）。然后以 read+append 打开，第一行必须是 header 且 `version==1 && date 匹配 && route_id 匹配`，否则 `InvalidData("统计日志头不匹配")` → read_warning `当日统计日志恢复失败（{kind}），当前仅显示本次运行数据`。逐行解析：request 行且 id 非空且非 `保活-` → 覆盖到 map（同 id 后行替换前行）；损坏行计数 `damaged`；末尾无换行的残行记 `truncate_at` 并截断文件；文件末尾缺换行则补 `\n`。damaged>0 → warning `统计日志中有 {n} 条未写完或损坏的记录，已恢复其余可读数据`。恢复的记录按 `(cache.completed_at_unix_ms, sequence)` 排序后逐条 `account`（保证 recent 20 按完成顺序）。`append` 失败时截断回写前偏移，防止拼接残缺 JSON。
- **午夜切换 `rollover(now)`**：日期不同 → flush；记录 dirty 未清空；找出 active 中 outcome 为 None 的 id（carried）；`load(新日期, import_legacy=false)`；沿用旧 active；若有未写入则 read_warning `上一日部分统计未能写入日志，请检查日志目录是否可写`；对 carried 每个 `change(Started)`（新一天 total 计入，阶段保留）。

### 7.5 旧日志恢复（`legacy.rs::restore(dir, route_name, date)`）

- 读取顺序：`retry-proxy.log.3`、`.2`、`.1`、`retry-proxy.log`（不存在跳过）；只接受**以 `\n` 结尾**的完整行；UTF-8；行以 `YYYY-MM-DD`（目标日期）开头；`[0..19]` 解析 `%Y-%m-%d %H:%M:%S`；`[20..]` 拆 `LEVEL body`，LEVEL ∈ {INFO, WARNING, ERROR}；body 以 `[{route_name}][` 开头，取到 `]` 为 id；id 长度 8–32 且全 hex（排除 `保活-`）。按时间戳稳定排序。
- 每条：`sequence = index+1`；`parse_call(body)`：可选前缀 `第 {a}/{b} 次 `，然后 `{METHOD} {path} -> {resp}`，METHOD ∈ 标准 9 种；`resp` 以 `上游 HTTP ` 开头则解析状态码。
- 含 `上游不接受代理补充的缓存标识` → cache_key=Added, cache_fallback=true；否则首次 `key_state(body)`（`，缓存标识：{label}` 反查）。
- 含 `改为完整流式转发` → forced_forward。
- 重试：含 `可重试，`+`秒后再次请求` 或 `将在 `+`秒后重试` → 用行内/最近尝试号 `retry(attempt)`；无尝试号则按 (id,时间,正文) 去重后 `retry_count+1`。
- 终态（仅在无 outcome 时）：含 `响应未完成`/`客户端在响应转发前断开`/`通道或后台任务已取消`/`请求总等待达到`/`重试耗尽`/`已达到重试上限` → Failure。否则需 INFO 级别且有状态码：200 → Success，并按 `CacheInputAccounting::for_path(path)` 构造 CacheRequest（`，模型 `、`，输入 N`、`，缓存命中 N`、`，缓存写入 N` 字段，`cache_key_status` 缺省 `旧日志未记录`）；非 {408,425,429,5xx} 或 attempt≥limit 或 forced_forward → Failure。

---

## 8. 系统代理（`system_proxy.rs`）

`SystemProxyResolver::resolve(target)` → `Option<ProxyDecision{proxy_url, from_system: true}>`：
1. `bypass_from_env`：`NO_PROXY`/`no_proxy` 列表匹配 → 直连。`bypass_pattern_list`：无 host → 直连；host 为 `localhost`、`::1`、`127.*`、`0.0.0.0` → **始终直连**；模式以 `,` 或 `;` 分隔：`*` 或 (`<local>` 且 host 无 `.`) → 直连；去 `http://`/`https://` 与路径，可带 `:port`（端口不符则不匹配），去 `*.` 前缀，host 相等或以 `.{pattern}` 结尾。
2. 环境变量（按 scheme）：`{scheme}_proxy`、`{SCHEME}_PROXY`、`all_proxy`、`ALL_PROXY`，非空即用（无 `://` 则补 `http://`）。
3. Windows 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`：`ProxyEnable`(DWORD)==0 → 直连；`ProxyOverride` 用同一 bypass 规则；`ProxyServer`：含 `=` 时取 `{scheme}=host:port` 项（scheme 不区分大小写），不含 `=` 则整体使用；补 `http://`。
4. 都没有 → 直连（None）。日志"链路：系统代理/直连"即据此。

---

## 9. 环境变量 `RETRY_GENERATION_TIMEOUT_SECONDS` / `RETRY_TOTAL_TIMEOUT_SECONDS`

见 §1.5。解析为 f64（trim；空忽略；非法报错 `环境变量 X 的值无效`）；写入 `RouteRuntimeOverrides.generation_timeout_seconds` / `total_timeout_seconds`，`route_id = selected_route_id`（无路由时 ""）。`normalize()` 时仅在 `overrides.route_id == 选中路由 id` 时覆盖顶层镜像；`runtime_config_for(route_id)` 仅对该路由生效；`canonical_value` 保存时不写回（保存 route 原值）。覆盖值仍受 `validate` 范围检查（0 < x ≤ 86400）。其他通道不受影响。

---

## 10. 集成测试清单（`rust/tests/`）

### `proxy_integration.rs`（`ProxyService` 级）
- `manual_preparation_wakes_the_service_immediately_and_stopping_cancels_it`：一键准备立即触发保活探测，停止通道取消它。
- `service_forwards_and_retries`：服务转发请求并在可重试状态码上重试后成功。
- `streaming_keeps_request_active_until_body_finishes`：流式响应在正文读完前 active_requests 保持 1。
- `unavailable_upstream_returns_502_after_retry`：上游不可达，重试耗尽返回 502 `upstream_unavailable`。
- `stopping_discards_in_flight_requests`：停用通道硬停，丢弃处理中请求并记录"丢弃 N 个处理中的请求"。
- `stopping_cuts_off_a_downstream_client_that_stopped_reading`：停用时切断停止读取的下游客户端。
- `provider_keepalive_uses_its_own_conversation_and_survives_stopping_the_source_channel`：跨通道准备使用独立会话，源通道停止后仍继续。

### `request_lifecycle.rs`（`RetryProxy` 直接 serve）
- `generation_timeout_retries_despite_heartbeats_without_leaking_the_old_attempt`：仅心跳时等待生成超时后重试，旧尝试暂存不泄漏。
- `generation_retries_empty_eof_and_network_failure_before_content`：生成前 EOF/网络错误可重试。
- `generation_waiting_preserves_fragmented_prefix_and_shows_the_actual_phase`：分片前缀原样保留，阶段显示"等待生成"。
- `generation_exhaustion_returns_failure_instead_of_an_empty_success`：等待生成耗尽后返回失败（502）而非空成功。
- `generation_wait_obeys_the_total_deadline_without_replaying`：等待生成受总等待上限约束，返回 504 不重放。
- `generation_wait_is_cancelled_when_the_client_leaves`：客户端断开时取消等待生成。
- `generation_never_replays_tool_calls_reasoning_or_unknown_events`：工具调用/推理/未知事件立即放行，不重发。
- `generation_passes_empty_completion_and_error_events_without_retrying`：空完成事件和错误事件透传不重试。
- `generation_prefix_limit_preserves_all_bytes_and_the_retry_boundary`：1 MiB 前缀上限内外的字节都完整转发，超限关闭重试。
- `request_phases_follow_attempts_and_body_delivery_without_countdowns`：阶段与尝试次数随流程变化，无倒计时。
- `response_timing_includes_request_body_upload`：耗时从接收请求体开始。
- `oversized_chunked_errors_forward_the_prefix_and_tail_without_retrying` / `oversized_known_length_errors_bypass_buffering_without_truncation`：超 1 MiB 错误正文完整转发不重试。
- `errors_at_the_buffer_limit_still_retry_normally`：恰好 1 MiB 仍可重试。
- `successful_responses_are_not_limited_by_the_retry_buffer_limit`：成功响应不受暂存上限限制。
- `oversized_errors_release_request_state_on_deadline_and_client_disconnect`：超大错误在 deadline/断开时释放状态。
- `total_deadline_covers_an_unfinished_request_body`：请求体未传完也受总等待上限。
- `disconnect_during_request_body_records_one_failure`：请求体期间断开记 1 次失败。
- `disconnect_before_response_headers_stops_the_request`：响应头前断开停止请求。
- `disconnect_during_retry_wait_prevents_the_next_attempt`：重试等待中断开不再发起下次尝试。
- `new_requests_and_direct_provider_traffic_do_not_wait_for_old_requests`：新请求不排队等待旧请求。
- `total_deadline_stops_retry_after_wait_and_returns_504`：Retry-After 等待被总上限截断返回 504。
- `total_deadline_covers_headers_first_chunk_and_request_upload`：总上限覆盖响应头、首块和上传。
- `extreme_retry_after_is_bounded_without_duration_overflow`：极大 Retry-After 不溢出。
- `total_deadline_is_shared_by_all_attempts`：所有尝试共享同一 deadline。
- `streaming_deadline_ends_partial_answers_without_replaying_them`：转发中到期以中断结束不重放。
- `total_deadline_releases_a_client_that_stops_reading`：客户端停读时到期释放。
- `exhausted_retries_forward_the_entire_last_error_response`：耗尽时完整返回最后一次错误响应。
- `terminal_events_finish_without_waiting_for_upstream_disconnect`：终止事件后立即收尾。
- `successful_http_without_a_terminal_event_aborts_the_response`：200 但无终止事件以中断结束。
- `claude_partial_tool_response_is_delivered_and_requires_message_stop`：Claude 工具响应放行，需 `message_stop` 才算完成。

### `request_logging.rs`
- `non_streaming_completion_logs_usage_without_changing_traffic`：非流式完成日志含用量且不改流量。
- `preparation_passes_sites_that_require_the_original_client_format` / `preparation_reports_the_site_error_without_echoing_credentials`：一键准备兼容原客户端格式、错误不泄漏凭据。
- `streaming_completion_finishes_before_transport_eof`：流式完成在 EOF 前收尾。
- `active_requests_suppress_probes_and_disconnects_do_not_replace_templates`：有活动请求时不发探测；断开不替换模板。
- `keepalive_consumes_the_complete_body_and_logs_usage_without_counting_as_user_traffic`：保活不计用户统计。
- `a_broken_probe_body_is_not_logged_as_success_after_http_200`：200 但正文损坏不算成功。
- `streaming_timeout_resets_while_content_continues_to_arrive`：持续有内容时读超时不触发。
- `protocol_errors_and_premature_eof_remain_failures`：协议错误与提前 EOF 记失败。
- `structured_failure_diagnostics_preserve_response_bytes_and_hide_messages`：失败诊断保留字节、隐藏消息原文。
- `rate_limit_after_http_200_is_explained_without_retrying_or_changing_the_body`：200 后流内限流解释为"上游请求超限"不重试。
- `interrupted_streams_keep_progress_and_request_ids_without_claiming_rate_limits`：中断保留进度与上游请求 ID，不误判限流。
- `response_timeouts_log_the_retry_decision_without_leaking_the_request_url`：超时日志含重试决定，不泄漏 URL。
- `model_list_requests_do_not_replace_the_session_keepalive_template`：GET /models 不替换模板。
- `shared_provider_probes_keep_the_source_destination_and_yield_to_another_channels_real_request`：共享服务商探测让行真实请求。
- `different_providers_can_run_background_conversations_concurrently`：不同服务商后台会话并行。
- `preparation_waits_for_the_complete_response_in_every_supported_protocol`：准备等待各协议完整回复。
- `preparation_logs_cli_launch_failure_and_stays_pending_until_cancelled`：CLI 启动失败继续重试直至取消。
- `background_session_rolls_over_using_latest_context_usage_and_logs_the_reset`：会话超阈值重建并记录。

### `prompt_cache.rs`
- `channel_cache_counts_claude_and_other_models_without_changing_replies`：Claude/其他模型缓存统计不改回复。
- `claude_cache_distinguishes_creation_only_missing_usage_and_failed_replies`：仅写入/缺用量/失败分别处理。
- `stable_key_preserves_payload_and_is_visible_in_logs_and_weighted_metrics`：稳定 key、正文保留、日志与加权统计可见。
- `explicit_rejection_resends_original_once_and_remembers_compatibility`：明确拒绝时兼容重发一次并记忆。
- `supplied_keys_and_unrelated_validation_errors_never_trigger_compatibility_retries`：客户端 key/无关错误不触发重发。
- `oversized_chunked_error_is_replayed_without_truncation_or_resending`：超 64 KiB 错误原样转发。
- `non_json_error_starts_forwarding_before_the_upstream_closes`：非 JSON 错误立即转发。
- `error_inspection_respects_the_total_deadline`：错误检查受总上限约束。
- `stream_bytes_are_preserved_and_usage_is_counted_once`：流字节保留，用量只计一次。
- `missing_usage_and_failed_streams_do_not_count_as_zero_cache_hits`：缺用量/失败不计零命中。

---

## 附：移植时需特别注意的隐含行为

1. `metrics.success` 只在 **HTTP 200**（不是 2xx）且流 outcome 为 Complete 时触发；201/204 等成功码计为 failure。
2. 内部保活请求使用全新 `ProxyMetrics`，且 `PromptCache`、`DailyJournal` 都跳过 `保活-` 前缀 id。
3. `GenerationGate` 只对 2xx 且 `text/event-stream` 的 API 路径生效；JSON/压缩/未知格式直接转发。
4. `copy_request_headers` **保留 `authorization`** 等鉴权头、删除 `host`/`content-length`（由 HTTP 客户端重新生成）。
5. `Retry-After` 只在 429/503 的 **HTTP 状态重试**路径使用；网络错误重试始终用指数退避。
6. `wrap_request_lifecycle` 的"交付完成"必须等下游 HTTP 层把容量 1 的队列读空，才结清 active 计数与保活计数。
7. 日志级别字符串是 `WARNING`（非 `WARN`），legacy 恢复解析依赖此值。
8. 每日 jsonl 头行的 `route_id` 与目录 SHA-256 一致；改名不改 id 则历史保留。
