using System;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;

namespace RetryProxy.Core.Proxy;

/// <summary>
/// 一次真实请求的登记与收尾（对应 proxy.rs 的 RequestFinishGuard）。
/// 只有 <see cref="KeepAlive"/> 非空的请求才计入统计与保活计数；释放时若仍在等响应，记一次失败。
/// </summary>
internal sealed class RequestFinishGuard : IDisposable
{
    private bool _registered;
    private bool _disposed;

    public RequestFinishGuard(
        ProxyMetrics metrics,
        KeepAliveWatchdog? keepAlive,
        KeepAliveFlavor flavor,
        RouteLogger logger,
        string requestId,
        string method,
        string safePath,
        MonotonicInstant startedAt)
    {
        Metrics = metrics;
        KeepAlive = keepAlive;
        Flavor = flavor;
        Logger = logger;
        RequestId = requestId;
        Method = method;
        SafePath = safePath;
        StartedAt = startedAt;
        AwaitingResponse = true;
    }

    public ProxyMetrics Metrics { get; }

    public KeepAliveWatchdog? KeepAlive { get; set; }

    public KeepAliveFlavor Flavor { get; }

    public RouteLogger Logger { get; }

    public string RequestId { get; }

    public string Method { get; }

    public string SafePath { get; }

    public MonotonicInstant StartedAt { get; }

    public bool AwaitingResponse { get; set; }

    public void Start()
    {
        if (_registered || KeepAlive is null)
        {
            return;
        }

        Metrics.RequestStarted(RequestId, Method, SafePath);
        KeepAlive.RequestStarted(Flavor);
        _registered = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Start();
        if (KeepAlive is null)
        {
            return;
        }

        if (AwaitingResponse)
        {
            Metrics.Failure(RequestId);
            Logger.Warn($"[{RequestId}] 客户端在响应转发前断开，已取消当前请求，不再重试，耗时 {StartedAt.ElapsedSeconds:F2} 秒");
        }

        KeepAlive.RequestFinished();
        Metrics.RequestFinished(RequestId);
    }
}
