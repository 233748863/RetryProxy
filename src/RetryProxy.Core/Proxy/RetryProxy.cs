using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Service;
using RetryProxy.Core.Stats;

namespace RetryProxy.Core.Proxy;

/// <summary>
/// 请求转发与重试管线（对应 proxy.rs 的 RetryProxy）。一个实例服务一条通道。
/// </summary>
public sealed class RetryProxy
{
    public const int MaxRequestBodyBytes = 100 * 1024 * 1024;
    public const int MaxRetryResponseBodyBytes = 1024 * 1024;
    public const int MaxGenerationPrefixBytes = 1024 * 1024;

    /// <summary>保活补发的请求带上这个头，方便在日志里认出来，也避免它被当成新模板。</summary>
    public const string KeepAliveMarkerHeader = HeaderRules.KeepAliveMarkerHeader;

    private const double RetryJitterSeconds = 0.5;
    private static readonly int[] RetryableStatusCodes = { 408, 425, 429 };
    private static readonly string[] HttpDateFormats =
    {
        "r",
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM  d HH:mm:ss yyyy",
    };

    private readonly HttpClient _client;
    private readonly PromptCache _promptCache;
    private string? _upstreamApiKey;
    private ClaudeAuthMode _upstreamAuthMode;
    private string? _localAccessKey;
    private Func<double> _randomValue = () => Random.Shared.NextDouble();

    public RetryProxy(ProxyConfig config, ProxyLogger logger, ProxyMetrics metrics, CancellationToken cancel, IProxyResolver? proxyResolver = null)
    {
        config.Validate(true);
        Config = config;
        Logger = logger.Route(string.Empty);
        Metrics = metrics;
        Cancel = cancel;
        ProxyResolver = proxyResolver ?? SystemProxyResolver.Shared;
        var timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        try
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = timeout,
                UseProxy = true,
                Proxy = new ResolverWebProxy(ProxyResolver),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            };
            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }
        catch (Exception error)
        {
            throw new ConfigException($"无法初始化上游 HTTP 客户端：{error.Message}");
        }

        KeepAlive = new KeepAliveWatchdog(config.KeepaliveEnabled, TimeSpan.FromSeconds(config.KeepaliveIdleMinutes * 60.0));
        KeepAlive.SetContextLimit((ulong)Math.Max(config.KeepaliveContextLimit, 1));
        _promptCache = new PromptCache(config.UpstreamBaseUrl);
    }

    public ProxyConfig Config { get; }

    public RouteLogger Logger { get; private set; }

    public ProxyMetrics Metrics { get; }

    public CancellationToken Cancel { get; }

    public IProxyResolver ProxyResolver { get; }

    /// <summary>空闲保活状态。每次请求收尾时更新，看门狗按它决定要不要补发。</summary>
    public KeepAliveWatchdog KeepAlive { get; private set; }

    public RetryProxy WithRandomValue(Func<double> randomValue)
    {
        _randomValue = randomValue;
        return this;
    }

    public RetryProxy WithRouteLogger(RouteLogger logger)
    {
        Logger = logger;
        return this;
    }

    public RetryProxy WithKeepAliveWatchdog(KeepAliveWatchdog watchdog)
    {
        KeepAlive = watchdog;
        return this;
    }

    public RetryProxy WithUpstreamApiKey(string? apiKey, string? localAccessKey, ClaudeAuthMode authMode = ClaudeAuthMode.Bearer)
    {
        _upstreamApiKey = apiKey;
        _localAccessKey = localAccessKey;
        _upstreamAuthMode = authMode;
        return this;
    }

    /// <summary>到点就发一轮保活探测（对应 send_due_keepalive_probe）。</summary>
    public async Task SendDueKeepAliveProbeAsync()
    {
        var probe = KeepAlive.BeginDueProbe();
        if (probe is not null)
        {
            await SendProbeAsync(probe).ConfigureAwait(false);
        }
    }

    /// <summary>按模板立刻发一轮（测试与验收用；对应 send_keepalive_probe）。</summary>
    public async Task SendKeepAliveProbeAsync(KeepAliveTemplate template)
    {
        var probe = KeepAlive.BeginProbe(template);
        if (probe is not null)
        {
            await SendProbeAsync(probe).ConfigureAwait(false);
        }
    }

    private async Task SendProbeAsync(KeepAliveProbe probe)
    {
        using var _ = probe;
        var startedAt = MonotonicInstant.Now;
        var timeoutSeconds = probe.IsPreparation && _localAccessKey is not null
            ? Config.TotalTimeoutSeconds + 30
            : Math.Min(Config.TimeoutSeconds, Config.TotalTimeoutSeconds);
        var sessionLabel = probe.SessionId.Length > 8 ? probe.SessionId[..8] : probe.SessionId;
        var configuration = _localAccessKey is not null ? "经后台临时代理转发" : probe.UsesSuppliedKey ? "使用本次输入的 Key 经本通道转发" : "沿用本机客户端配置";
        var preparing = probe.IsPreparation;
        var logger = Logger.WithActivity(preparing ? LogActivity.Preparation : LogActivity.KeepAlive);
        logger.Info($"[会话 {sessionLabel}] 第 {probe.Turn} 轮，随机题号 {probe.QuestionIndex + 1}/250，{probe.Flavor.Label()} CLI，{configuration}，问题：{probe.Question}");

        Cli.CliReply? reply = null;
        string? failure = null;
        string? interruption = null;
        using (var timeout = new CancellationTokenSource())
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(Cancel, probe.Cancel, timeout.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                reply = await probe.ExecuteAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Cli.CliException error)
            {
                failure = error.Message;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                failure = $"CLI 调用异常：{error.GetType().Name}";
            }

            // 与 Rust 的 biased select 一致：先看通道取消，再看会话让行，最后才是超时。
            if (Cancel.IsCancellationRequested)
            {
                interruption = "通道或应用已停止";
            }
            else if (probe.Cancel.IsCancellationRequested)
            {
                interruption = "已让行真实请求或保活设置发生变化，本轮会话已清理";
            }
            else if (reply is null && failure is null)
            {
                failure = $"CLI 本轮执行超过 {StreamLifecycle.Format(timeoutSeconds)} 秒，已终止并清理会话";
            }
        }

        var elapsed = startedAt.Elapsed.TotalSeconds;
        var nextRound = KeepAlive.Enabled ? $"空闲 {(long)KeepAlive.Idle.TotalSeconds} 秒后进行下一轮" : "自动保活已关闭";
        var afterFailure = KeepAlive.Snapshot().Preparing && !Cancel.IsCancellationRequested
            ? $"准备未完成，随机等待 {KeepAliveWatchdog.PreparationRetryMinDelay.TotalSeconds:F3}～{KeepAliveWatchdog.PreparationRetryMaxDelay.TotalSeconds:F3} 秒后继续重试；可点击“终止准备”取消"
            : nextRound;
        var prefix = $"[会话 {sessionLabel}] 第 {probe.Turn} 轮 {probe.Flavor.Label()} CLI";
        if (interruption is not null)
        {
            probe.Interrupt(interruption);
            logger.Info($"{prefix}，本轮已中断：{interruption}，耗时 {elapsed:F2} 秒，{afterFailure}");
            return;
        }

        if (failure is not null)
        {
            probe.Fail(failure);
            logger.Warn($"{prefix}，响应未完成：{failure}，耗时 {elapsed:F2} 秒，{afterFailure}");
            return;
        }

        var answerPreview = new System.Text.StringBuilder();
        var taken = 0;
        foreach (var rune in (reply!.Stats.Answer() ?? string.Empty).EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(rune))
            {
                continue;
            }

            if (taken++ == 180)
            {
                break;
            }

            answerPreview.Append(rune.ToString());
        }

        var completion = probe.Complete(reply.Model, reply.Stats.ContextTokens(probe.Flavor == KeepAliveFlavor.Claude));
        if (completion is null)
        {
            const string reason = "完整回复确认前本轮已取消，未计为成功";
            probe.Interrupt(reason);
            logger.Info($"{prefix}，本轮已中断：{reason}，耗时 {elapsed:F2} 秒，{afterFailure}");
            return;
        }

        var context = completion.ContextTokens?.ToString(CultureInfo.InvariantCulture) ?? "未获取";
        var reset = completion.ResetReason is { } resetReason ? $"，{resetReason}" : string.Empty;
        if (preparing && KeepAlive.Enabled)
        {
            nextRound = $"首次准备成功，已转为自动保活；空闲 {(long)KeepAlive.Idle.TotalSeconds} 秒后进行下一轮";
        }
        logger.Info($"{prefix} 完整回复{reply.Stats.LogFields()}，当前会话 {context}/{completion.ContextLimit} token，{LogText.TimingText(reply.FirstContentSeconds, elapsed)}，回答：{answerPreview}{reset}，{nextRound}");
    }

    // ---------------------------------------------------------------------
    // 请求入口
    // ---------------------------------------------------------------------

    private sealed class RequestContext
    {
        private Task? _cancelled;

        public RequestContext(ProxyMetrics metrics, RouteLogger logger, CancellationToken cancel, CancellationToken token, string requestId, string method, string safePath, Deadline deadline, MonotonicInstant startedAt)
        {
            Metrics = metrics;
            Logger = logger;
            Cancel = cancel;
            Token = token;
            RequestId = requestId;
            Method = method;
            SafePath = safePath;
            Deadline = deadline;
            StartedAt = startedAt;
        }

        /// <summary>本次请求的统计对象；内部请求换成一次性的空对象。</summary>
        public ProxyMetrics Metrics { get; }

        public RouteLogger Logger { get; }

        /// <summary>通道取消或内部会话取消。</summary>
        public CancellationToken Cancel { get; }

        /// <summary>通道取消 + 内部取消 + 总等待到期 + 客户端断开。</summary>
        public CancellationToken Token { get; }

        public string RequestId { get; }

        public string Method { get; }

        public string SafePath { get; }

        public Deadline Deadline { get; }

        public MonotonicInstant StartedAt { get; }

        public Task Cancelled => _cancelled ??= Task.Delay(Timeout.Infinite, Token);
    }

    /// <summary>健康检查：返回当前统计快照。</summary>
    public async Task HealthAsync(HttpContext context)
    {
        var snapshot = Metrics.Snapshot();
        var payload = new HealthPayload
        {
            Metrics = new HealthMetrics
            {
                StatisticsDate = snapshot.StatisticsDate,
                HistoricalUnfinishedRequests = snapshot.HistoricalUnfinishedRequests,
                RestoredFromLegacyLogs = snapshot.RestoredFromLegacyLogs,
                StatisticsWarning = snapshot.StatisticsWarning,
                TotalRequests = snapshot.TotalRequests,
                ActiveRequests = snapshot.ActiveRequests,
                SuccessfulRequests = snapshot.SuccessfulRequests,
                RetryCount = snapshot.RetryCount,
                FailedRequests = snapshot.FailedRequests,
                Requests = snapshot.Requests,
                Cache = snapshot.Cache,
                CacheHitRatePercent = snapshot.Cache.HitRatePercent(),
                GptCache = snapshot.GptCache,
                GptCacheHitRatePercent = snapshot.GptCache.HitRatePercent(),
            },
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonText.Compact);
        await WriteSimpleResponseAsync(context, 200, "application/json; charset=utf-8", body).ConfigureAwait(false);
    }

    /// <summary>转发入口（对应 proxy_handler + handle_request）。</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (_localAccessKey is { } localAccessKey)
        {
            if (HttpMethods.IsHead(context.Request.Method) && context.Request.Path == "/api/hello")
            {
                await WriteSimpleResponseAsync(context, 200, "application/json; charset=utf-8", Array.Empty<byte>()).ConfigureAwait(false);
                return;
            }

            var authorization = context.Request.Headers.Authorization.ToString();
            var apiKey = context.Request.Headers["x-api-key"].ToString();
            if (!AccessKeyMatches(authorization, $"Bearer {localAccessKey}") && !AccessKeyMatches(apiKey, localAccessKey))
            {
                await WriteSimpleResponseAsync(context, 401, "application/json; charset=utf-8",
                    JsonBody.Error("unauthorized", "后台临时代理拒绝未授权请求")).ConfigureAwait(false);
                return;
            }
        }

        var deadline = Deadline.AfterSeconds(Config.TotalTimeoutSeconds);
        var startedAt = MonotonicInstant.Now;
        // 当日统计日志跨进程存活，保留完整 UUID，重启后不同请求不会被旧的 32 位显示 ID 合并。
        var requestId = Guid.NewGuid().ToString("N");
        var method = context.Request.Method;
        var (safePath, rawQuery) = RawTarget(context);
        var requestHeaders = ToHeaderList(context.Request.Headers);
        var internalCancel = InternalSessions.RequestCancel(requestHeaders);
        // HEAD 不可能是用户对话（Claude Code 会先发不带自定义头的 HEAD /api/hello 探测连通性）；
        // 把它算作真实请求会误伤正经本通道转发的后台准备。
        var countsAsRealRequest = internalCancel is null && !HttpMethods.IsHead(method);
        var metrics = Metrics;
        var cancel = Cancel;
        if (internalCancel is { } sessionCancel)
        {
            metrics = new ProxyMetrics();
            cancel = sessionCancel;
            requestId = $"{ProxyMetrics.KeepAlivePrefix}{requestId}";
        }

        var preparingAtStart = KeepAlive.Snapshot().Preparing;
        var requestLogger = Logger.ForRequest(requestId, internalCancel is not null, preparingAtStart);
        var guard = new RequestFinishGuard(
            Metrics,
            countsAsRealRequest ? KeepAlive : null,
            KeepAliveFlavorExtensions.Detect(safePath),
            requestLogger,
            requestId,
            method,
            safePath,
            startedAt);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(Cancel, cancel, context.RequestAborted);
        CancelAt(requestCts, deadline);
        CancellationTokenSource? sessionCts = null;
        ProxyResponse? response = null;
        var error = ProxyErrorKind.None;
        string? bodyError = null;
        RequestContext? ctx = null;
        try
        {
            var body = await ReadRequestBodyAsync(context, requestCts.Token).ConfigureAwait(false);
            var token = requestCts.Token;
            if (guard.KeepAlive is not null && InternalSessions.BodyRequestCancel(body) is { } bodyCancel)
            {
                metrics = new ProxyMetrics();
                cancel = bodyCancel;
                requestId = $"{ProxyMetrics.KeepAlivePrefix}{requestId}";
                requestLogger = Logger.ForRequest(requestId, internalRequest: true, preparingAtStart);
                guard.KeepAlive = null;
                sessionCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token, bodyCancel);
                token = sessionCts.Token;
            }

            guard.Start();
            if (guard.KeepAlive is null)
            {
                body = HeaderRules.StripInternalRequestMetadata(body);
            }

            ctx = new RequestContext(metrics, requestLogger, cancel, token, requestId, method, safePath, deadline, startedAt);
            response = await HandleRequestInnerAsync(ctx, requestHeaders, rawQuery, body).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 与 Rust 的 biased select 同序：通道取消 > 内部取消 > 总等待到期 > 客户端断开。
            // 定时器可能比单调时钟早不到 1 毫秒触发，所以不能把“既没取消也没断开”当成取消。
            error = Cancel.IsCancellationRequested || cancel.IsCancellationRequested
                ? ProxyErrorKind.Cancelled
                : deadline.HasPassed || !context.RequestAborted.IsCancellationRequested
                    ? ProxyErrorKind.DeadlineExceeded
                    : ProxyErrorKind.Dropped;
        }
        catch (ProxyBodyException failure)
        {
            error = ProxyErrorKind.Body;
            bodyError = failure.Message;
        }
        finally
        {
            sessionCts?.Dispose();
        }

        guard.Start();
        if (error != ProxyErrorKind.Dropped)
        {
            guard.AwaitingResponse = false;
        }

        switch (error)
        {
            case ProxyErrorKind.Cancelled:
                metrics.Failure(requestId);
                requestLogger.Info($"[{requestId}] 通道或后台任务已取消，已停止当前请求，不再重试，耗时 {startedAt.ElapsedSeconds:F2} 秒");
                break;
            case ProxyErrorKind.DeadlineExceeded:
                metrics.Failure(requestId);
                requestLogger.Warn($"[{requestId}] {method} {safePath} -> 请求总等待达到 {StreamLifecycle.Format(Config.TotalTimeoutSeconds)} 秒，已取消当前请求，不再重试，向客户端返回 HTTP 504，耗时 {startedAt.ElapsedSeconds:F2} 秒");
                break;
            case ProxyErrorKind.Body:
                metrics.Failure(requestId);
                break;
        }

        if (response is null)
        {
            guard.Dispose();
            switch (error)
            {
                case ProxyErrorKind.Cancelled:
                    await WriteSimpleResponseAsync(context, 499, null, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                    break;
                case ProxyErrorKind.DeadlineExceeded:
                    await WriteSimpleResponseAsync(context, 504, "application/json; charset=utf-8", JsonBody.Error("proxy_timeout", "请求超过总等待上限，已停止重试")).ConfigureAwait(false);
                    break;
                case ProxyErrorKind.Body:
                    await WriteSimpleResponseAsync(context, 400, "application/json; charset=utf-8", JsonBody.Error("invalid_request", bodyError ?? string.Empty)).ConfigureAwait(false);
                    break;
            }

            return;
        }

        // 对应 wrap_request_lifecycle：正文交付完（或被取消 / 到期 / 客户端断开）才释放请求登记。
        try
        {
            if (guard.KeepAlive is not null)
            {
                guard.Metrics.RequestPhase(guard.RequestId, RequestPhase.ReceivingResponse);
            }

            await DeliverAsync(context, response, requestCts.Token).ConfigureAwait(false);
        }
        finally
        {
            guard.Dispose();
        }
    }

    private static bool AccessKeyMatches(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static async Task DeliverAsync(HttpContext context, ProxyResponse response, CancellationToken token)
    {
        var aborted = false;
        try
        {
            context.Response.StatusCode = response.Status;
            foreach (var (name, value) in response.Headers)
            {
                try
                {
                    context.Response.Headers.Append(name, value);
                }
                catch (Exception)
                {
                    // 无法表示的头直接丢弃，与 axum 转换失败时的行为一致。
                }
            }

            if (response.ContentLength is { } length)
            {
                context.Response.ContentLength = length;
            }

            await context.Response.StartAsync(token).ConfigureAwait(false);
            await foreach (var chunk in response.Body(token).WithCancellation(CancellationToken.None).ConfigureAwait(false))
            {
                if (chunk.IsEmpty)
                {
                    continue;
                }

                await context.Response.Body.WriteAsync(chunk, token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            aborted = true;
        }

        if (aborted)
        {
            context.Abort();
        }
    }

    private static async Task WriteSimpleResponseAsync(HttpContext context, int status, string? contentType, ReadOnlyMemory<byte> body)
    {
        try
        {
            context.Response.StatusCode = status;
            if (contentType is not null)
            {
                context.Response.ContentType = contentType;
            }

            context.Response.ContentLength = body.Length;
            if (!body.IsEmpty)
            {
                await context.Response.Body.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
            }

            await context.Response.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            context.Abort();
        }
    }

    private static (string Path, string Query) RawTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw) && raw[0] == '/')
        {
            var question = raw.IndexOf('?');
            return question < 0 ? (raw, string.Empty) : (raw[..question], raw[(question + 1)..]);
        }

        var path = context.Request.Path.ToUriComponent();
        var query = context.Request.QueryString.Value ?? string.Empty;
        return (path.Length == 0 ? "/" : path, query.TrimStart('?'));
    }

    private static HeaderList ToHeaderList(IHeaderDictionary headers)
    {
        var list = new HeaderList();
        foreach (var (name, values) in headers)
        {
            foreach (var value in values)
            {
                if (value is not null)
                {
                    list.Append(name, value);
                }
            }
        }

        return list;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadRequestBodyAsync(HttpContext context, CancellationToken token)
    {
        try
        {
            var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, token).ConfigureAwait(false);
            return buffer.TryGetBuffer(out var segment) ? new ReadOnlyMemory<byte>(segment.Array, segment.Offset, segment.Count) : buffer.ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ProxyBodyException(error.Message);
        }
    }

    private static void CancelAt(CancellationTokenSource source, Deadline deadline)
    {
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            source.Cancel();
        }
        else if (remaining < TimeSpan.FromDays(1))
        {
            // CancelAfter 只有毫秒精度：向上取整再加 1 毫秒，保证触发时截止时刻一定已过。
            source.CancelAfter(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds) + 1));
        }
    }

    // ---------------------------------------------------------------------
    // 转发主循环
    // ---------------------------------------------------------------------

    private sealed class UpstreamResponse
    {
        public UpstreamResponse(int status, HeaderList headers, long? expectedBodyBytes, IChunkSource source)
        {
            Status = status;
            Headers = headers;
            ExpectedBodyBytes = expectedBodyBytes;
            Source = source;
        }

        public int Status { get; }

        public HeaderList Headers { get; }

        public long? ExpectedBodyBytes { get; }

        public IChunkSource Source { get; set; }
    }

    private async Task<ProxyResponse> HandleRequestInnerAsync(RequestContext ctx, HeaderList requestHeaders, string rawQuery, ReadOnlyMemory<byte> body)
    {
        var requestId = ctx.RequestId;
        var method = ctx.Method;
        var safePath = ctx.SafePath;
        var headers = HeaderRules.CopyRequestHeaders(requestHeaders);
        if (_upstreamApiKey is { } apiKey)
        {
            headers.Remove("authorization");
            headers.Remove("x-api-key");
            headers.Remove("api-key");
            if (Config.ClientType == ClientType.Claude && _upstreamAuthMode == ClaudeAuthMode.ApiKey)
            {
                headers.Set("x-api-key", apiKey);
            }
            else
            {
                headers.Set("authorization", $"Bearer {apiKey}");
            }
        }
        var metadata = RequestMetadata.Parse(body);
        var pathAndQuery = rawQuery.Length > 0 ? $"{safePath}?{rawQuery}" : safePath;
        var keepAliveTemplate = HttpMethods.IsPost(method) ? new KeepAliveTemplate(method, pathAndQuery, headers, body) : null;
        var accept = headers.Get("accept");
        var streaming = metadata.Stream || (accept is not null && accept.ToLowerInvariant().Contains("text/event-stream", StringComparison.Ordinal));
        var model = metadata.Model;
        var cacheRequest = _promptCache.Prepare(method, safePath, headers, body, requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal));
        ctx.Metrics.CacheKey(requestId, cacheRequest.State);
        if (streaming || model is not null)
        {
            headers.Set("accept-encoding", "identity");
        }
        var alternateClaudeHeaders = _upstreamApiKey is not null && Config.ClientType == ClientType.Claude
            ? ClaudeAuthenticationFallback(headers)
            : null;
        var usedAlternateClaudeAuthentication = false;

        var targetUrl = BuildTargetUrl(Config.UpstreamBaseUrl, safePath, rawQuery);
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedTarget))
        {
            throw new ProxyBodyException("目标 URL 无效");
        }

        var usingSystemProxy = ProxyResolver.Resolve(parsedTarget) is not null;
        var requireValidContext = _localAccessKey is not null && KeepAlive.Snapshot().Preparing
            && HttpMethods.IsPost(method) && KeepAliveFlavorExtensions.Detect(safePath) != KeepAliveFlavor.Unknown;
        var maxRetries = (ulong)Math.Max(Config.MaxRetries, 0);
        if (_localAccessKey is not null && !requireValidContext)
        {
            maxRetries = 0;
        }
        var totalAttempts = requireValidContext ? 0UL : Saturating.Add(maxRetries, 1);
        BufferedResponse? lastResponse = null;

        for (ulong attempt = 0; totalAttempts == 0 || attempt < totalAttempts; attempt++)
        {
            var attemptNumber = attempt + 1;
            ctx.Metrics.RequestAttempt(requestId, attemptNumber);
            var startedAt = MonotonicInstant.Now;
            UpstreamResponse upstream;
            try
            {
                upstream = await SendCacheAwareAsync(ctx, method, targetUrl, headers, cacheRequest, streaming).ConfigureAwait(false);
            }
            catch (UpstreamException failure)
            {
                if (await HandleAttemptFailureAsync(ctx, attemptNumber, totalAttempts, null, null, startedAt, usingSystemProxy, failure).ConfigureAwait(false))
                {
                    return RetryExhaustedResponse(ctx, totalAttempts, lastResponse);
                }

                continue;
            }

            var status = upstream.Status;
            var responseHeaders = upstream.Headers;
            var expectedBodyBytes = upstream.ExpectedBodyBytes;
            var reader = new ChunkReader(upstream.Source);
            try
            {
                if (status is 401 or 403 && alternateClaudeHeaders is not null && !usedAlternateClaudeAuthentication)
                {
                    reader.Dispose();
                    headers = alternateClaudeHeaders;
                    usedAlternateClaudeAuthentication = true;
                    // 认证方式切换不受配置的重试次数限制；即使 max_retries=0，也必须给另一种格式一次机会。
                    if (totalAttempts is > 0 and < ulong.MaxValue)
                    {
                        totalAttempts++;
                    }

                    ctx.Logger.Info($"[{requestId}] 上游 HTTP {status} 拒绝当前 Claude 鉴权，改用另一种认证格式重试一次");
                    if (keepAliveTemplate is not null)
                    {
                        keepAliveTemplate = new KeepAliveTemplate(method, pathAndQuery, headers, body);
                    }

                    continue;
                }

                if (requireValidContext)
                {
                    RetryResponseBody buffered;
                    try
                    {
                        buffered = expectedBodyBytes > MaxRetryResponseBodyBytes
                            ? RetryResponseBody.Overflow(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty)
                            : await BufferUpstreamResponseAsync(reader, requestId, startedAt, ctx.Metrics, ctx.Token).ConfigureAwait(false);
                    }
                    catch (UpstreamException failure)
                    {
                        reader.Dispose();
                        if (await HandleAttemptFailureAsync(ctx, attemptNumber, totalAttempts, status, null, startedAt, usingSystemProxy, failure).ConfigureAwait(false))
                        {
                            return RetryExhaustedResponse(ctx, totalAttempts, lastResponse);
                        }

                        continue;
                    }

                    var stats = new ResponseStats(responseHeaders, safePath, model).WithAnswerCapture();
                    if (!buffered.TooLarge)
                    {
                        stats.Observe(buffered.Body.Span, ctx.StartedAt.ElapsedSeconds);
                        stats.Finish(ctx.StartedAt.ElapsedSeconds);
                    }

                    if (!buffered.TooLarge && status is >= 200 and < 300
                        && stats.Outcome is { IsFailed: false }
                        && stats.ContextTokens(Config.ClientType == ClientType.Claude) is > 0
                        && stats.Answer() is not null)
                    {
                        reader = new ChunkReader(new ReplayChunkSource(
                            new (ReadOnlyMemory<byte>?, Exception?)[] { (buffered.Body, null) }, upstream.Source));
                    }
                    else
                    {
                        reader.Dispose();
                        var reason = buffered.TooLarge ? $"响应超过 {MaxRetryResponseBodyBytes} 字节暂存上限"
                            : stats.FailureSummary() ?? stats.Outcome?.Reason ?? "未返回完整回复及有效上下文";
                        if (totalAttempts != 0 && attempt >= maxRetries)
                        {
                            ctx.Metrics.Failure(requestId);
                            return RetryExhaustedResponse(ctx, totalAttempts, lastResponse);
                        }

                        var delay = RetryDelay(attempt, status, responseHeaders);
                        ctx.Metrics.Retry(requestId, attemptNumber);
                        ctx.Metrics.RequestPhase(requestId, RequestPhase.WaitingRetry);
                        ctx.Logger.Warn($"[{requestId}] 第 {attemptNumber} 次 {method} {safePath} -> 上游 HTTP {status}，{reason}，未交给客户端，{delay:F3} 秒后代理重试{stats.FailureLogFields()}");
                        await WaitDelayAsync(delay, ctx.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                var retryable = IsRetryableStatus(status);
                if (retryable && attempt < maxRetries)
                {
                    if (expectedBodyBytes is null || expectedBodyBytes <= MaxRetryResponseBodyBytes)
                    {
                        RetryResponseBody buffered;
                        try
                        {
                            buffered = await BufferUpstreamResponseAsync(reader, requestId, startedAt, ctx.Metrics, ctx.Token).ConfigureAwait(false);
                        }
                        catch (UpstreamException failure)
                        {
                            reader.Dispose();
                            if (await HandleAttemptFailureAsync(ctx, attemptNumber, totalAttempts, status, null, startedAt, usingSystemProxy, failure).ConfigureAwait(false))
                            {
                                return RetryExhaustedResponse(ctx, totalAttempts, lastResponse);
                            }

                            continue;
                        }

                        if (!buffered.TooLarge)
                        {
                            reader.Dispose();
                            var response = new BufferedResponse(status, responseHeaders, buffered.Body);
                            ctx.Logger.Info(LogText.FormatCompletedAttempt(
                                requestId, attemptNumber, totalAttempts, method, safePath, status, buffered.FirstByteSeconds, startedAt.ElapsedSeconds, string.Empty));
                            lastResponse = response;
                            var delay = RetryDelay(attempt, status, response.Headers);
                            ctx.Metrics.Retry(requestId, attemptNumber);
                            ctx.Metrics.RequestPhase(requestId, RequestPhase.WaitingRetry);
                            ctx.Logger.Warn($"[{requestId}] 上游 HTTP {status} 可重试，{delay:F3} 秒后再次请求");
                            await WaitDelayAsync(delay, ctx.Token).ConfigureAwait(false);
                            continue;
                        }

                        reader = new ChunkReader(new ReplayChunkSource(
                            new (ReadOnlyMemory<byte>?, Exception?)[] { (buffered.Body, null), (buffered.Chunk, null) },
                            upstream.Source));
                    }

                    ctx.Logger.Warn($"[{requestId}] 上游 HTTP {status} 错误正文超过 {MaxRetryResponseBodyBytes} 字节暂存上限，改为完整流式转发，不再因本次状态码重试");
                }
                else if (retryable)
                {
                    if (_localAccessKey is null || !requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal))
                    {
                        ctx.Logger.Warn($"[{requestId}] 重试耗尽，向客户端返回最后一次上游响应 HTTP {status}");
                    }
                }

                try
                {
                    return await PrepareStreamResponseAsync(
                        ctx, reader, expectedBodyBytes, responseHeaders, attemptNumber, totalAttempts, status, usingSystemProxy, model, cacheRequest.State, keepAliveTemplate, true).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is UpstreamException or NoGenerationException)
                {
                    reader.Dispose();
                    if (await HandleAttemptFailureAsync(ctx, attemptNumber, totalAttempts, status, null, startedAt, usingSystemProxy, failure).ConfigureAwait(false))
                    {
                        return RetryExhaustedResponse(ctx, totalAttempts, lastResponse);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                reader.Dispose();
                throw;
            }
        }

        return RetryExhaustedResponse(ctx, totalAttempts, lastResponse);
    }

    private async Task<UpstreamResponse> SendUpstreamAsync(RequestContext ctx, string method, string targetUrl, HeaderList headers, ReadOnlyMemory<byte> body, bool streaming)
    {
        var timeout = TimeSpan.FromSeconds(Config.TimeoutSeconds);
        var request = new HttpRequestMessage(new HttpMethod(method), targetUrl)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        HttpContent? content = null;
        if (!body.IsEmpty)
        {
            content = new ReadOnlyMemoryContent(body);
            request.Content = content;
        }

        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // 非流式请求整体受单次超时约束；流式请求只在等响应头与每次读取上各自计时。
        var overall = streaming ? null : new CancellationTokenSource(timeout);
        using var sendCts = overall is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ctx.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ctx.Token, overall.Token);
        if (overall is null)
        {
            sendCts.CancelAfter(timeout);
        }

        HttpResponseMessage response;
        Stream stream;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendCts.Token).ConfigureAwait(false);
            stream = await response.Content.ReadAsStreamAsync(sendCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure)
        {
            overall?.Dispose();
            if (ctx.Token.IsCancellationRequested)
            {
                throw;
            }

            throw UpstreamException.Timeout(false, failure);
        }
        catch (Exception failure) when (failure is not UpstreamException)
        {
            overall?.Dispose();
            throw UpstreamException.From(failure);
        }

        var raw = new HeaderList();
        foreach (var (name, values) in response.Headers)
        {
            foreach (var value in values)
            {
                raw.Append(name, value);
            }
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            foreach (var value in values)
            {
                raw.Append(name, value);
            }
        }

        return new UpstreamResponse(
            (int)response.StatusCode,
            HeaderRules.CopyResponseHeaders(raw),
            response.Content.Headers.ContentLength,
            new HttpChunkSource(response, stream, timeout, overall));
    }

    private async Task<UpstreamResponse> SendCacheAwareAsync(RequestContext ctx, string method, string targetUrl, HeaderList headers, CacheRequestBody request, bool streaming)
    {
        while (true)
        {
            var response = await SendUpstreamAsync(ctx, method, targetUrl, headers, request.Body, streaming).ConfigureAwait(false);
            var contentType = response.Headers.Get("content-type");
            var jsonError = contentType is null || IsJsonMime(contentType);
            var encoding = response.Headers.Get("content-encoding");
            var encoded = encoding is not null && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase);
            if (request.IsAmended
                && response.Status is 400 or 422
                && jsonError
                && !encoded
                && (response.ExpectedBodyBytes is null || response.ExpectedBodyBytes <= PromptCache.MaxCacheErrorBytes))
            {
                List<(ReadOnlyMemory<byte>?, Exception?)>? replay;
                try
                {
                    replay = await ProbeCacheErrorAsync(response.Source, ctx.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    response.Source.Dispose();
                    throw;
                }

                if (replay is null)
                {
                    response.Source.Dispose();
                    _promptCache.Reject(request);
                    ctx.Metrics.CacheFallback(ctx.RequestId);
                    ctx.Logger.Info($"[{ctx.RequestId}] 上游不接受代理补充的缓存标识，使用原请求兼容重发一次；当前通道对同一接口、模型及鉴权暂停补充");
                    // reject 之后不再有补充版本，这条路径每个请求最多走一次；总等待与取消仍覆盖两次发送。
                    continue;
                }

                response.Source = new ReplayChunkSource(replay, response.Source);
            }

            return response;
        }
    }

    private static bool IsJsonMime(string contentType)
    {
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return mime == "application/json" || mime.EndsWith("+json", StringComparison.Ordinal);
    }

    /// <summary>读完上游的小错误正文；返回 null 表示上游拒绝了缓存标识，否则返回要重放的前缀。</summary>
    private static async Task<List<(ReadOnlyMemory<byte>?, Exception?)>?> ProbeCacheErrorAsync(IChunkSource source, CancellationToken token)
    {
        var prefix = new ByteBuffer();
        var reader = new ChunkReader(source);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte>? chunk;
            try
            {
                chunk = await ReadWithCancelAsync(reader, token).ConfigureAwait(false);
            }
            catch (UpstreamException failure)
            {
                // 把已读的部分正文和传输错误原样交回正常转发流程。
                return new List<(ReadOnlyMemory<byte>?, Exception?)> { (prefix.ToArray(), null), (null, failure) };
            }

            if (chunk is null)
            {
                return PromptCache.RejectsCacheKey(prefix.Span)
                    ? null
                    : new List<(ReadOnlyMemory<byte>?, Exception?)> { (prefix.ToArray(), null) };
            }

            if (chunk.Value.Length > PromptCache.MaxCacheErrorBytes - prefix.Length)
            {
                return new List<(ReadOnlyMemory<byte>?, Exception?)> { (prefix.ToArray(), null), (chunk.Value, null) };
            }

            prefix.Append(chunk.Value.Span);
        }
    }

    internal readonly struct RetryResponseBody
    {
        private RetryResponseBody(bool tooLarge, ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> chunk, double? firstByteSeconds)
        {
            TooLarge = tooLarge;
            Body = body;
            Chunk = chunk;
            FirstByteSeconds = firstByteSeconds;
        }

        public bool TooLarge { get; }

        /// <summary>完整暂存的正文；超限时是超限前的前缀。</summary>
        public ReadOnlyMemory<byte> Body { get; }

        /// <summary>超限时导致超限的那一块（未复制）。</summary>
        public ReadOnlyMemory<byte> Chunk { get; }

        public double? FirstByteSeconds { get; }

        public static RetryResponseBody Buffered(ReadOnlyMemory<byte> body, double? firstByteSeconds) => new(false, body, default, firstByteSeconds);

        public static RetryResponseBody Overflow(ReadOnlyMemory<byte> prefix, ReadOnlyMemory<byte> chunk) => new(true, prefix, chunk, null);
    }

    internal static async Task<RetryResponseBody> BufferUpstreamResponseAsync(ChunkReader reader, string requestId, MonotonicInstant startedAt, ProxyMetrics metrics, CancellationToken token)
    {
        var body = new ByteBuffer();
        double? firstByteSeconds = null;
        while (true)
        {
            var chunk = await ReadWithCancelAsync(reader, token).ConfigureAwait(false);
            if (chunk is null)
            {
                break;
            }

            if (chunk.Value.IsEmpty)
            {
                continue;
            }

            if (firstByteSeconds is null)
            {
                firstByteSeconds = startedAt.ElapsedSeconds;
                metrics.RequestPhase(requestId, RequestPhase.ReceivingResponse);
            }

            if (chunk.Value.Length > MaxRetryResponseBodyBytes - body.Length)
            {
                return RetryResponseBody.Overflow(body.ToArray(), chunk.Value);
            }

            body.Append(chunk.Value.Span);
        }

        return RetryResponseBody.Buffered(body.ToArray(), firstByteSeconds);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadWithCancelAsync(ChunkReader reader, CancellationToken token)
    {
        var task = reader.Peek();
        if (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
            if (!task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
            }
        }

        return reader.Consume(task);
    }

    private async Task<ProxyResponse> PrepareStreamResponseAsync(
        RequestContext ctx,
        ChunkReader reader,
        long? expectedBodyBytes,
        HeaderList responseHeaders,
        ulong attemptNumber,
        ulong totalAttempts,
        int status,
        bool usingSystemProxy,
        string? model,
        CacheKeyState cacheKeyState,
        KeepAliveTemplate? keepAliveTemplate,
        bool logCompletion)
    {
        var requestId = ctx.RequestId;
        var stats = new ResponseStats(responseHeaders, ctx.SafePath, model).WithCacheKeyState(cacheKeyState);
        var generationGate = status is >= 200 and < 300 && stats.IsApiEventStream ? new GenerationGate() : null;
        var generationDeadline = Deadline.AfterSeconds(Config.GenerationTimeoutSeconds);
        var prefix = new ByteBuffer();
        ulong receivedBodyBytes = 0;
        var upstreamFinished = false;
        if (generationGate is not null)
        {
            ctx.Metrics.RequestPhase(requestId, RequestPhase.WaitingGeneration);
        }

        Task? generationTimer = generationGate is null ? null : generationDeadline.WaitAsync(ctx.Token);
        ReadOnlyMemory<byte>? firstChunk = null;
        while (true)
        {
            var readTask = reader.Peek();
            if (!readTask.IsCompleted)
            {
                await (generationTimer is null
                    ? Task.WhenAny(readTask, ctx.Cancelled)
                    : Task.WhenAny(readTask, ctx.Cancelled, generationTimer)).ConfigureAwait(false);
            }

            ctx.Token.ThrowIfCancellationRequested();
            if (generationGate is not null && generationDeadline.HasPassed)
            {
                if (generationGate.Finish())
                {
                    ctx.Logger.Warn($"[{requestId}] 等待生成到期时存在未识别的消息，原样转发已收内容，不再重试");
                    break;
                }

                throw new NoGenerationException($"等待生成达到 {StreamLifecycle.Format(Config.GenerationTimeoutSeconds)} 秒，尚未向客户端转发响应{stats.FailureLogFields()}");
            }

            if (!readTask.IsCompleted)
            {
                continue;
            }

            var chunk = reader.Consume(readTask);
            if (chunk is null)
            {
                upstreamFinished = true;
                if (generationGate is not null && !generationGate.Finish())
                {
                    stats.Finish(ctx.StartedAt.ElapsedSeconds);
                    throw new NoGenerationException($"上游流在生成内容前结束，未收到完成事件，尚未向客户端转发响应{stats.FailureLogFields()}");
                }

                break;
            }

            if (chunk.Value.IsEmpty)
            {
                continue;
            }

            receivedBodyBytes = Saturating.Add(receivedBodyBytes, (ulong)chunk.Value.Length);
            stats.Observe(chunk.Value.Span, ctx.StartedAt.ElapsedSeconds);
            if (generationGate is not null)
            {
                if (chunk.Value.Length > MaxGenerationPrefixBytes - prefix.Length)
                {
                    ctx.Logger.Warn($"[{requestId}] 生成前消息超过 {MaxGenerationPrefixBytes} 字节暂存上限，改为完整流式转发，不再重试");
                }
                else if (!generationGate.Observe(chunk.Value.Span))
                {
                    prefix.Append(chunk.Value.Span);
                    continue;
                }
            }

            firstChunk = chunk;
            break;
        }

        ctx.Metrics.RequestPhase(requestId, RequestPhase.ReceivingResponse);
        var lifecycle = new StreamLifecycle(
            ctx.Logger,
            ctx.Metrics,
            requestId,
            attemptNumber,
            totalAttempts,
            ctx.Method,
            ctx.SafePath,
            status,
            ctx.StartedAt,
            stats,
            expectedBodyBytes,
            receivedBodyBytes,
            KeepAlive,
            keepAliveTemplate,
            logCompletion,
            _localAccessKey is not null && requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal) && IsRetryableStatus(status),
            ctx.Deadline,
            Config.TotalTimeoutSeconds,
            ctx.Cancel);
        if (upstreamFinished)
        {
            lifecycle.Finish();
        }
        else
        {
            lifecycle.CheckCompletion();
        }

        var prefixBytes = prefix.ToArray();
        return new ProxyResponse(
            status,
            responseHeaders,
            token => ForwardBody(ctx, reader, lifecycle, prefixBytes, firstChunk, upstreamFinished, status, usingSystemProxy, token),
            null);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ForwardBody(
        RequestContext ctx,
        ChunkReader reader,
        StreamLifecycle lifecycle,
        byte[] prefix,
        ReadOnlyMemory<byte>? firstChunk,
        bool upstreamFinished,
        int status,
        bool usingSystemProxy,
        CancellationToken deliveryToken,
        [EnumeratorCancellation] CancellationToken enumeratorToken = default)
    {
        using var readerScope = reader;
        using var lifecycleScope = lifecycle;
        var timeoutReason = lifecycle.TimeoutReason();
        if (!lifecycle.Completed && ctx.Deadline.HasPassed)
        {
            lifecycle.Interrupted(timeoutReason);
            throw new ResponseStreamException(timeoutReason);
        }

        if (prefix.Length > 0)
        {
            yield return prefix;
        }

        if (firstChunk is { } first)
        {
            yield return first;
        }

        Task? deliveryCancelled = null;
        while (!upstreamFinished && (!lifecycle.Completed || status >= 400))
        {
            var readTask = reader.Peek();
            if (!readTask.IsCompleted)
            {
                deliveryCancelled ??= Task.Delay(Timeout.Infinite, deliveryToken);
                await Task.WhenAny(readTask, deliveryCancelled).ConfigureAwait(false);
            }

            if (ctx.Cancel.IsCancellationRequested)
            {
                lifecycle.Interrupted("代理通道已停止，请求已取消");
                throw new ResponseStreamException("代理请求已取消");
            }

            if (ctx.Deadline.HasPassed)
            {
                lifecycle.Interrupted(timeoutReason);
                throw new ResponseStreamException(timeoutReason);
            }

            if (!readTask.IsCompleted)
            {
                // 只剩客户端断开这一种可能：交给 Dispose 记“客户端断开或响应未读完”。
                yield break;
            }

            ReadOnlyMemory<byte>? chunk;
            try
            {
                chunk = reader.Consume(readTask);
            }
            catch (UpstreamException failure)
            {
                lifecycle.Interrupted(NetworkErrorLabel.Describe(failure, usingSystemProxy, NetworkPhase.ReadingResponse));
                throw new ResponseStreamException(failure.Message);
            }

            if (chunk is null)
            {
                lifecycle.Finish();
                break;
            }

            if (chunk.Value.IsEmpty)
            {
                continue;
            }

            lifecycle.Observe(chunk.Value.Span);
            yield return chunk.Value;
        }

        if (status is >= 200 and < 300 && lifecycle.Stats.MissingTerminalEvent)
        {
            throw new ResponseStreamException("上游流提前结束，未收到完成事件");
        }
    }

    /// <summary>记录一次失败的尝试；返回 true 表示已到重试上限。</summary>
    private async Task<bool> HandleAttemptFailureAsync(
        RequestContext ctx,
        ulong attemptNumber,
        ulong totalAttempts,
        int? status,
        double? firstByteSeconds,
        MonotonicInstant startedAt,
        bool usingSystemProxy,
        Exception error)
    {
        var elapsed = startedAt.ElapsedSeconds;
        var phase = status is null ? NetworkPhase.AwaitingResponse : NetworkPhase.ReadingResponse;
        var label = error switch
        {
            UpstreamException network => NetworkErrorLabel.Describe(network, usingSystemProxy, phase),
            NoGenerationException generation => generation.Message,
            _ => throw error,
        };
        var statusText = status is { } value ? $"上游 HTTP {value}" : "上游状态码：无";
        double? delay = totalAttempts == 0 || attemptNumber < totalAttempts ? RetryDelay(attemptNumber - 1, null, null) : null;
        var isTemporaryKeepAlive = _localAccessKey is not null && ctx.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal);
        var retryText = delay is { } seconds ? $"将在 {seconds:F3} 秒后重试" : "已达到重试上限";
        if (delay is null && isTemporaryKeepAlive)
        {
            retryText = KeepAlive.Snapshot().Preparing ? "本轮结束，后台准备将在间隔后继续" : "本轮结束，下次按保活间隔继续";
        }
        var attemptText = isTemporaryKeepAlive ? "本轮" : totalAttempts == 0 ? $"第 {attemptNumber} 次" : $"第 {attemptNumber}/{totalAttempts} 次";
        ctx.Logger.Warn($"[{ctx.RequestId}] {attemptText} {ctx.Method} {ctx.SafePath} -> {statusText}，{label}，{retryText}，{LogText.TimingText(firstByteSeconds, elapsed)}");
        if (delay is not { } wait)
        {
            ctx.Metrics.Failure(ctx.RequestId);
            return true;
        }

        ctx.Metrics.Retry(ctx.RequestId, attemptNumber);
        ctx.Metrics.RequestPhase(ctx.RequestId, RequestPhase.WaitingRetry);
        await WaitDelayAsync(wait, ctx.Token).ConfigureAwait(false);
        return false;
    }

    private async Task WaitDelayAsync(double delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var seconds = Math.Min(Math.Max(delay, 0.0), Config.TotalTimeoutSeconds);
        if (double.IsNaN(seconds))
        {
            seconds = 0;
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
    }

    public double RetryDelay(ulong attempt, int? status, HeaderList? headers)
    {
        if (status is 429 or 503 && headers?.Get("retry-after") is { } value && ParseRetryAfter(value) is { } retryAfter)
        {
            // Retry-After 是上游要求的最短等待，抖动只能加不能减。
            return retryAfter + _randomValue() * RetryJitterSeconds;
        }

        var baseDelay = Config.BaseDelaySeconds;
        for (ulong index = 0; index < attempt; index++)
        {
            if (baseDelay == 0.0 || baseDelay >= Config.MaxDelaySeconds)
            {
                break;
            }

            baseDelay = Math.Min(baseDelay * 2.0, Config.MaxDelaySeconds);
        }

        if (baseDelay == 0.0)
        {
            return 0.0;
        }

        // 在允许区间内采样，到达上限后仍保留抖动。
        var minimum = Math.Max(baseDelay - RetryJitterSeconds, 0.0);
        var maximum = Math.Min(baseDelay + RetryJitterSeconds, Config.MaxDelaySeconds);
        return minimum + _randomValue() * (maximum - minimum);
    }

    internal static double? ParseRetryAfter(string value)
    {
        var trimmed = value.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return double.IsFinite(seconds) ? Math.Max(seconds, 0.0) : null;
        }

        if (DateTimeOffset.TryParseExact(trimmed, HttpDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            return Math.Max((date - DateTimeOffset.UtcNow).TotalSeconds, 0.0);
        }

        return null;
    }

    private ProxyResponse RetryExhaustedResponse(RequestContext ctx, ulong attempts, BufferedResponse? lastResponse)
    {
        if (lastResponse is { } response)
        {
            ctx.Logger.Warn($"[{ctx.RequestId}] 重试耗尽，返回客户端最后一次完整上游响应 HTTP {response.Status}");
            return ProxyResponse.Buffered(response.Status, response.Headers, response.Body);
        }

        var headers = new HeaderList();
        headers.Set("content-type", "application/json; charset=utf-8");
        return ProxyResponse.Buffered(502, headers, JsonBody.Error("upstream_unavailable", $"上游暂时不可用，已尝试 {attempts} 次"));
    }

    internal static bool IsRetryableStatus(int status)
    {
        return Array.IndexOf(RetryableStatusCodes, status) >= 0 || status is >= 500 and <= 599;
    }

    /// <summary>
    /// Claude Code and CCSwitch default to Bearer; some compatible providers still accept only x-api-key.
    /// Returns the other format for the current request, or null when no conversion is possible.
    /// </summary>
    private static HeaderList? ClaudeAuthenticationFallback(HeaderList headers)
    {
        var apiKey = headers.Get("x-api-key") ?? headers.Get("api-key");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var fallback = headers.Clone();
            fallback.Remove("authorization");
            fallback.Remove("x-api-key");
            fallback.Remove("api-key");
            fallback.Set("authorization", $"Bearer {apiKey}");
            return fallback;
        }

        var authorization = headers.Get("authorization");
        const string bearerPrefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[bearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        var alternate = headers.Clone();
        alternate.Remove("authorization");
        alternate.Remove("x-api-key");
        alternate.Remove("api-key");
        alternate.Set("x-api-key", token);
        return alternate;
    }

    internal static string BuildTargetUrl(string baseUrl, string path, string query)
    {
        var target = $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
        return query.Length > 0 ? $"{target}?{query}" : target;
    }
}

internal sealed class ProxyBodyException : Exception
{
    public ProxyBodyException(string message)
        : base(message)
    {
    }
}

internal static class JsonBody
{
    public static byte[] Error(string type, string message)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("type", type);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}

internal sealed class HealthPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [System.Text.Json.Serialization.JsonPropertyName("metrics")]
    public HealthMetrics Metrics { get; set; } = new();
}

internal sealed class HealthMetrics
{
    [System.Text.Json.Serialization.JsonPropertyName("statistics_date")]
    public string StatisticsDate { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("historical_unfinished_requests")]
    public ulong HistoricalUnfinishedRequests { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("restored_from_legacy_logs")]
    public bool RestoredFromLegacyLogs { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("statistics_warning")]
    public string? StatisticsWarning { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_requests")]
    public ulong TotalRequests { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("active_requests")]
    public ulong ActiveRequests { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("successful_requests")]
    public ulong SuccessfulRequests { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("retry_count")]
    public ulong RetryCount { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("failed_requests")]
    public ulong FailedRequests { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("requests")]
    public List<ActiveRequest> Requests { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("cache")]
    public CacheSnapshot Cache { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("cache_hit_rate_percent")]
    public double? CacheHitRatePercent { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("gpt_cache")]
    public CacheSnapshot GptCache { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("gpt_cache_hit_rate_percent")]
    public double? GptCacheHitRatePercent { get; set; }
}
