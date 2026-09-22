namespace RetryProxy.Core.Logging;

/// <summary>
/// 带通道名前缀的日志器。消息以 <c>[</c> 开头时前缀紧贴，否则以空格分隔。
/// </summary>
public sealed class RouteLogger
{
    private readonly ProxyLogger _logger;
    private readonly string _routeName;

    internal RouteLogger(ProxyLogger logger, string routeName)
    {
        _logger = logger;
        _routeName = routeName.Trim();
    }

    public ProxyLogger Base => _logger;

    public string RouteName => _routeName;

    public void Info(string message) => _logger.Info(Prefix(message));

    public void Warn(string message) => _logger.Warn(Prefix(message));

    public void Error(string message) => _logger.Error(Prefix(message));

    private string Prefix(string message)
    {
        if (_routeName.Length == 0)
        {
            return message;
        }

        return message.StartsWith('[')
            ? $"[{_routeName}]{message}"
            : $"[{_routeName}] {message}";
    }
}
