namespace RetryProxy.Core.Logging;

/// <summary>日志所属的功能；一键准备进入保活阶段后仍属于同一来源。</summary>
public enum LogSource
{
    ChannelProxy,
    ChannelKeepAlive,
    Preparation,
    System,
}

public static class LogSourceExtensions
{
    public static string Label(this LogSource source) => source switch
    {
        LogSource.ChannelProxy => "通道代理",
        LogSource.ChannelKeepAlive => "通道保活",
        LogSource.Preparation => "一键准备",
        _ => "系统",
    };

    public static LogSource? FromLabel(string label) => label switch
    {
        "通道代理" => LogSource.ChannelProxy,
        "通道保活" => LogSource.ChannelKeepAlive,
        "一键准备" => LogSource.Preparation,
        "系统" => LogSource.System,
        _ => null,
    };
}

/// <summary>功能内的行为，在一次问答或请求开始时确定。</summary>
public enum LogActivity
{
    Preparation,
    KeepAlive,
    Service,
}
