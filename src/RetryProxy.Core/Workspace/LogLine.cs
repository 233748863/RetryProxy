using System;
using System.Collections.Generic;
using System.Linq;

namespace RetryProxy.Core.Workspace;

/// <summary>日志面板的级别筛选（对应 ui.rs 的 LogFilter）。</summary>
public enum LogLevelFilter
{
    All,
    Info,
    Warning,
    Error,
}

public static class LogLevelFilterExtensions
{
    public static readonly LogLevelFilter[] All =
    {
        LogLevelFilter.All, LogLevelFilter.Info, LogLevelFilter.Warning, LogLevelFilter.Error,
    };

    public static string Label(this LogLevelFilter filter) => filter switch
    {
        LogLevelFilter.All => "全部",
        LogLevelFilter.Info => "信息",
        LogLevelFilter.Warning => "警告",
        _ => "错误",
    };

    /// <summary>日志行里显示的级别标签，等宽对齐以免正文起点参差不齐。</summary>
    public static string Badge(this LogLevelFilter filter) => filter switch
    {
        LogLevelFilter.Error => "ERR ",
        LogLevelFilter.Warning => "WARN",
        _ => "INFO",
    };

    public static bool Accepts(this LogLevelFilter filter, string line) => filter switch
    {
        LogLevelFilter.All => true,
        LogLevelFilter.Info => LogLine.Level(line) == LogLevelFilter.Info,
        LogLevelFilter.Warning => LogLine.Level(line) == LogLevelFilter.Warning,
        _ => LogLine.Level(line) == LogLevelFilter.Error,
    };
}

/// <summary>正文里 HTTP 状态码的着色类别。</summary>
public enum StatusColorClass
{
    /// <summary>2xx/3xx：跟随该行级别色。</summary>
    Level,

    /// <summary>4xx：警告色。</summary>
    Warning,

    /// <summary>5xx：错误色。</summary>
    Danger,
}

/// <summary>一行日志拆出的四段：时间戳、级别、标签组、正文。</summary>
public sealed class LogLineParts
{
    public string Timestamp { get; init; } = string.Empty;

    public LogLevelFilter Level { get; init; } = LogLevelFilter.Info;

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string Body { get; init; } = string.Empty;

    /// <summary>同一天的日志前 11 个字符都一样，界面只画时间部分。</summary>
    public string TimeOfDay => Timestamp.Length >= 19 ? Timestamp.Substring(11) : string.Empty;
}

/// <summary>日志行解析、筛选与状态码定位（对应 ui.rs 的 split_log_line 等函数）。</summary>
public static class LogLine
{
    public static LogLineParts Split(string line)
    {
        var trimmed = line.TrimEnd();
        // 时间戳长度固定 19 个 ASCII 字符，靠分隔符位置判断而不是搜关键字。
        var hasTimestamp = trimmed.Length > 20
            && trimmed[4] == '-'
            && trimmed[7] == '-'
            && trimmed[13] == ':'
            && trimmed[16] == ':';
        string timestamp;
        string afterTime;
        if (hasTimestamp)
        {
            timestamp = trimmed.Substring(0, 19);
            afterTime = trimmed.Substring(19).TrimStart();
        }
        else
        {
            timestamp = string.Empty;
            afterTime = trimmed;
        }

        var level = LogLevelFilter.Info;
        var afterLevel = afterTime;
        var space = afterTime.IndexOf(' ');
        if (space > 0)
        {
            var word = afterTime.Substring(0, space);
            var rest = afterTime.Substring(space + 1).TrimStart();
            switch (word)
            {
                case "ERROR":
                    level = LogLevelFilter.Error;
                    afterLevel = rest;
                    break;
                case "WARNING":
                    level = LogLevelFilter.Warning;
                    afterLevel = rest;
                    break;
                case "INFO":
                    level = LogLevelFilter.Info;
                    afterLevel = rest;
                    break;
            }
        }

        // 连续的 `[...]` 前缀就是标签组，第一个非 `[` 字符起是正文。
        var tags = new List<string>();
        var body = afterLevel;
        while (body.StartsWith('['))
        {
            var end = body.IndexOf(']');
            if (end < 0)
            {
                break;
            }

            tags.Add(body.Substring(1, end - 1));
            body = body.Substring(end + 1).TrimStart();
        }

        return new LogLineParts
        {
            Timestamp = timestamp,
            Level = level,
            Tags = tags,
            Body = body,
        };
    }

    /// <summary>解析日志行的级别，用于着色与筛选。</summary>
    public static LogLevelFilter Level(string line) => Split(line).Level;

    /// <summary>日志行是否通过当前的级别、关键字与通道筛选。</summary>
    public static bool Matches(string line, LogLevelFilter filter, string lowercaseQuery, string? route, bool keepAliveOnly = false, bool preparationOnly = false)
    {
        if (!filter.Accepts(line))
        {
            return false;
        }

        if (keepAliveOnly || preparationOnly)
        {
            var tags = Split(line).Tags;
            if (keepAliveOnly && !tags.Contains("保活")
                && (tags.Contains("准备") || !tags.Any(tag => tag.StartsWith("保活-", StringComparison.Ordinal))))
            {
                return false;
            }

            if (preparationOnly && !tags.Contains("准备"))
            {
                return false;
            }
        }

        if (route is not null && !line.Contains($"[{route}]", StringComparison.Ordinal))
        {
            return false;
        }

        return lowercaseQuery.Length == 0 || line.ToLowerInvariant().Contains(lowercaseQuery, StringComparison.Ordinal);
    }

    /// <summary>从正文里找出第一个 <c>HTTP &lt;三位状态码&gt;</c>，返回状态码与它在正文中的字符区间。</summary>
    public static (int Status, int Start, int End)? FindStatusCode(string body)
    {
        var search = 0;
        while (true)
        {
            var offset = body.IndexOf("HTTP ", search, StringComparison.Ordinal);
            if (offset < 0)
            {
                return null;
            }

            var digitsStart = offset + "HTTP ".Length;
            var digitsEnd = digitsStart;
            while (digitsEnd < body.Length && char.IsAsciiDigit(body[digitsEnd]))
            {
                digitsEnd++;
            }

            if (digitsEnd - digitsStart == 3 && int.TryParse(body.AsSpan(digitsStart, 3), out var status))
            {
                return (status, offset, digitsEnd);
            }

            search = digitsStart;
        }
    }

    public static StatusColorClass? StatusColor(int status) => status switch
    {
        >= 200 and <= 399 => StatusColorClass.Level,
        >= 400 and <= 499 => StatusColorClass.Warning,
        >= 500 and <= 599 => StatusColorClass.Danger,
        _ => null,
    };
}
