using System;

namespace RetryProxy.Core.Logging;

/// <summary>
/// 带通道名前缀的日志器。消息以 <c>[</c> 开头时前缀紧贴，否则以空格分隔。
/// </summary>
public sealed class RouteLogger
{
    private readonly ProxyLogger _logger;
    private readonly string _routeName;
    private readonly Func<string>? _logLabel;

    internal RouteLogger(ProxyLogger logger, string routeName, Func<string>? logLabel = null)
    {
        _logger = logger;
        _routeName = routeName.Trim();
        _logLabel = logLabel;
    }

    public ProxyLogger Base => _logger;

    public string RouteName => _logLabel?.Invoke() ?? _routeName;

    public void Info(string message) => _logger.Info(Prefix(message));

    public void Warn(string message) => _logger.Warn(Prefix(message));

    public void Error(string message) => _logger.Error(Prefix(message));

    private string Prefix(string message)
    {
        var routeName = RouteName;
        if (_logLabel is null && routeName != "保活"
            && (message.StartsWith("[保活-", System.StringComparison.Ordinal)
                || message.StartsWith("供应商保活", System.StringComparison.Ordinal)
                || message.StartsWith("后台准备 [会话", System.StringComparison.Ordinal)
                || message.StartsWith("自动保活 [会话", System.StringComparison.Ordinal)))
        {
            message = $"[保活]{message}";
        }

        if (routeName.Length == 0)
        {
            return message;
        }

        return message.StartsWith('[')
            ? $"[{routeName}]{message}"
            : $"[{routeName}] {message}";
    }
}
