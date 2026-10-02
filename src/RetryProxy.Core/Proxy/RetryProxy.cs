using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
using RetryProxy.Core.Tls;

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

    /// <summary>Claude 通道多久看一次是否该核对 TLS 指纹；真正的抓取仍由指纹库按 12 小时节流。</summary>
    private static readonly TimeSpan FingerprintCheckInterval = TimeSpan.FromMinutes(1);

    private static readonly string[] AuthHeaders = { "authorization", "x-api-key", "api-key" };

    /// <summary>http 上游（本机测试、本机另一个程序）。</summary>
    private readonly HttpClient _httpClient;
    /// <summary>https 上游：Claude 走 TLS 指纹连接器，Codex 走系统 TLS；两者都按客户端原顺序重排请求头。</summary>
    private readonly HttpClient _httpsClient;
    private readonly PromptCache _promptCache = new();
    private readonly ChannelState _channel;
    /// <summary>Claude 通道一律创建（默认全应用共用 <see cref="TlsFingerprintStore.Shared"/>），其他通道不触碰指纹组件。</summary>
    private readonly TlsFingerprintConnector? _tlsConnector;
    private X509Certificate2? _trustedTestCertificate;
    private TlsFingerprintStore? _tlsFingerprints;
    /// <summary>一键准备的后台临时代理：只接受带访问密钥的请求，不按通道参数重试（见 <see cref="AsPreparationProxy"/>）。</summary>
    private bool _preparationProxy;
    private long _lastRejectionLogMs;
    private int _suppressedRejections;
    private Func<double> _randomValue = () => Random.Shared.NextDouble();

    public RetryProxy(ProxyConfig config, ProxyLogger logger, ProxyMetrics metrics, CancellationToken cancel, IProxyResolver? proxyResolver = null)
    {
        config.Validate(true);
        Config = config;
        Logger = logger.Route(string.Empty);
        Metrics = metrics;
        Cancel = cancel;
        ProxyResolver = proxyResolver ?? SystemProxyResolver.Shared;
        _channel = new ChannelState(ChannelSnapshot.FromRuntime(config));
        try
        {
            // 每条通道按 http / https 各一个客户端；连接超时在连接回调里按当前快照计时，改参数后新连接立即生效。
            var plain = UpstreamHandler();
            plain.UseProxy = true;
            plain.Proxy = new ResolverWebProxy(ProxyResolver);
            plain.ConnectCallback = (context, token) => TimedConnectAsync(context, token, DialAsync);
            _httpClient = new HttpClient(plain, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

            var secure = UpstreamHandler();
            if (config.ClientType == ClientType.Claude)
            {
                // Claude 通道模拟 Claude Code 指纹（TLS 握手 + 请求头原顺序）。连接器自行处理系统代理与 TLS，
                // 请求改写成 http:// 后 HttpClient 只负责 HTTP/1.1。一律创建，切到 https 供应商时不用重建通道。
                _tlsFingerprints = TlsFingerprintStore.Shared;
                var connector = new TlsFingerprintConnector(ProxyResolver, () => _tlsFingerprints!.Current);
                _tlsConnector = connector;
                secure.UseProxy = false;
                secure.ConnectCallback = (context, token) => TimedConnectAsync(context, token, connector.ConnectAsync);
            }
            else
            {
                // Codex 在 Windows 上走 SChannel，TLS 握手与 .NET 默认一致，只需在系统 TLS 之上按 Codex 原顺序重排请求头。
                // 经 HTTP 系统代理时，到代理的 CONNECT 连接也会经过这里，隧道建好后其上跑的是 TLS 握手字节，不能套重排层。
                secure.UseProxy = true;
                secure.Proxy = new ResolverWebProxy(ProxyResolver);
                secure.ConnectCallback = (context, token) => TimedConnectAsync(context, token, DialAsync);
                secure.PlaintextStreamFilter = (context, _) => ValueTask.FromResult(context.InitialRequestMessage.Method == HttpMethod.Connect
                    ? context.PlaintextStream
                    : new HeaderOrderStream(context.PlaintextStream));
                secure.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None
                    || (_trustedTestCertificate is not null && certificate is not null && certificate.GetCertHashString() == _trustedTestCertificate.Thumbprint);
            }

            _httpsClient = new HttpClient(secure, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        catch (Exception error)
        {
            throw new ConfigException($"无法初始化上游 HTTP 客户端：{error.Message}");
        }

        KeepAlive = new KeepAliveWatchdog(config.KeepaliveEnabled, TimeSpan.FromSeconds(config.KeepaliveIdleMinutes * 60.0));
        KeepAlive.SetContextLimit((ulong)Math.Max(config.KeepaliveContextLimit, 1));
    }

    private static SocketsHttpHandler UpstreamHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
    };

    /// <summary>
    /// 建立连接限时：取当前快照的单次超时。到时抛 <see cref="TimeoutException"/>，日志归为“连上游一直连不上”。
    /// </summary>
    private async ValueTask<Stream> TimedConnectAsync(SocketsHttpConnectionContext context, CancellationToken token,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connect)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_channel.Current.TimeoutSeconds));
        try
        {
            return await connect(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("连接上游超时", error);
        }
    }

    /// <summary>与 SocketsHttpHandler 默认一致的 TCP 连接（双栈套接字，关闭 Nagle）。</summary>
    private static async ValueTask<Stream> DialAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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

    /// <summary>是否用 Claude Code 的 TLS 指纹连上游：Claude 通道且当前供应商是 https。</summary>
    public bool UsesTlsFingerprint => _tlsConnector is not null
        && _channel.Current.UpstreamBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前快照（地址、Key、参数）。</summary>
    public ChannelSnapshot Snapshot => _channel.Current;

    /// <summary>
    /// 换上新快照，之后的每次尝试都用它（PRD-供应商管理 §4.2）。换了"供应商 · Key"时，
    /// 还没向客户端输出的请求立即放弃当前尝试（含退避等待、等待生成）改用新 Key 重发，返回这类请求的个数；没换 Key 时返回 null。
    /// </summary>
    public int? UpdateSnapshot(ChannelSnapshot snapshot) => _channel.Update(snapshot);

    /// <summary>仅供测试：Codex 通道的系统 TLS 额外信任这张自签证书。</summary>
    internal RetryProxy WithTrustedTestCertificate(X509Certificate2 certificate)
    {
        _trustedTestCertificate = certificate;
        return this;
    }

    /// <summary>仅供测试：替换指纹来源，并额外信任测试证书。</summary>
    internal RetryProxy WithTlsFingerprint(TlsFingerprintStore store, X509Certificate2? trustedRoot)
    {
        if (_tlsConnector is not null)
        {
            _tlsFingerprints = store;
            _tlsConnector.TrustedRoot = trustedRoot;
        }

        return this;
    }

    /// <summary>
    /// Claude 通道在运行期间定期核对本机 Claude Code 的指纹（多个通道共用一次抓取，每 12 小时一次）。
    /// 核对常开：当前供应商不是 https（本机测试）时跳过，切到 https 供应商后一分钟内开始。
    /// </summary>
    public async Task RefreshTlsFingerprintLoopAsync(CancellationToken cancel)
    {
        if (_tlsConnector is null || _tlsFingerprints is null)
        {
            return;
        }

        var logger = Logger.WithActivity(LogActivity.Service);
        var announced = false;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (UsesTlsFingerprint)
                {
                    if (!announced)
                    {
                        announced = true;
                        logger.Info($"上游握手使用 Claude Code TLS 指纹（{_tlsFingerprints.Source}，JA4 {_tlsFingerprints.Current.Ja4}）");
                    }

                    await _tlsFingerprints.RefreshAsync(logger, cancel).ConfigureAwait(false);
                }

                await Task.Delay(FingerprintCheckInterval, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 作为一键准备的后台临时代理：只接受带 <paramref name="localAccessKey"/> 的请求（其余返回 401），并为它们注入
    /// <paramref name="apiKey"/>；Claude 请求禁止调用工具；不按通道参数重试，准备中的请求改为一直重试到拿到有效上下文。
    /// </summary>
    public RetryProxy AsPreparationProxy(string apiKey, string localAccessKey, ClaudeAuthMode authMode = ClaudeAuthMode.Bearer)
    {
        _preparationProxy = true;
        _channel.Update(_channel.Current.WithKey(apiKey, localAccessKey, authMode));
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
        var snapshot = _channel.Current;
        var timeoutSeconds = probe.IsPreparation && _preparationProxy
            ? snapshot.TotalTimeoutSeconds + 30
            : Math.Min(snapshot.TimeoutSeconds, snapshot.TotalTimeoutSeconds);
        var sessionLabel = probe.SessionId.Length > 8 ? probe.SessionId[..8] : probe.SessionId;
        var configuration = _preparationProxy ? "经后台代理"
            : probe.UsesChannelToken ? "经本通道·当前 Key"
            : probe.UsesSuppliedKey ? "经本通道·指定 Key"
            : "本机客户端配置";
        var preparing = probe.IsPreparation;
        var logger = Logger.WithActivity(preparing ? LogActivity.Preparation : LogActivity.KeepAlive);
        logger.Info($"[会话 {sessionLabel}] 第 {probe.Turn} 轮，题 {probe.QuestionIndex + 1}/{KeepAliveQuestions.Count}，{probe.Flavor.Label()} CLI，{configuration}，问：{probe.Question}");

        Cli.CliReply? reply = null;
        string? failure = null;
        var timedOut = false;
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
                timedOut = true;
                failure = $"CLI 本轮执行超过 {StreamLifecycle.Format(timeoutSeconds)} 秒，已终止并清理会话";
            }
        }

        var elapsed = startedAt.Elapsed.TotalSeconds;
        var nextRound = KeepAlive.Enabled ? $"空闲 {(long)KeepAlive.Idle.TotalSeconds}秒后下轮" : "自动保活已关闭";
        var afterFailure = KeepAlive.Snapshot().Preparing && !Cancel.IsCancellationRequested
            ? $"{KeepAliveWatchdog.PreparationRetryMinDelay.TotalSeconds:F3}～{KeepAliveWatchdog.PreparationRetryMaxDelay.TotalSeconds:F3}秒后重试"
            : nextRound;
        var prefix = $"[会话 {sessionLabel}] 第 {probe.Turn} 轮 {probe.Flavor.Label()} CLI";
        if (interruption is not null)
        {
            probe.Interrupt(interruption);
            logger.Info($"{prefix}，本轮已中断：{interruption}，总 {elapsed:F2}秒，{afterFailure}");
            return;
        }

        if (failure is not null)
        {
            probe.Fail(failure, timedOut && preparing);
            logger.Warn($"{prefix}，响应未完成：{failure}，总 {elapsed:F2}秒，{afterFailure}");
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
            logger.Info($"{prefix}，本轮已中断：{reason}，总 {elapsed:F2}秒，{afterFailure}");
            return;
        }

        var context = completion.ContextTokens?.ToString(CultureInfo.InvariantCulture) ?? "未获取";
        var reset = completion.ResetReason is { } resetReason ? $"，{resetReason}" : string.Empty;
        // 模型、token 与首字已记在代理的请求行，这里只写本轮结论，避免一次请求看起来像两次；
        // 准备转入保活由工作区的“准备完成”一行说明。例：[会话 80221d57] 第 1 轮 Codex CLI 完成，上下文 10105/50000 token，总 52.57秒，答：知道了，空闲 480秒后下轮
        logger.Info($"{prefix} 完成，上下文 {context}/{completion.ContextLimit} token，总 {elapsed:F2}秒，答：{answerPreview}{reset}，{nextRound}");
    }

    // ---------------------------------------------------------------------
    // 请求入口
    // ---------------------------------------------------------------------

    private sealed class RequestContext
    {
        private Task? _cancelled;

        public RequestContext(ProxyMetrics metrics, RouteLogger logger, CancellationToken cancel, CancellationToken token, string requestId, string method, string safePath, Deadline deadline, MonotonicInstant startedAt,
            IReadOnlyList<string> headerOrder, double totalTimeoutSeconds, bool followsSwitch)
        {
            HeaderOrder = headerOrder;
            Metrics = metrics;
            Logger = logger;
            Cancel = cancel;
            Token = token;
            RequestId = requestId;
            Method = method;
            SafePath = safePath;
            Deadline = deadline;
            StartedAt = startedAt;
            TotalTimeoutSeconds = totalTimeoutSeconds;
            FollowsSwitch = followsSwitch;
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

        public RetryLog Retries { get; } = new();

        public Task Cancelled => _cancelled ??= Task.Delay(Timeout.Infinite, Token);

        /// <summary>客户端请求头的原顺序（含 Host、Content-Length），指纹连接按它重排上游请求头。</summary>
        public IReadOnlyList<string> HeaderOrder { get; }

        /// <summary>请求开始时的总等待上限（秒）；之后改参数不影响已开始的请求。</summary>
        public double TotalTimeoutSeconds { get; }

        /// <summary>真实请求：输出前跟随切换，切换 Key 时改用新 Key 重发。保活、准备等内部请求不跟随。</summary>
        public bool FollowsSwitch { get; }
    }

    /// <summary>
    /// 按一份快照算好的上游请求（PRD-供应商管理 §9 的 BuildAttempt）：请求头、正文、目标地址等；快照版本不变时各次尝试共用。
    /// </summary>
    private sealed class AttemptPlan
    {
        public required long Version { get; init; }

        public required ChannelSnapshot Snapshot { get; init; }

        /// <summary>管理通道当前没有 Key：不转发，在本地返回错误。</summary>
        public bool MissingKey { get; init; }

        public HeaderList Headers { get; init; } = new();

        /// <summary>Claude 注入 Key 后被 401 / 403 拒绝时改用的另一种认证格式；不能转换时为 null。</summary>
        public HeaderList? AlternateHeaders { get; init; }

        public IReadOnlyList<string> HeaderOrder { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> AlternateHeaderOrder { get; init; } = Array.Empty<string>();

        public CacheRequestBody CacheRequest { get; init; } = new(ReadOnlyMemory<byte>.Empty);

        public string TargetUrl { get; init; } = string.Empty;

        public bool UsingSystemProxy { get; init; }

        /// <summary>发往上游的模型（改写后），统计与日志用。</summary>
        public string? Model { get; init; }

        public string? ReasoningEffort { get; init; }

        public string? ModelIdentity { get; init; }

        /// <summary>请求行附加的"供应商 · Key"与模型改写，例：<c>，Any · 主号，模型改写 claude-opus-5 → glm-5</c>；旧用法与测试为空。</summary>
        public string LogFields { get; init; } = string.Empty;
    }

    /// <summary>一次尝试：用哪份计划、哪种认证格式，以及“请求取消或切换 Key”都会触发的令牌。</summary>
    private sealed class AttemptScope
    {
        private readonly RequestContext _request;
        private Task? _cancelled;

        public AttemptScope(RequestContext request, AttemptPlan plan, CancellationToken token, bool alternate)
        {
            _request = request;
            Plan = plan;
            Token = token;
            Alternate = alternate;
        }

        public AttemptPlan Plan { get; }

        public CancellationToken Token { get; }

        public bool Alternate { get; }

        public HeaderList Headers => Alternate ? Plan.AlternateHeaders! : Plan.Headers;

        public IReadOnlyList<string> HeaderOrder => Alternate ? Plan.AlternateHeaderOrder : Plan.HeaderOrder;

        public Task Cancelled => Token == _request.Token ? _request.Cancelled : _cancelled ??= Task.Delay(Timeout.Infinite, Token);
    }

    /// <summary>
    /// 访问限制（PRD-供应商管理 §7）：只接受 Host 为 127.0.0.1 / localhost 的请求，拒绝带 Origin 头的请求，
    /// 防止网页借 DNS 重绑定或跨站请求使用本机端口。健康检查也受限。拒绝时返回 403，日志不记请求里的原值。
    /// </summary>
    internal async Task GuardLocalAccessAsync(HttpContext context, RequestDelegate next)
    {
        var reason = LocalAccessRejection(context.Request);
        if (reason is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        LogRejectedAccess(reason);
        await WriteSimpleResponseAsync(context, 403, "application/json; charset=utf-8",
            JsonBody.Error("forbidden", "只接受本机客户端的请求")).ConfigureAwait(false);
    }

    /// <summary>不符合访问限制时返回原因，例：Host 为 <c>evil.example:18081</c> → “Host 不是 127.0.0.1 或 localhost”。</summary>
    internal static string? LocalAccessRejection(HttpRequest request)
    {
        if (request.Headers.ContainsKey("Origin"))
        {
            return "请求带有 Origin 头（来自网页）";
        }

        var host = request.Host.Host;
        return string.Equals(host, "127.0.0.1", StringComparison.Ordinal) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Host 不是 127.0.0.1 或 localhost";
    }

    /// <summary>拒绝日志每分钟最多一条，其间的次数并入下一条，避免网页反复请求刷屏。</summary>
    private void LogRejectedAccess(string reason)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRejectionLogMs);
        if ((last != 0 && now - last < 60_000) || Interlocked.CompareExchange(ref _lastRejectionLogMs, now, last) != last)
        {
            Interlocked.Increment(ref _suppressedRejections);
            return;
        }

        var suppressed = Interlocked.Exchange(ref _suppressedRejections, 0);
        Logger.WithActivity(LogActivity.Service).Warn($"已拒绝非本机客户端的请求：{reason}，返回 HTTP 403"
            + (suppressed > 0 ? $"（此前一分钟内另有 {suppressed} 次拒绝未逐条记录）" : string.Empty));
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
        if (_preparationProxy)
        {
            if (HttpMethods.IsHead(context.Request.Method) && context.Request.Path == "/api/hello")
            {
                await WriteSimpleResponseAsync(context, 200, "application/json; charset=utf-8", Array.Empty<byte>()).ConfigureAwait(false);
                return;
            }

            if (!CarriesToken(context.Request.Headers.Authorization.ToString(), context.Request.Headers["x-api-key"].ToString(), _channel.Current.LocalToken))
            {
                await WriteSimpleResponseAsync(context, 401, "application/json; charset=utf-8",
                    JsonBody.Error("unauthorized", "后台临时代理拒绝未授权请求")).ConfigureAwait(false);
                return;
            }
        }

        // 总等待按请求开始时的参数计算，之后改参数只影响新请求。
        var totalTimeoutSeconds = _channel.Current.TotalTimeoutSeconds;
        var deadline = Deadline.AfterSeconds(totalTimeoutSeconds);
        var startedAt = MonotonicInstant.Now;
        // 当日统计日志跨进程存活，保留完整 UUID，重启后不同请求不会被旧的 32 位显示 ID 合并。
        var requestId = Guid.NewGuid().ToString("N");
        var method = context.Request.Method;
        var (safePath, rawQuery) = RawTarget(context);
        var requestHeaders = ToHeaderList(context.Request.Headers, InboundHeaderRecorder.Take(context));
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

            ctx = new RequestContext(metrics, requestLogger, cancel, token, requestId, method, safePath, deadline, startedAt, HeaderOrder(requestHeaders),
                totalTimeoutSeconds, guard.KeepAlive is not null);
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
                requestLogger.Info($"[{requestId}] 通道或后台任务已取消，不重试，总 {startedAt.ElapsedSeconds:F2}秒");
                break;
            case ProxyErrorKind.DeadlineExceeded:
                metrics.Failure(requestId);
                requestLogger.Warn($"[{requestId}] {method} {safePath} -> 请求总等待达到 {StreamLifecycle.Format(totalTimeoutSeconds)}秒，已取消，不重试，返回 HTTP 504，总 {startedAt.ElapsedSeconds:F2}秒");
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

    /// <summary>
    /// 请求是否带着本地口令（或后台临时代理的访问密钥）：<c>Authorization: Bearer {口令}</c> 或 <c>x-api-key: {口令}</c>，定长比较。
    /// 口令为空时一律不算。
    /// </summary>
    private static bool CarriesToken(string? authorization, string? apiKey, string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        const string bearer = "Bearer ";
        if (authorization is { Length: > 7 } && authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            && AccessKeyMatches(authorization[bearer.Length..].Trim(), token))
        {
            return true;
        }

        return !string.IsNullOrEmpty(apiKey) && AccessKeyMatches(apiKey.Trim(), token);
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

    /// <summary>
    /// 请求头名按首次出现的顺序去重，保留原大小写。
    /// 例：<c>Accept, Authorization, x-a, x-a, Host</c> → <c>Accept, Authorization, x-a, Host</c>。
    /// </summary>
    private static IReadOnlyList<string> HeaderOrder(HeaderList headers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (name, _) in headers)
        {
            if (seen.Add(name))
            {
                order.Add(name);
            }
        }

        return order;
    }

    /// <summary>
    /// Kestrel 的请求头转成保序列表。有原始头名记录（<see cref="InboundHeaderRecorder"/>）时按客户端原顺序与大小写排列。
    /// 例：Kestrel 给出 <c>Accept, Connection, Host</c>、原始为 <c>Host, accept, Connection</c> → 列表为 <c>Host, accept, Connection</c>；
    /// 记录里没有的头（正常不会出现）按 Kestrel 的顺序排在最后。
    /// </summary>
    private static HeaderList ToHeaderList(IHeaderDictionary headers, IReadOnlyList<string>? rawNames = null)
    {
        var list = new HeaderList();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rawNames is not null)
        {
            foreach (var name in rawNames)
            {
                if (added.Add(name) && headers.TryGetValue(name, out var values))
                {
                    AppendValues(list, name, values);
                }
            }
        }

        foreach (var (name, values) in headers)
        {
            if (!added.Contains(name))
            {
                AppendValues(list, name, values);
            }
        }

        return list;
    }

    private static void AppendValues(HeaderList list, string name, Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            if (value is not null)
            {
                list.Append(name, value);
            }
        }
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
        var baseHeaders = HeaderRules.CopyRequestHeaders(requestHeaders);
        if (_preparationProxy && Config.ClientType == ClientType.Claude
            && HttpMethods.IsPost(method) && KeepAliveFlavorExtensions.Detect(safePath) == KeepAliveFlavor.Claude)
        {
            body = HeaderRules.DisableClaudeToolUse(body);
        }

        var metadata = RequestMetadata.Parse(body);
        var pathAndQuery = rawQuery.Length > 0 ? $"{safePath}?{rawQuery}" : safePath;
        // 模板取注入前的请求头，内存里不留真实 Key。
        var keepAliveTemplate = HttpMethods.IsPost(method) ? new KeepAliveTemplate(method, pathAndQuery, baseHeaders, body) : null;
        var accept = baseHeaders.Get("accept");
        var streaming = metadata.Stream || (accept is not null && accept.ToLowerInvariant().Contains("text/event-stream", StringComparison.Ordinal));
        var requireValidContext = _preparationProxy && KeepAlive.Snapshot().Preparing
            && HttpMethods.IsPost(method) && KeepAliveFlavorExtensions.Detect(safePath) != KeepAliveFlavor.Unknown;
        // 所有真实请求在输出前跟随切换；例如旧窗口仍带旧 Key，代理也会在每次尝试中替换成当前 Key。
        var followsSwitch = ctx.FollowsSwitch;
        using var undelivered = followsSwitch ? _channel.EnterUndelivered() : null;
        AttemptPlan? plan = null;
        // Claude 注入 Key 被拒后改用另一种认证格式；换了"供应商 · Key"后重新从供应商设置的格式开始。
        var alternate = false;
        // attemptNumber 数全部尝试（日志、统计）；retries 只数计入重试上限的失败，认证格式切换与切换 Key 改投都不计。
        ulong attemptNumber = 0;
        ulong retries = 0;
        BufferedResponse? lastResponse = null;

        while (true)
        {
            var (current, version, switchToken) = _channel.Read();
            if (plan is null || plan.Version != version)
            {
                if (plan is not null && !plan.Snapshot.SameKeyAs(current))
                {
                    alternate = false;
                }

                var first = plan is null;
                plan = BuildAttempt(ctx, current, version, baseHeaders, body, metadata.Model, metadata.ModelIdentity, metadata.ReasoningEffort, streaming, rawQuery);
                if (first)
                {
                    ctx.Metrics.CacheKey(requestId, plan.CacheRequest.State);
                }
            }

            var snapshot = plan.Snapshot;
            if (plan.MissingKey)
            {
                return MissingKeyResponse(ctx, snapshot);
            }

            var maxRetries = _preparationProxy && !requireValidContext ? 0UL : (ulong)Math.Max(snapshot.MaxRetries, 0);
            var canRetry = requireValidContext || retries < maxRetries;
            attemptNumber = Saturating.Add(attemptNumber, 1);
            ctx.Metrics.RequestAttempt(requestId, attemptNumber);
            // 真实请求在输出前跟随切换：切换 Key 时本次尝试（含退避等待、等待生成）立即作废，改用新 Key 重发。
            using var attemptCts = followsSwitch ? CancellationTokenSource.CreateLinkedTokenSource(ctx.Token, switchToken) : null;
            var attempt = new AttemptScope(ctx, plan, attemptCts?.Token ?? ctx.Token, alternate && plan.AlternateHeaders is not null);
            var startedAt = MonotonicInstant.Now;
            try
            {
                UpstreamResponse upstream;
                try
                {
                    upstream = await SendCacheAwareAsync(ctx, attempt, streaming).ConfigureAwait(false);
                }
                catch (UpstreamException failure)
                {
                    if (await HandleAttemptFailureAsync(ctx, attempt, attemptNumber, canRetry, retries, null, startedAt, failure).ConfigureAwait(false))
                    {
                        return RetryExhaustedResponse(ctx, attemptNumber, lastResponse);
                    }

                    retries++;
                    continue;
                }

                var status = upstream.Status;
                var responseHeaders = upstream.Headers;
                var expectedBodyBytes = upstream.ExpectedBodyBytes;
                var reader = new ChunkReader(upstream.Source);
                try
                {
                    if (status is 401 or 403 && plan.AlternateHeaders is not null && !attempt.Alternate)
                    {
                        reader.Dispose();
                        // 认证方式切换不受配置的重试次数限制；即使 max_retries=0，也必须给另一种格式一次机会。
                        alternate = true;
                        ctx.Logger.Info($"[{requestId}] {LogText.UpstreamStatus(status)} 拒绝 Claude 鉴权，换认证格式重试一次");
                        continue;
                    }

                    if (requireValidContext)
                    {
                        RetryResponseBody buffered;
                        try
                        {
                            buffered = expectedBodyBytes > MaxRetryResponseBodyBytes
                                ? RetryResponseBody.Overflow(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty)
                                : await BufferUpstreamResponseAsync(reader, requestId, startedAt, ctx.Metrics, attempt.Token).ConfigureAwait(false);
                        }
                        catch (UpstreamException failure)
                        {
                            reader.Dispose();
                            if (await HandleAttemptFailureAsync(ctx, attempt, attemptNumber, canRetry, retries, status, startedAt, failure).ConfigureAwait(false))
                            {
                                return RetryExhaustedResponse(ctx, attemptNumber, lastResponse);
                            }

                            retries++;
                            continue;
                        }

                        var stats = new ResponseStats(responseHeaders, safePath, plan.Model, plan.ReasoningEffort, plan.ModelIdentity).WithAnswerCapture();
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
                            var summary = buffered.TooLarge ? null : stats.FailureSummary();
                            var reason = buffered.TooLarge ? $"响应超过 {MaxRetryResponseBodyBytes} 字节暂存上限"
                                : summary ?? stats.Outcome?.Reason ?? "未返回完整回复及有效上下文";
                            var delay = RetryDelay(retries, status, responseHeaders, snapshot);
                            ctx.Metrics.Retry(requestId, attemptNumber);
                            ctx.Metrics.RequestPhase(requestId, RequestPhase.WaitingRetry);
                            var failureText = summary is not null && status is < 200 or >= 300 ? LogText.UpstreamStatus(status, summary) : $"{LogText.UpstreamStatus(status)}，{reason}";
                            LogRetry(ctx, failureText, $"[{requestId}] {LogText.AttemptText(attemptNumber)} {method} {safePath} -> {failureText}，未转发{stats.FailureLogFields()}，{LogText.RetryDelayText(delay)}");
                            await WaitDelayAsync(delay, ctx.Deadline, attempt.Token).ConfigureAwait(false);
                            retries++;
                            continue;
                        }
                    }

                    var retryable = IsRetryableStatus(status);
                    if (retryable && canRetry)
                    {
                        if (expectedBodyBytes is null || expectedBodyBytes <= MaxRetryResponseBodyBytes)
                        {
                            RetryResponseBody buffered;
                            try
                            {
                                buffered = await BufferUpstreamResponseAsync(reader, requestId, startedAt, ctx.Metrics, attempt.Token).ConfigureAwait(false);
                            }
                            catch (UpstreamException failure)
                            {
                                reader.Dispose();
                                if (await HandleAttemptFailureAsync(ctx, attempt, attemptNumber, canRetry, retries, status, startedAt, failure).ConfigureAwait(false))
                                {
                                    return RetryExhaustedResponse(ctx, attemptNumber, lastResponse);
                                }

                                retries++;
                                continue;
                            }

                            if (!buffered.TooLarge)
                            {
                                reader.Dispose();
                                // 与一键准备一样解析暂存的错误正文：状态后写上游错误码对应的具体原因，并附上错误码、上游请求 ID 等诊断字段。
                                // 例：上游 HTTP 500（当前需求量高，模型负载已达上限），上游错误码 get_channel_failed，…
                                var stats = new ResponseStats(responseHeaders, safePath, plan.Model, plan.ReasoningEffort, plan.ModelIdentity);
                                stats.Observe(buffered.Body.Span, ctx.StartedAt.ElapsedSeconds);
                                stats.Finish(ctx.StartedAt.ElapsedSeconds);
                                var summary = stats.FailureSummary();
                                var response = new BufferedResponse(status, responseHeaders, buffered.Body, summary);
                                lastResponse = response;
                                var delay = RetryDelay(retries, status, response.Headers, snapshot);
                                var failureText = LogText.UpstreamStatus(status, summary);
                                LogRetry(ctx, failureText, $"[{requestId}] {LogText.AttemptText(attemptNumber)} {method} {safePath} -> {failureText}{stats.FailureLogFields()}，{LogText.RetryDelayText(delay)}");
                                ctx.Metrics.Retry(requestId, attemptNumber);
                                ctx.Metrics.RequestPhase(requestId, RequestPhase.WaitingRetry);
                                await WaitDelayAsync(delay, ctx.Deadline, attempt.Token).ConfigureAwait(false);
                                retries++;
                                continue;
                            }

                            reader = new ChunkReader(new ReplayChunkSource(
                                new (ReadOnlyMemory<byte>?, Exception?)[] { (buffered.Body, null), (buffered.Chunk, null) },
                                upstream.Source));
                        }

                        ctx.Logger.Warn($"[{requestId}] {LogText.UpstreamStatus(status)} 错误正文超过 {MaxRetryResponseBodyBytes} 字节，改为完整流式转发，本次状态码不重试");
                    }
                    else if (retryable)
                    {
                        if (!_preparationProxy || !requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal))
                        {
                            ctx.Logger.Warn($"[{requestId}] 重试耗尽，返回最后响应 HTTP {status}");
                        }
                    }

                    try
                    {
                        return await PrepareStreamResponseAsync(
                            ctx, attempt, reader, expectedBodyBytes, responseHeaders, attemptNumber, status, keepAliveTemplate, logCompletion: true, canRetry: canRetry).ConfigureAwait(false);
                    }
                    catch (Exception failure) when (failure is UpstreamException or NoGenerationException)
                    {
                        reader.Dispose();
                        if (await HandleAttemptFailureAsync(ctx, attempt, attemptNumber, canRetry, retries, status, startedAt, failure).ConfigureAwait(false))
                        {
                            return RetryExhaustedResponse(ctx, attemptNumber, lastResponse);
                        }

                        retries++;
                    }
                }
                catch (OperationCanceledException)
                {
                    reader.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (followsSwitch && switchToken.IsCancellationRequested && !ctx.Token.IsCancellationRequested)
            {
                // 旧 Key 的错误响应与重试日志节流都不再适用，改投后从头计。
                lastResponse = null;
                ctx.Retries.Reset();
                var label = _channel.Current.Label;
                ctx.Logger.Info($"[{requestId}] 改投 {(label.Length > 0 ? label : "新 Key")}，未输出，立即重发（不计重试）");
            }
        }
    }

    /// <summary>
    /// 按快照算出本次尝试的上游请求（PRD-供应商管理 §6、§7、§9）：管理通道统一注入当前 Key 并映射模型，
    /// 客户端自带的认证头全部替换；目标地址按客户端的地址规则拼接。
    /// </summary>
    private AttemptPlan BuildAttempt(RequestContext ctx, ChannelSnapshot snapshot, long version, HeaderList baseHeaders, ReadOnlyMemory<byte> body,
        string? requestModel, string? modelIdentity, string? reasoningEffort, bool streaming, string rawQuery)
    {
        // 普通通道始终有占位口令；是否匹配入站凭据不影响注入。仅未关联管理配置的基础转发实例保留原始凭据。
        var inject = snapshot.LocalToken.Length > 0 || snapshot.HasKey;
        if (inject && !snapshot.HasKey)
        {
            return new AttemptPlan { Version = version, Snapshot = snapshot, MissingKey = true };
        }

        var headers = baseHeaders.Clone();
        var order = ctx.HeaderOrder;
        var model = requestModel;
        var rewrite = string.Empty;
        // Codex 固定 Bearer；Claude 按供应商设置。
        var authMode = Config.ClientType == ClientType.Claude ? snapshot.AuthMode : ClaudeAuthMode.Bearer;
        if (inject)
        {
            headers.ReplaceAll(AuthHeaders, AuthHeaderName(authMode), AuthHeaderValue(authMode, snapshot.ApiKey));
            order = RenameAuthHeader(order, AuthHeaderName(authMode));
            if (HttpMethods.IsPost(ctx.Method))
            {
                var result = ModelRewriter.Apply(Config.ClientType, snapshot, headers, body);
                if (result.From is { } from)
                {
                    body = result.Body;
                    model = DiagnosticText.CleanModel(result.To);
                    modelIdentity = DiagnosticText.ModelIdentity(result.To);
                    rewrite = $"，模型改写 {DiagnosticText.ComparisonDisplay(DiagnosticText.CleanModel(from) ?? string.Empty)} -> {DiagnosticText.ComparisonDisplay(model ?? string.Empty)}";
                }
            }
        }

        var cacheRequest = _promptCache.Prepare(snapshot.UpstreamBaseUrl, ctx.Method, ctx.SafePath, headers, body,
            ctx.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal));
        if (streaming || model is not null)
        {
            // 客户端声明了压缩时默认改成要求上游不压缩以便解析；开启压缩透传后保留客户端的声明（其中编码须都能由代理解压）。
            // 客户端没发这个头（如 Codex）时也不补，与直连一致；上游若仍压缩，响应按 Content-Encoding 解压解析。
            var acceptEncoding = string.Join(",", headers.GetAll("accept-encoding"));
            if (acceptEncoding.Length > 0 && (!snapshot.PassThroughCompression || !ContentDecoder.AcceptsOnlySupported(acceptEncoding)))
            {
                headers.Set("accept-encoding", "identity");
            }
        }

        HeaderList? alternate = null;
        var alternateOrder = order;
        if (inject && Config.ClientType == ClientType.Claude)
        {
            // Claude Code 与多数中转站默认 Bearer，也有只认 x-api-key 的；被拒时换另一种格式再试一次。
            var other = authMode == ClaudeAuthMode.ApiKey ? ClaudeAuthMode.Bearer : ClaudeAuthMode.ApiKey;
            alternate = headers.Clone();
            alternate.ReplaceAll(AuthHeaders, AuthHeaderName(other), AuthHeaderValue(other, snapshot.ApiKey));
            alternateOrder = RenameAuthHeader(order, AuthHeaderName(other));
        }

        var targetUrl = UrlRules.UpstreamTarget(Config.ClientType, snapshot.UpstreamBaseUrl, ctx.SafePath, rawQuery);
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedTarget))
        {
            throw new ProxyBodyException("目标 URL 无效");
        }

        var label = snapshot.ProviderName.Length == 0 ? string.Empty
            : inject ? snapshot.Label
            : $"{snapshot.ProviderName} · 客户端凭据";
        return new AttemptPlan
        {
            Version = version,
            Snapshot = snapshot,
            Headers = headers,
            AlternateHeaders = alternate,
            HeaderOrder = order,
            AlternateHeaderOrder = alternateOrder,
            CacheRequest = cacheRequest,
            TargetUrl = targetUrl,
            UsingSystemProxy = ProxyResolver.Resolve(parsedTarget) is not null,
            Model = model,
            ReasoningEffort = reasoningEffort,
            ModelIdentity = modelIdentity,
            LogFields = (label.Length > 0 ? $"，{label}" : string.Empty) + rewrite,
        };
    }

    private static string AuthHeaderName(ClaudeAuthMode mode) => mode == ClaudeAuthMode.ApiKey ? "x-api-key" : "authorization";

    private static string AuthHeaderValue(ClaudeAuthMode mode, string apiKey) => mode == ClaudeAuthMode.ApiKey ? apiKey : $"Bearer {apiKey}";

    /// <summary>
    /// 在客户端的头序表里把认证头原位改名（PRD-供应商管理 §9 头序）：第一个认证头换成 <paramref name="name"/>，大小写风格跟随原头，
    /// 其余认证头删去。例：<c>accept, authorization, content-type</c> → <c>accept, x-api-key, content-type</c>；
    /// <c>Accept, Authorization</c> → <c>Accept, X-Api-Key</c>。
    /// </summary>
    internal static IReadOnlyList<string> RenameAuthHeader(IReadOnlyList<string> order, string name)
    {
        var renamed = new List<string>(order.Count);
        var replaced = false;
        foreach (var header in order)
        {
            if (!Array.Exists(AuthHeaders, auth => string.Equals(auth, header, StringComparison.OrdinalIgnoreCase)))
            {
                renamed.Add(header);
            }
            else if (!replaced)
            {
                replaced = true;
                renamed.Add(header == header.ToLowerInvariant() ? name : TitleCase(name));
            }
        }

        return renamed;
    }

    private static string TitleCase(string name) =>
        string.Join('-', name.Split('-').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>管理通道当前没有 Key：不转发，直接告诉客户端（PRD-供应商管理 §7）。</summary>
    private static ProxyResponse MissingKeyResponse(RequestContext ctx, ChannelSnapshot snapshot)
    {
        var provider = snapshot.ProviderName.Length > 0 ? $"“{snapshot.ProviderName}”" : string.Empty;
        ctx.Logger.Warn($"[{ctx.RequestId}] {ctx.Method} {ctx.SafePath} -> 当前供应商{provider}没有 Key，未转发，向客户端返回 HTTP 403");
        ctx.Metrics.Failure(ctx.RequestId);
        var headers = new HeaderList();
        headers.Set("content-type", "application/json; charset=utf-8");
        return ProxyResponse.Buffered(403, headers, JsonBody.Error("no_provider_key", $"当前供应商{provider}没有 Key，请先在 RetryProxy 中为它添加 Key"));
    }

    private async Task<UpstreamResponse> SendUpstreamAsync(RequestContext ctx, AttemptScope attempt, ReadOnlyMemory<byte> body, bool streaming)
    {
        var plan = attempt.Plan;
        var timeout = TimeSpan.FromSeconds(plan.Snapshot.TimeoutSeconds);
        var requestUri = new Uri(plan.TargetUrl);
        var secure = requestUri.Scheme == Uri.UriSchemeHttps;
        string? hostHeader = null;
        if (secure && _tlsConnector is not null)
        {
            hostHeader = TlsFingerprintConnector.HostHeader(requestUri);
            requestUri = TlsFingerprintConnector.PlainRequestUri(requestUri);
        }

        var request = new HttpRequestMessage(new HttpMethod(ctx.Method), requestUri)
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

        foreach (var (name, value) in attempt.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (hostHeader is not null)
        {
            request.Headers.Host = hostHeader;
        }

        // https 连接外层的 HeaderOrderStream 读取并删掉这个内部头，按其顺序重排请求头（HttpClient 自己会把 Host 放最前、Content-* 放最后）。
        // http 连接没有这一层，不能加，否则会原样发到上游。
        if (secure && attempt.HeaderOrder.Count > 0)
        {
            request.Headers.TryAddWithoutValidation(HeaderOrderStream.PlanHeader, string.Join(",", attempt.HeaderOrder));
        }

        // 非流式请求整体受单次超时约束；流式请求只在等响应头与每次读取上各自计时。
        var overall = streaming ? null : new CancellationTokenSource(timeout);
        using var sendCts = overall is null
            ? CancellationTokenSource.CreateLinkedTokenSource(attempt.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, overall.Token);
        if (overall is null)
        {
            sendCts.CancelAfter(timeout);
        }

        HttpResponseMessage response;
        Stream stream;
        try
        {
            response = await (secure ? _httpsClient : _httpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendCts.Token).ConfigureAwait(false);
            stream = await response.Content.ReadAsStreamAsync(sendCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure)
        {
            overall?.Dispose();
            if (attempt.Token.IsCancellationRequested)
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

    private async Task<UpstreamResponse> SendCacheAwareAsync(RequestContext ctx, AttemptScope attempt, bool streaming)
    {
        var request = attempt.Plan.CacheRequest;
        while (true)
        {
            var response = await SendUpstreamAsync(ctx, attempt, request.Body, streaming).ConfigureAwait(false);
            var contentType = response.Headers.Get("content-type");
            var jsonError = contentType is null || IsJsonMime(contentType);
            var encoding = response.Headers.Get("content-encoding");
            if (request.IsAmended
                && response.Status is 400 or 422
                && jsonError
                && (!ContentDecoder.IsEncoded(encoding) || ContentDecoder.IsSupported(encoding))
                && (response.ExpectedBodyBytes is null || response.ExpectedBodyBytes <= PromptCache.MaxCacheErrorBytes))
            {
                List<(ReadOnlyMemory<byte>?, Exception?)>? replay;
                try
                {
                    replay = await ProbeCacheErrorAsync(response.Source, encoding, attempt.Token).ConfigureAwait(false);
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
                    ctx.Logger.Info($"[{ctx.RequestId}] 上游不接受代理补充的缓存标识，原请求兼容重发一次；同接口、模型及鉴权暂停补充");
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

    /// <summary>读完上游的小错误正文；返回 null 表示上游拒绝了缓存标识，否则返回要重放的前缀（原始字节，压缩的也原样重放）。</summary>
    private static async Task<List<(ReadOnlyMemory<byte>?, Exception?)>?> ProbeCacheErrorAsync(IChunkSource source, string? contentEncoding, CancellationToken token)
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
                return ContentDecoder.DecodeAll(contentEncoding, prefix.Span) is { } plain && PromptCache.RejectsCacheKey(plain)
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
        AttemptScope attempt,
        ChunkReader reader,
        long? expectedBodyBytes,
        HeaderList responseHeaders,
        ulong attemptNumber,
        int status,
        KeepAliveTemplate? keepAliveTemplate,
        bool logCompletion,
        bool canRetry)
    {
        var requestId = ctx.RequestId;
        var plan = attempt.Plan;
        var usingSystemProxy = plan.UsingSystemProxy;
        var generationTimeoutSeconds = plan.Snapshot.GenerationTimeoutSeconds;
        var stats = new ResponseStats(responseHeaders, ctx.SafePath, plan.Model, plan.ReasoningEffort, plan.ModelIdentity).WithCacheKeyState(plan.CacheRequest.State);
        var generationGate = status is >= 200 and < 300 && stats.IsApiEventStream
            ? new GenerationGate(ContentDecoder.Create(responseHeaders.Get("content-encoding")))
            : null;
        var generationDeadline = Deadline.AfterSeconds(generationTimeoutSeconds);
        var prefix = new ByteBuffer();
        ulong receivedBodyBytes = 0;
        var upstreamFinished = false;
        if (generationGate is not null)
        {
            ctx.Metrics.RequestPhase(requestId, RequestPhase.WaitingGeneration);
        }

        // 等待生成期间也跟随切换：切换 Key 时这里抛出取消，由主循环改用新 Key 重发。
        Task? generationTimer = generationGate is null ? null : generationDeadline.WaitAsync(attempt.Token);
        ReadOnlyMemory<byte>? firstChunk = null;
        while (true)
        {
            var readTask = reader.Peek();
            if (!readTask.IsCompleted)
            {
                await (generationTimer is null
                    ? Task.WhenAny(readTask, attempt.Cancelled)
                    : Task.WhenAny(readTask, attempt.Cancelled, generationTimer)).ConfigureAwait(false);
            }

            attempt.Token.ThrowIfCancellationRequested();
            if (generationGate is not null && generationDeadline.HasPassed)
            {
                if (generationGate.Finish())
                {
                    if (!generationGate.HasRateLimitError)
                    {
                        ctx.Logger.Warn($"[{requestId}] 等待生成到期，含未知消息，原样转发，不重试");
                    }

                    break;
                }

                throw new NoGenerationException($"等待生成达到 {StreamLifecycle.Format(generationTimeoutSeconds)}秒，未转发", stats.FailureLogFields());
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
                    throw new NoGenerationException("生成前流结束，无完成事件，未转发", stats.FailureLogFields());
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
                    ctx.Logger.Warn($"[{requestId}] 生成前消息超过 {MaxGenerationPrefixBytes} 字节，改为完整流式转发，不重试");
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

        // 此处尚未创建下游响应；例如 response.created 后立即限流，可丢弃本次前缀并按原策略重试。
        // 最后一次仍原样交付错误；开闸后的正文只走 ForwardBody，不能重新进入重试循环。
        if (canRetry && generationGate is { HasRateLimitError: true })
        {
            stats.Finish(ctx.StartedAt.ElapsedSeconds);
            throw new NoGenerationException("上游请求超限，未转发", stats.FailureLogFields());
        }

        ctx.Metrics.RequestPhase(requestId, RequestPhase.ReceivingResponse);
        var lifecycle = new StreamLifecycle(
            ctx.Logger,
            ctx.Metrics,
            requestId,
            attemptNumber,
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
            _preparationProxy && requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal) && IsRetryableStatus(status),
            ctx.Deadline,
            ctx.TotalTimeoutSeconds,
            ctx.Cancel,
            plan.LogFields);
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
        // 完成事件之后若压缩流尚未收尾（gzip/zlib 尾部、br/zstd 结束块），继续转发到收尾为止，否则客户端解压会报流被截断。
        while (!upstreamFinished && (!lifecycle.Completed || status >= 400 || lifecycle.Stats.AwaitingEncodedEnd))
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

    /// <summary>
    /// 记录一次失败的尝试；<paramref name="canRetry"/> 为 false 或已无重试机会时返回 true（已到重试上限），
    /// 否则按第 <paramref name="retries"/> 次重试的间隔等待后返回 false。
    /// </summary>
    private async Task<bool> HandleAttemptFailureAsync(
        RequestContext ctx,
        AttemptScope attempt,
        ulong attemptNumber,
        bool canRetry,
        ulong retries,
        int? status,
        MonotonicInstant startedAt,
        Exception error)
    {
        var elapsed = startedAt.ElapsedSeconds;
        var phase = status is null ? NetworkPhase.AwaitingResponse : NetworkPhase.ReadingResponse;
        var (label, fields) = error switch
        {
            UpstreamException network => (NetworkErrorLabel.Describe(network, attempt.Plan.UsingSystemProxy, phase), string.Empty),
            NoGenerationException generation => (generation.Reason, generation.Fields),
            _ => throw error,
        };
        var statusText = status is { } value ? LogText.UpstreamStatus(value) : "HTTP 无";
        double? delay = canRetry ? RetryDelay(retries, null, null, attempt.Plan.Snapshot) : null;
        var isTemporaryKeepAlive = _preparationProxy && ctx.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal);
        var attemptText = isTemporaryKeepAlive ? "本轮" : LogText.AttemptText(attemptNumber);
        var failureText = $"{statusText}，{label}";
        var message = $"[{ctx.RequestId}] {attemptText} {ctx.Method} {ctx.SafePath} -> {failureText}{fields}，本次 {elapsed:F2}秒";
        if (delay is not { } wait)
        {
            var endText = !isTemporaryKeepAlive ? "已达到重试上限"
                : KeepAlive.Snapshot().Preparing ? "本轮结束，后台准备将在间隔后继续" : "本轮结束，下次按保活间隔继续";
            ctx.Logger.Warn($"{message}，{endText}");
            ctx.Metrics.Failure(ctx.RequestId);
            return true;
        }

        LogRetry(ctx, failureText, $"{message}，{LogText.RetryDelayText(wait)}");
        ctx.Metrics.Retry(ctx.RequestId, attemptNumber);
        ctx.Metrics.RequestPhase(ctx.RequestId, RequestPhase.WaitingRetry);
        await WaitDelayAsync(wait, ctx.Deadline, attempt.Token).ConfigureAwait(false);
        return false;
    }

    /// <summary>按 <see cref="RetryLog"/> 节流写一次将要重试的失败：原因变了写完整行，同一原因每 20 次写一条进度。</summary>
    private static void LogRetry(RequestContext ctx, string reason, string fullMessage)
    {
        switch (ctx.Retries.Next(reason))
        {
            case RetryLogKind.Full:
                ctx.Logger.Warn(fullMessage);
                break;
            case RetryLogKind.Progress:
                ctx.Logger.Warn(ctx.Retries.ProgressText(ctx.RequestId, reason));
                break;
        }
    }

    /// <summary>退避等待；切换 Key 时 <paramref name="token"/> 取消，等待立即结束。</summary>
    internal static async Task WaitDelayAsync(double delay, Deadline deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var seconds = double.IsNaN(delay) ? 0 : Math.Max(delay, 0.0);
        if (seconds >= deadline.Remaining.TotalSeconds)
        {
            // 例如退避 30 秒、总等待只剩 3 秒：到期必须结束请求，不能把截短等待当作重试就绪。
            // 不依赖取消回调及时调度，避免两个计时器同时到期时多发一次请求。
            await deadline.WaitAsync(token).ConfigureAwait(false);
            throw new OperationCanceledException(token);
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (deadline.HasPassed)
        {
            throw new OperationCanceledException(token);
        }
    }

    /// <summary>第 <paramref name="attempt"/> 次重试（从 0 起）前的等待秒数，按当前快照的退避间隔计算。</summary>
    public double RetryDelay(ulong attempt, int? status, HeaderList? headers) => RetryDelay(attempt, status, headers, _channel.Current);

    private double RetryDelay(ulong attempt, int? status, HeaderList? headers, ChannelSnapshot snapshot)
    {
        if (status is 429 or 503 && headers?.Get("retry-after") is { } value && ParseRetryAfter(value) is { } retryAfter)
        {
            // Retry-After 是上游要求的最短等待，抖动只能加不能减。
            return retryAfter + _randomValue() * RetryJitterSeconds;
        }

        var baseDelay = snapshot.BaseDelaySeconds;
        for (ulong index = 0; index < attempt; index++)
        {
            if (baseDelay == 0.0 || baseDelay >= snapshot.MaxDelaySeconds)
            {
                break;
            }

            baseDelay = Math.Min(baseDelay * 2.0, snapshot.MaxDelaySeconds);
        }

        if (baseDelay == 0.0)
        {
            return 0.0;
        }

        // 在允许区间内采样，到达上限后仍保留抖动。
        var minimum = Math.Max(baseDelay - RetryJitterSeconds, 0.0);
        var maximum = Math.Min(baseDelay + RetryJitterSeconds, snapshot.MaxDelaySeconds);
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
            ctx.Logger.Warn($"[{ctx.RequestId}] 重试耗尽，返回最后完整响应 {LogText.HttpStatus(response.Status, response.Summary)}");
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

    /// <summary>上游地址按客户端规则拼接，见 <see cref="UrlRules.UpstreamTarget"/>。</summary>
    internal static string BuildTargetUrl(ClientType clientType, string baseUrl, string path, string query) =>
        UrlRules.UpstreamTarget(clientType, baseUrl, path, query);
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
