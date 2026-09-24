using System;
using RetryProxy.Core.Metrics;

namespace RetryProxy.Core.Logging;

/// <summary>
/// 带固定来源、通道或任务、行为的日志器；异步请求全程使用同一个实例。
/// </summary>
public sealed class RouteLogger
{
    private readonly ProxyLogger _logger;
    private readonly LogActivity? _activity;
    private readonly string? _requestId;

    internal RouteLogger(ProxyLogger logger, string routeName, LogSource source,
        LogActivity? activity = null, string? requestId = null)
    {
        _logger = logger;
        RouteName = routeName.Trim();
        Source = source;
        _activity = activity;
        _requestId = requestId;
    }

    public ProxyLogger Base => _logger;

    public string RouteName { get; }

    public LogSource Source { get; }

    public RouteLogger WithActivity(LogActivity activity)
    {
        var source = Source == LogSource.Preparation ? Source
            : activity is LogActivity.Preparation or LogActivity.KeepAlive ? LogSource.ChannelKeepAlive : Source;
        return new RouteLogger(_logger, RouteName, source, activity, _requestId);
    }

    public RouteLogger ForRequest(string requestId, bool internalRequest, bool preparing)
    {
        var context = internalRequest || Source == LogSource.Preparation
            ? WithActivity(preparing ? LogActivity.Preparation : LogActivity.KeepAlive)
            : this;
        return new RouteLogger(_logger, RouteName, context.Source, context._activity, requestId);
    }

    public void Info(string message) => _logger.Write("INFO", Prefix(message));

    public void Warn(string message) => _logger.Write("WARNING", Prefix(message));

    public void Error(string message) => _logger.Write("ERROR", Prefix(message));

    private string Prefix(string message)
    {
        var prefix = $"[{Source.Label()}]";
        if (RouteName.Length > 0)
        {
            prefix += $"[{RouteName}]";
        }

        var activity = _activity switch
        {
            LogActivity.Preparation => "准备",
            LogActivity.KeepAlive => Source == LogSource.Preparation ? "独立保活" : "自动保活",
            LogActivity.Service => "服务",
            _ => null,
        };
        if (activity is not null)
        {
            prefix += $"[{activity}]";
        }

        if (_requestId is not null && message.StartsWith($"[{_requestId}]", StringComparison.Ordinal))
        {
            // 保活前缀只用于内部统计，界面和文件统一显示请求 ID，避免准备请求被误读成保活。
            var id = _requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal)
                ? _requestId[ProxyMetrics.KeepAlivePrefix.Length..] : _requestId;
            message = $"[请求 {id}]{message[(_requestId.Length + 2)..]}";
        }

        return message.StartsWith('[')
            ? prefix + message
            : $"{prefix} {message}";
    }
}
